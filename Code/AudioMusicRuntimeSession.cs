using System.Threading.Tasks;
namespace Smod;

/// <summary>Owns one prepared composition, native mixer, clock carrier, and source bank.
/// All public calls belong to the audio session thread. Gameplay must use event facts.</summary>
internal sealed class AudioMusicRuntimeSession : IAudioMusicEventSession
{
 enum Lifetime { New, Loading, Running, Stopping, Stopped }
 readonly AudioMusicSourceBank bank;
 readonly IAudioMusicOutput output;
 readonly Func<Task> wait;
 AudioMusicPcmSession session;
 double destinationBpm;
 Lifetime state;
 Task stopTask;
 float volume=1;
 bool paused;
 public bool IsRunning => state==Lifetime.Running;
 public bool IsStopped => state==Lifetime.Stopped;
 public long Position => session?.Position ?? 0;
 public void LimitPlaybackTo(long sample)=>session?.Processor.LimitPlaybackTo(sample);
 public Task<bool> Seek(long sample)=>IsRunning?session.Seek(sample):Task.FromResult(false);
 public int ActivePhraseVoices => session?.Processor.PublishedActiveVoices ?? 0;
 internal long PausedFrames => session?.Processor.PublishedPausedFrames ?? 0;
 internal bool ValidChannelSequence => session is not null && !session.Processor.InvalidChannelSequence && !session.Processor.UnsupportedListener;
 public long ResidentBytes => bank.ResidentBytes;
 internal int PreparedRangeCount=>bank.PreparedRangeCount;
 internal int NativeDecodeCount=>bank.NativeDecodeCount;
 public IReadOnlyList<BackingPart> BackingParts=>eventBacking?.Parts??Array.Empty<BackingPart>();
 public IReadOnlyList<float> BackingGains {get;private set;}=Array.Empty<float>();
 public AudioMusicRuntimeSession(IAudioMusicOutput output=null,Func<Task> wait=null,long maximumBytes=64L*1024*1024)
 {
  bank=new(maximumBytes);this.output=output??new SboxMusicOutput();this.wait=wait??(()=>GameTask.DelayRealtime(5));
 }
 public float Volume
 {
  get=>volume;
  set {if(!float.IsFinite(value)||value<0||value>1)throw new ArgumentOutOfRangeException(nameof(value));volume=value;if(IsRunning)output.Volume=value;}
 }
 public bool Paused
 {
  get=>paused;
  set {paused=value;if(IsRunning)session.SetPaused(value);}
 }
 AudioMusicEventBacking eventBacking;
 sealed record StartupCompound(CompoundSourceHit[] Hits,AudioTone Tone);
 StartupCompound[] startupCompounds=Array.Empty<StartupCompound>();
 AudioMusicPrewarmRange[] startupRanges=Array.Empty<AudioMusicPrewarmRange>();
 /// <summary>Use the event-selected grid and its existing world parameters. Sources stay fixed for this composition.</summary>
 public Task<bool> StartEventBackingAsync(AudioEvent resource,AudioBeatGrid grid,IReadOnlyDictionary<AudioParameter,AudioParameterValue> parameters)
 {
  if(state!=Lifetime.New)throw new InvalidOperationException("A music runtime can start only once.");
  eventBacking=new(resource,grid,parameters,includeTimeline:resource.SharedMusicOutput);
  // Prepare every authored action range before the song clock starts.
  startupRanges=AudioMusicPrewarmPlan.Build(resource);
  startupCompounds=resource.Tracks.Prepend(resource.MasterTrack).Where(t=>t?.Overlay?.Enabled==true)
   .SelectMany(t=>(t.Instruments??new()).Where(i=>i?.CompoundHits is {Count:>0})
    .SelectMany(i=>(resource.Source(i)?.Tracks??new()).Where(source=>source?.Sound is not null)
     .Select(source=>new StartupCompound(i.CompoundHits.Select(h=>new CompoundSourceHit(source.Sound,source.Offset+h.SourceOffset,h.Length,h.BeatOffset,h.Gain)).ToArray(),ReferenceEquals(t,resource.MasterTrack)?null:CopyTone(t.Tone)))))
   .ToArray();
  return StartAsync(grid,eventBacking.Parts,resource.MasterTrack.Tone);
 }
 float[] mixGainScratch;
 bool[] mixTailScratch;
 public void UpdateEventParameters(IReadOnlyDictionary<AudioParameter,AudioParameterValue> parameters,AudioMusicMixState mix=null)
 {
  if(eventBacking is null)throw new InvalidOperationException("The music session has no event backing binding.");
  if(mixGainScratch is null || mixGainScratch.Length!=eventBacking.Parts.Count)
  {mixGainScratch=new float[eventBacking.Parts.Count];mixTailScratch=new bool[eventBacking.Parts.Count];}
  eventBacking.EvaluateMix(parameters,mix,mixGainScratch,mixTailScratch);
  SetBackingGains(mixGainScratch,mixTailScratch);
 }
 internal sealed record BackingPart(AudioInstrument Instrument,SoundEventTrack Source,AudioTone TrackTone,float Gain);
 public Task<bool> StartAsync(AudioBeatGrid grid,AudioInstrument instrument,SoundEventTrack selectedSource,AudioTone masterTone,AudioTone trackTone,float backingGain)
  => StartAsync(grid,new BackingPart[]{new(instrument,selectedSource,trackTone,backingGain)},masterTone);
 public async Task<bool> StartAsync(AudioBeatGrid grid,IReadOnlyList<BackingPart> parts,AudioTone masterTone)
 {
  if(state!=Lifetime.New)throw new InvalidOperationException("A music runtime can start only once.");
  ArgumentNullException.ThrowIfNull(grid);ArgumentNullException.ThrowIfNull(parts);
  if(parts.Count<1 || parts.Count>128)throw new ArgumentOutOfRangeException(nameof(parts));
  var authored=parts.ToArray();
  foreach(var part in authored)
  {
   ArgumentNullException.ThrowIfNull(part);
   if(!float.IsFinite(part.Gain)||part.Gain<0)throw new ArgumentOutOfRangeException(nameof(parts));
  }
  var copy=new AudioBeatGrid{Name=grid.Name,Start=grid.Start,End=grid.End,BeatsPerMinute=grid.BeatsPerMinute,BeatsPerBar=grid.BeatsPerBar,BarsPerPhrase=grid.BarsPerPhrase};
  state=Lifetime.Loading;
  try
  {
   // Snapshot authoring before the first await; decode one part at a time within the bank budget.
   var master=CopyTone(masterTone);
   var inputs=authored.Select(part=>new {
    Source=part.Source.Sound,
    Range=AudioMusicSourcePreparation.ResolveRange(part.Instrument,part.Source),
    Tone=CopyTone(part.TrackTone),part.Gain,
    Echo=part.Instrument.Echo is {} fx ? new AudioEchoEffect{ReleaseOnly=fx.ReleaseOnly,DelaySeconds=fx.DelaySeconds,WetGain=fx.WetGain,Feedback=fx.Feedback,TailSeconds=fx.TailSeconds}:null,
    Start=eventBacking?.IncludesTimeline==true ? (int)Math.Round((part.Instrument.StartTime-grid.Start.Seconds)*44100) : 0,
    Envelope=eventBacking?.IncludesTimeline==true ? new AudioInstrument{FadeInDuration=part.Instrument.FadeInDuration,FadeInCurveMidpoint=part.Instrument.FadeInCurveMidpoint,FadeOutDuration=part.Instrument.FadeOutDuration,FadeOutCurveMidpoint=part.Instrument.FadeOutCurveMidpoint} : null
   }).ToArray();
   var stems=new List<AudioMusicPcmBacking.Stem>();
   foreach(var input in inputs)
   {
    var pcm=await bank.PrepareAsync(input.Source,input.Range.Offset,input.Range.Length,master,input.Tone);
    if(state!=Lifetime.Loading)return false;
    if(pcm is null)throw new InvalidOperationException("The music backing exceeds its preparation budget. "+bank.LastBudgetRejection);
    stems.Add(new(pcm,input.Gain,input.Start,input.Envelope,input.Echo));
   }
   if(!await bank.PrewarmAsync(startupRanges,master))throw new InvalidOperationException("Music action phrases exceed the preparation budget. "+bank.LastBudgetRejection);
   if(state!=Lifetime.Loading)return false;
   foreach(var compound in startupCompounds)
   {
    var hits=new List<AudioMusicPhraseComposer.Hit>();
    foreach(var hit in compound.Hits)
    {
     var pcm=await bank.PrepareAsync(hit.Source,hit.Offset,hit.Length,master,compound.Tone);
     if(state!=Lifetime.Loading)return false;
     if(pcm is null)throw new InvalidOperationException("A prewarmed music hit is unavailable. "+bank.LastBudgetRejection);
     hits.Add(new(pcm,hit.BeatOffset,hit.Gain));
    }
    if(await bank.ComposeAsync(hits,copy.BeatsPerMinute) is null)
     throw new InvalidOperationException("Music phrase composition exceeds its preparation budget. "+bank.LastBudgetRejection);
    if(state!=Lifetime.Loading)return false;
   }
   StartPrepared(copy,inputs[0].Source,stems,eventBacking?.IncludesTimeline==true?(int)Math.Round((copy.End.Seconds-copy.Start.Seconds)*44100):0);return true;
  }
  catch(OperationCanceledException) when(IsStopped){return false;}
  catch(ObjectDisposedException) when(IsStopped){return false;}
  catch {Dispose();throw;}
 }
 static AudioTone CopyTone(AudioTone tone) => tone is null ? null : new AudioTone{BassGainDb=tone.BassGainDb,BassFrequencyHz=tone.BassFrequencyHz};
 internal void StartPrepared(AudioBeatGrid grid,SoundFile source,short[] pcm,float gain)
  => StartPrepared(grid,source,new AudioMusicPcmBacking.Stem[]{new(pcm,gain)});
 internal void StartPrepared(AudioBeatGrid grid,SoundFile source,IReadOnlyList<AudioMusicPcmBacking.Stem> stems,int compositionFrames=0)
 {
  if(state is not (Lifetime.New or Lifetime.Loading))throw new InvalidOperationException("Music runtime is not awaiting a backing.");
  try
  {
   session=new(grid,backingStems:stems,repeatComposition:true,compositionFrames:compositionFrames);
   destinationBpm=grid.BeatsPerMinute;
   session.SetPaused(paused);
   output.Start(session.Processor,source,volume);
   state=Lifetime.Running;
  }
  catch {Dispose();throw;}
 }
 public void SetBackingGains(IReadOnlyList<float> gains,IReadOnlyList<bool> tails=null)
 {
  if(!IsRunning)throw new InvalidOperationException("Music is not running.");
  session.SetBackingGains(gains,tails);
  if(!BackingGains.SequenceEqual(gains))BackingGains=gains.ToArray();
 }
 public Task<short[]> PreparePhraseAsync(AudioInstrument instrument,SoundEventTrack source,AudioTone masterTone,AudioTone trackTone)
 {
  if(state is Lifetime.Stopping or Lifetime.Stopped)throw new ObjectDisposedException(nameof(AudioMusicRuntimeSession));
  return bank.PrepareAsync(instrument,source,masterTone,trackTone);
 }
 public AudioMusicPcmSession.Scheduled TrySchedule(AudioEventInstance process,short[] preparedPhrase,double quantumBeats,float gain)
  => IsRunning ? session.TrySchedule(process,preparedPhrase,quantumBeats,gain) : null;
 /// <summary>Prepare a source range for this exact process lifetime, then select a future beat.
 /// Loading never reserves a past beat and cancellation never transfers work to a restarted event.</summary>
 public Task<AudioMusicPcmSession.Scheduled> PrepareAndScheduleAsync(AudioEventInstance process,AudioInstrument instrument,SoundEventTrack source,AudioTone masterTone,AudioTone trackTone,double quantumBeats,float gain)
  => PrepareAndScheduleAsync(process,()=>bank.PrepareAsync(instrument,source,masterTone,trackTone),quantumBeats,gain,instrument?.OverlayBars,instrument?.MaximumMusicWaitBeats??0);
 internal sealed record CompoundSourceHit(SoundFile Source,float Offset,float Length,double BeatOffset,float Gain);
 /// <summary>Prepare a whole hit set before selecting its future entry beat. The song owns its tempo.</summary>
 internal Task<AudioMusicPcmSession.Scheduled> PrepareCompoundAndScheduleAsync(AudioMusicRequestOwner owner,IReadOnlyList<CompoundSourceHit> hits,double quantumBeats,float gain,IReadOnlyCollection<int> entryBars=null,int maximumWaitBeats=0,AudioTone masterTone=null,AudioTone trackTone=null,int? schedulingLeadSamples=null)
 {
  ArgumentNullException.ThrowIfNull(owner);ArgumentNullException.ThrowIfNull(hits);
  if(hits.Count is <1 or >3)throw new ArgumentException("A percussion set requires one to three hits.",nameof(hits));
  var snapshot=hits.ToArray();
  foreach(var hit in snapshot)
   if(hit is null || hit.Source is null || !float.IsFinite(hit.Offset)||hit.Offset<0 || !float.IsFinite(hit.Length)||hit.Length<=0
    || !double.IsFinite(hit.BeatOffset)||hit.BeatOffset<0||hit.BeatOffset>16 || !float.IsFinite(hit.Gain)||hit.Gain<0||hit.Gain>1)
    throw new ArgumentException("Invalid compound source range, beat offset or gain.",nameof(hits));
  var bpm=destinationBpm;var master=CopyTone(masterTone);var track=CopyTone(trackTone);
  async Task<short[]> Prepare()
  {
   var prepared=new List<AudioMusicPhraseComposer.Hit>();
   foreach(var hit in snapshot)
   {
    if(!IsRunning || owner.IsCancelled)return null;
    var pcm=await bank.PrepareAsync(hit.Source,hit.Offset,hit.Length,master,track);
    if(pcm is null || !IsRunning || owner.IsCancelled)return null;
    prepared.Add(new(pcm,hit.BeatOffset,hit.Gain));
   }
   return await bank.ComposeAsync(prepared,bpm);
  }
  return PrepareAndScheduleAsync(owner,Prepare,quantumBeats,gain,entryBars,maximumWaitBeats,schedulingLeadSamples:schedulingLeadSamples);
 }
 public Task<AudioMusicPcmSession.Scheduled> PrepareOwnedPhraseAsync(AudioMusicRequestOwner owner,AudioInstrument instrument,SoundEventTrack source,AudioTone masterTone,AudioTone trackTone,double quantumBeats,float gain,long? chainSample=null,int? schedulingLeadSamples=null)
  => instrument.CompoundHits is {Count:>0} hits
   ? PrepareCompoundAndScheduleAsync(owner,hits.Select(hit=>new CompoundSourceHit(source.Sound,source.Offset+hit.SourceOffset,hit.Length,hit.BeatOffset,hit.Gain)).ToArray(),quantumBeats,gain,instrument.OverlayBars,instrument.MaximumMusicWaitBeats,masterTone,trackTone,schedulingLeadSamples)
   : PrepareAndScheduleAsync(owner,()=>bank.PrepareAsync(instrument,source,masterTone,trackTone),quantumBeats,gain,instrument.OverlayBars,instrument.MaximumMusicWaitBeats,chainSample,schedulingLeadSamples,instrument.AllowReleaseTailAcrossLoop?(int)Math.Round(instrument.OverlayBodySeconds*44100):0);
 internal Task<AudioMusicPcmSession.Scheduled> PrepareAndScheduleAsync(AudioEventInstance process,Func<Task<short[]>> prepare,double quantumBeats,float gain,IReadOnlyCollection<int> entryBars=null,int maximumWaitBeats=0)
 {
  ArgumentNullException.ThrowIfNull(process);
  if(!IsRunning)return Task.FromResult<AudioMusicPcmSession.Scheduled>(null);
  return PrepareAndScheduleAsync(process.MusicRequestOwner,prepare,quantumBeats,gain,entryBars,maximumWaitBeats);
 }
 internal async Task<AudioMusicPcmSession.Scheduled> PrepareAndScheduleAsync(AudioMusicRequestOwner owner,Func<Task<short[]>> prepare,double quantumBeats,float gain,IReadOnlyCollection<int> entryBars=null,int maximumWaitBeats=0,long? chainSample=null,int? schedulingLeadSamples=null,int bodyFrames=0)
 {
  ArgumentNullException.ThrowIfNull(owner);ArgumentNullException.ThrowIfNull(prepare);
  if((!double.IsFinite(quantumBeats)||quantumBeats<.125))throw new ArgumentOutOfRangeException(nameof(quantumBeats));
  if(!float.IsFinite(gain)||gain<0)throw new ArgumentOutOfRangeException(nameof(gain));
  if(!IsRunning)return null;
  var allowedBars=entryBars?.ToArray();
  if(allowedBars?.Any(bar=>bar<1)==true)throw new ArgumentOutOfRangeException(nameof(entryBars));
  var target=session;
  var generation=target.TimelineGeneration;
  var deadline=chainSample ?? target.RequestDeadline(maximumWaitBeats);
  short[] pcm;
  try {pcm=await prepare();}
  catch(OperationCanceledException) when(IsStopped || owner.IsCancelled){return null;}
  catch(ObjectDisposedException) when(IsStopped || owner.IsCancelled){return null;}
  if(!IsRunning || !ReferenceEquals(target,session) || generation!=target.TimelineGeneration || owner.IsCancelled || pcm is null)return null;
  return target.TrySchedule(owner,pcm,quantumBeats,gain,allowedBars,deadline,chainSample,schedulingLeadSamples,bodyFrames);
 }
 public Task StopWithFadeAsync(float seconds)
 {
  if(!float.IsFinite(seconds)||seconds<0||seconds>60)throw new ArgumentOutOfRangeException(nameof(seconds));
  if(seconds==0)return StopAsync();
  if(stopTask is not null)return stopTask;
  if(state is Lifetime.New or Lifetime.Loading or Lifetime.Stopped){Dispose();return Task.CompletedTask;}
  state=Lifetime.Stopping;
  session.BeginFade();
  stopTask=FadeAndClose(Math.Max(1,(int)Math.Round(seconds*44100)));
  return stopTask;
 }
 async Task FadeAndClose(int frames)
 {
  try
  {
   var fade=output.FadeOutAsync(frames);
   var timeout=System.Diagnostics.Stopwatch.StartNew();
   while(!fade.IsCompleted && !IsStopped && !output.IsStopped && timeout.Elapsed.TotalSeconds<frames/44100d+1)await wait();
   // A stopped native carrier cannot render a release tail. Close its owned resources normally.
   // Keep the watchdog for a live carrier whose DSP is no longer advancing.
   if(IsStopped || output.IsStopped)return;
   if(!fade.IsCompleted)throw new TimeoutException("Music output stopped processing its fade.");
   var completed=await fade;
   if(!completed && !IsStopped)throw new InvalidOperationException("Music output fade was cancelled before completion.");
  }
  finally {Dispose();}
 }
 public Task StopAsync()
 {
  if(stopTask is not null)return stopTask;
  if(state is Lifetime.New or Lifetime.Loading or Lifetime.Stopped){Dispose();return Task.CompletedTask;}
  state=Lifetime.Stopping;
  var start=session.Position;session.CloseRequests();
  stopTask=DrainAndClose(start);return stopTask;
 }
 async Task DrainAndClose(long start)
 {
  var timeout=System.Diagnostics.Stopwatch.StartNew();
  try
  {
   // Keep the silent carrier ticking until backing and phrase release tails render.
   while(state==Lifetime.Stopping && session.Position-start<128 && timeout.Elapsed.TotalSeconds<1)await wait();
  }
  finally {Dispose();}
 }
 public void Dispose()
 {
  if(IsStopped)return;
  state=Lifetime.Stopped;session?.CloseRequests();
  try {output.Dispose();} finally {bank.Dispose();session=null;}
 }
}

internal interface IAudioMusicOutput : IDisposable
{
 float Volume {get;set;}
 bool IsStopped {get;}
 Task<bool> FadeOutAsync(int frames);
 void Start(Sandbox.Audio.AudioProcessor processor,SoundFile carrier,float volume);
}
internal sealed class SboxMusicOutput : IAudioMusicOutput
{
 Sandbox.Audio.Mixer mixer;
 SoundHandle carrier;
 readonly AudioMusicOutputGain gain=new();
 readonly Sandbox.Audio.AudioProcessor capture;
 readonly Func<Sandbox.Audio.Mixer> outputParent;
 internal SboxMusicOutput(Sandbox.Audio.AudioProcessor capture=null,Func<Sandbox.Audio.Mixer> outputParent=null) {this.capture=capture;this.outputParent=outputParent;}
 static SoundFile silentClock;
 internal Guid? MixerId=>mixer?.Id;
 internal float CarrierTime=>carrier?.Time??-1;
 internal void StopCarrierForCheck()=>carrier?.Stop();
 public bool IsStopped=>carrier is null || carrier.IsStopped;
 public float Volume {get=>gain.Volume;set=>gain.Volume=value;}
 public Task<bool> FadeOutAsync(int frames)=>gain.FadeOutAsync(frames);
 public void Start(Sandbox.Audio.AudioProcessor processor,SoundFile source,float volume)
 {
  if(mixer is not null)throw new InvalidOperationException("Native music output already exists.");
  if(source is null || !source.IsLoaded)throw new InvalidOperationException("Native music carrier must be ready before start.");
  try
  {
   mixer=(outputParent?.Invoke()??Sandbox.Audio.Mixer.Default).AddChild();mixer.Name=$"Smod music {Guid.NewGuid()}";mixer.Volume=1;gain.Volume=volume;mixer.AddProcessor(processor);mixer.AddProcessor(gain);
   if(capture is not null)mixer.AddProcessor(capture);
   silentClock ??= SoundFile.FromPcm("smod/runtime/music_clock.vsnd",new byte[4096],new SoundFile.PcmOptions{Channels=2,Rate=44100,Bits=16,Loop=true});
   carrier=Sound.PlayFile(silentClock,1);carrier.TargetMixer=mixer;carrier.SpacialBlend=0;
   // The session is local music, independent of scene camera/listener lifetime.
   carrier.ListenLocal=true;carrier.DistanceAttenuation=false;carrier.AirAbsorption=false;
   carrier.OcclusionEnabled=false;carrier.ReverbEnabled=false;
   if(carrier.IsStopped)throw new InvalidOperationException("Native music carrier failed to start.");
  }
  catch {Dispose();throw;}
 }
 public void Dispose()
 {
  gain.CancelFade();
  carrier?.Stop();carrier=null;
  if(mixer is null)return;
  mixer.ClearProcessors();mixer.Destroy();mixer=null;
 }
}
