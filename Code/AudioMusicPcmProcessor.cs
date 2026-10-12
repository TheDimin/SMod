namespace Smod;

/// <summary>
/// Native stereo bridge for one session/listener. Configure the core before attaching.
/// A session command bridge must own later changes; callers must not mutate the core
/// from the main thread while this processor is attached.
/// </summary>
internal sealed class AudioMusicPcmProcessor : Sandbox.Audio.AudioProcessor<AudioMusicPcmProcessor.State>
{
	internal sealed class State : ListenerState { }
	readonly AudioMusicPcmRenderer renderer;
	readonly AudioMusicPcmCommands commands;
	readonly float[] block;
	State listener;
	int preparedFrames;
	bool prepared;
	long publishedPosition;
	int publishedActiveVoices;
	public int PublishedActiveVoices => System.Threading.Interlocked.CompareExchange(ref publishedActiveVoices,0,0);
	long publishedPausedFrames;
	bool blockPaused;
	public long PublishedPausedFrames => System.Threading.Interlocked.Read(ref publishedPausedFrames);
	public long PublishedPosition => System.Threading.Interlocked.Read( ref publishedPosition );
	public AudioMusicPcmProcessor( AudioMusicPcmRenderer renderer, int maximumBlockFrames = 2048, AudioMusicPcmCommands commands = null )
	{
		ArgumentNullException.ThrowIfNull( renderer );
		if ( maximumBlockFrames < 1 || maximumBlockFrames > 8192 ) throw new ArgumentOutOfRangeException( nameof( maximumBlockFrames ) );
		this.renderer = renderer; this.commands = commands; block = new float[maximumBlockFrames * 2];
	}
	internal void LimitPlaybackTo(long sample)=>renderer.LimitPlaybackTo(sample);
 public bool UnsupportedListener { get; private set; }
	public bool InvalidChannelSequence { get; private set; }
	protected override void ProcessSingleChannel( Sandbox.Audio.AudioChannel channel, Span<float> input )
	{
		listener ??= CurrentState;
		if ( !ReferenceEquals( listener, CurrentState ) ) { UnsupportedListener = true; return; }
		MixChannel( channel.Get(), input );
	}
	// Kept separate from the engine callback for deterministic block-boundary coverage.
	internal void MixChannel( int channel, Span<float> input )
	{
		if ( channel > 1 ) return;
		if ( channel < 0 || input.Length * 2 > block.Length ) { InvalidChannelSequence = true; return; }
		if ( channel == 0 )
		{
			if ( prepared ) { InvalidChannelSequence = true; return; }
			commands?.Drain( renderer );
			blockPaused = renderer.Paused;
			var stereo = block.AsSpan( 0, input.Length * 2 ); stereo.Clear(); renderer.Render( stereo );
			preparedFrames = input.Length; prepared = true;
		}
		else if ( !prepared || preparedFrames != input.Length ) { InvalidChannelSequence = true; return; }
		for ( var i = 0; i < input.Length; i++ ) input[i] += block[i * 2 + channel];
		if ( channel == 1 ) { if(blockPaused) System.Threading.Interlocked.Add(ref publishedPausedFrames, preparedFrames); prepared = false; System.Threading.Interlocked.Exchange(ref publishedActiveVoices,renderer.ActiveCount); System.Threading.Interlocked.Exchange( ref publishedPosition, renderer.Position ); commands?.CompleteStereoBlock(); }
	}
}


