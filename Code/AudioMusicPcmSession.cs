namespace Smod;

/// <summary>
/// Audio-owned session scheduling policy. One session-thread producer; the native
/// processor owns rendering, including an optional immutable backing loop. The caller
/// attaches Processor to a ticking music mixer. This class does not select sources
/// from gameplay values. External native voices are not controlled by session pause.
/// </summary>
internal sealed class AudioMusicPcmSession
{
 internal const int DefaultLeadSamples=2646; // 60 ms; late preparation must defer, never truncate.
	internal sealed record Scheduled( long Sample, System.Threading.Tasks.Task<bool> Admission );
	readonly AudioMusicPcmCommands commands;
	readonly AudioBeatGrid grid;
	readonly AudioMusicLoopGrid loopGrid;
	readonly HashSet<AudioMusicRequestOwner> owners = new();
	readonly int leadSamples;
	readonly AudioMusicPcmBacking backing;
	bool closed;
	internal AudioMusicPcmProcessor Processor { get; }
	public long Position => Processor.PublishedPosition;
	long timelineGeneration;
	System.Threading.Tasks.Task<bool> pendingSeek;
	internal long TimelineGeneration=>timelineGeneration;
	public System.Threading.Tasks.Task<bool> Seek(long sample)
	{
		if(closed)return System.Threading.Tasks.Task.FromResult(false);
		var request=commands.Seek(sample);timelineGeneration++;pendingSeek=request;return request;
	}

	/// <summary>Pause the owned backing and phrases together at the next audio block. Does not control unrelated native voices.</summary>
	public void SetPaused(bool paused)
	{
		if(closed) throw new InvalidOperationException("The music session is closed.");
		commands.SetPaused(paused);
	}
	public AudioMusicPcmSession( AudioBeatGrid grid, int voices = 4, int pending = 32, int leadSamples = DefaultLeadSamples, short[] backingPcm = null, float backingGain = 1f, bool repeatComposition = false, IReadOnlyList<AudioMusicPcmBacking.Stem> backingStems = null, int compositionFrames = 0 )
	{
		ArgumentNullException.ThrowIfNull( grid );
		if ( !grid.IsValid || leadSamples < 512 || leadSamples > 44100 ) throw new ArgumentOutOfRangeException( nameof( leadSamples ) );
		// Copy the grid: runtime state must not alter or follow mutations of shared authored data.
		this.grid = new AudioBeatGrid { Name = grid.Name, Start = grid.Start, End = grid.End, BeatsPerMinute = grid.BeatsPerMinute, BeatsPerBar = grid.BeatsPerBar, BarsPerPhrase = grid.BarsPerPhrase };
		this.leadSamples = leadSamples;
        if(backingPcm is not null && backingStems is not null)throw new ArgumentException("Choose one backing source form.");
        backing=backingStems is not null ? new AudioMusicPcmBacking(backingStems,compositionFrames)
         : backingPcm is null ? null : new AudioMusicPcmBacking(backingPcm,backingGain);
        if(repeatComposition)
        {
            if(backing is null)throw new ArgumentException("Repeating music requires an owned stereo backing.");
            loopGrid=new AudioMusicLoopGrid(grid,backing.FrameCount);
        }
		commands = new AudioMusicPcmCommands( pending );
		Processor = new AudioMusicPcmProcessor( new AudioMusicPcmRenderer( voices, backing: backing ), commands: commands );
	}
 public void SetBackingGains(IReadOnlyList<float> gains,IReadOnlyList<bool> tails=null)
 {
  if(closed)throw new InvalidOperationException("The music session is closed.");
  if(backing is null)throw new InvalidOperationException("The music session has no backing.");
  backing.SetGains(gains,tails);
 }
 internal long? RequestDeadline(int maximumWaitBeats)
 {
  if(maximumWaitBeats<0 || maximumWaitBeats>64)throw new ArgumentOutOfRangeException(nameof(maximumWaitBeats));
  if(maximumWaitBeats==0)return null;
  var frames=maximumWaitBeats*grid.SecondsPerBeat*44100;
  return frames>=long.MaxValue-Position ? long.MaxValue : Position+(long)Math.Floor(frames);
 }
	/// <summary>Audio engine supplies an admitted immutable stereo 44100 Hz phrase. Null means no eligible boundary, session closed, or bounded queue/owner capacity reached.</summary>
	public Scheduled TrySchedule( AudioEventInstance process, short[] pcm, double quantumBeats, float gain, IReadOnlyCollection<int> entryBars=null )
	{
		ArgumentNullException.ThrowIfNull( process ); ArgumentNullException.ThrowIfNull( pcm );
		if ( closed ) return null;
		return TrySchedule(process.MusicRequestOwner,pcm,quantumBeats,gain,entryBars);
	}
	// Keep the owner captured before asynchronous preparation. Never resolve a restarted event here.
	internal Scheduled TrySchedule(AudioMusicRequestOwner owner,short[] pcm,double quantumBeats,float gain,IReadOnlyCollection<int> entryBars=null,long? latestSample=null,long? earliestSample=null,int? schedulingLeadSamples=null,int bodyFrames=0)
	{
		ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(pcm);
		if(closed || owner.IsCancelled || (pendingSeek is not null && (!pendingSeek.IsCompletedSuccessfully || !pendingSeek.Result)))return null;
		if ( pcm.Length == 0 || pcm.Length % 2 != 0 ) throw new ArgumentException( "Expected stereo PCM.", nameof( pcm ) );
        if(bodyFrames<0 || bodyFrames>pcm.Length/2)throw new ArgumentOutOfRangeException(nameof(bodyFrames));
		var margin=schedulingLeadSamples ?? leadSamples;
        if(margin<512 || margin>44100)throw new ArgumentOutOfRangeException(nameof(schedulingLeadSamples));
        var earliest=Math.Max(checked(Position+margin),earliestSample ?? 0);
        var sample=loopGrid is null ? AudioMusicPhraseGrid.Next(grid,earliest,quantumBeats,pcm.Length/2,entryBars) : loopGrid.Next(earliest,quantumBeats,pcm.Length/2,entryBars,bodyFrames);
        if(sample is null || (latestSample.HasValue && sample.Value>latestSample.Value) || (loopGrid is null && sample.Value+pcm.Length/2d>grid.End.Seconds*44100))return null;
		owners.RemoveWhere( item => item.IsCancelled );
		if ( !owners.Contains( owner ) && owners.Count >= 256 ) return null;
		if ( !owner.CanBindSession( this ) ) return null;
		var request = commands.TryQueue( owner, pcm, sample.Value, gain );
		if ( request is null ) return null;
		owner.TryBindSession( this );
		owners.Add( owner );
		return new Scheduled( sample.Value, request.Admission );
	}
	/// <summary>Reject new and queued phrases without cutting the backing or sounding phrases.</summary>
	public void BeginFade() => commands.BeginFade();
	/// <summary>Reject future/pending requests and release voices owned by this session. The native owner must still detach the processor after its release tail.</summary>
	public void CloseRequests()
	{
		if ( closed ) return;
		closed = true; commands.Close(); commands.SetPaused(false); backing?.Cancel();
		foreach ( var owner in owners ) owner.Cancel();
		owners.Clear();
	}
}


