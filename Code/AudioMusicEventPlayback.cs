using System.Threading.Tasks;
namespace Smod;

/// <summary>One event's shared-backing lifetime. Call on the event thread.
/// Source completion belongs only to the generation that started it.</summary>
internal sealed class AudioMusicEventPlayback : IDisposable
{
 internal const long DefaultPreparationBudgetBytes=128L*1024*1024;
 readonly AudioEvent resource;
 readonly Func<IAudioMusicEventSession> create;
 IAudioMusicEventSession current,readySession;
 readonly HashSet<IAudioMusicEventSession> retiring=new();
 void DisposeRetiring(){foreach(var session in retiring.ToArray())session.Dispose();retiring.Clear();}
 sealed class StartupSeek
 {
  public long Sample;
  public readonly TaskCompletionSource<bool> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
 }
 StartupSeek startupSeek;
 void CancelStartup()
 {
  startupSeek?.Completion.TrySetResult(false);startupSeek=null;readySession=null;
 }
 IReadOnlyDictionary<AudioParameter,AudioParameterValue> parameters;
 AudioMusicMixState mix;
 float volume=1;
 bool paused,disposed;
 public AudioMusicEventPlayback(AudioEvent resource,Func<IAudioMusicEventSession> create=null)
 {
  ArgumentNullException.ThrowIfNull(resource);this.resource=resource;
  this.create=create??(()=>new AudioMusicRuntimeSession(maximumBytes:DefaultPreparationBudgetBytes));
 }
 public bool IsRunning=>current is not null && ReferenceEquals(current,readySession) && current.IsRunning;
 /// <summary>The current session's published sample clock; no wall-clock estimate.</summary>
 public long Position=>IsRunning?current.Position:0;
 internal IReadOnlyList<AudioMusicRuntimeSession.BackingPart> BackingParts=>current?.BackingParts??Array.Empty<AudioMusicRuntimeSession.BackingPart>();
 internal IReadOnlyList<float> BackingGains=>current?.BackingGains??Array.Empty<float>();
 public Task<bool> Seek(long sample)
 {
  if(sample<0 || sample>long.MaxValue-8192)throw new ArgumentOutOfRangeException(nameof(sample));
  if(IsRunning)return current.Seek(sample);
  if(disposed || current is null)return Task.FromResult(false);
  startupSeek?.Completion.TrySetResult(false);
  startupSeek=new StartupSeek{Sample=sample};return startupSeek.Completion.Task;
 }
 /// <summary>Submit a process phrase to this song generation. The runtime owns source preparation,
 /// beat selection and cancellation; a replaced song never accepts an old completion.</summary>
 public async Task<AudioMusicPcmSession.Scheduled> PrepareAndScheduleAsync(AudioEventInstance process,AudioInstrument instrument,SoundEventTrack source,AudioTone masterTone,AudioTone trackTone,int quantumBeats,float gain)
 {
  ArgumentNullException.ThrowIfNull(process);
  ArgumentNullException.ThrowIfNull(instrument);
  ArgumentNullException.ThrowIfNull(source);
  if(quantumBeats<1)throw new ArgumentOutOfRangeException(nameof(quantumBeats));
  if(!float.IsFinite(gain)||gain<0||gain>1)throw new ArgumentOutOfRangeException(nameof(gain));
  var session=current;
  if(disposed || !IsRunning)return null;
  var owner=process.MusicRequestOwner;
  var scheduled=await session.PrepareAndScheduleAsync(process,instrument,source,masterTone,trackTone,quantumBeats,gain);
  return !disposed && ReferenceEquals(current,session) && session.IsRunning && !owner.IsCancelled ? scheduled : null;
 }
 internal async Task<AudioMusicPcmSession.Scheduled> PrepareOwnedPhraseAsync(AudioMusicRequestOwner owner,AudioInstrument instrument,SoundEventTrack source,AudioTone masterTone,AudioTone trackTone,int quantumBeats,float gain)
 {
  var session=current;if(disposed || !IsRunning || owner.IsCancelled)return null;
  var result=await session.PrepareOwnedPhraseAsync(owner,instrument,source,masterTone,trackTone,quantumBeats,gain);
  return !disposed && ReferenceEquals(current,session) && session.IsRunning && !owner.IsCancelled ? result : null;
 }
 public float Volume
 {
  get=>volume;
  set {if(!float.IsFinite(value)||value<0||value>1)throw new ArgumentOutOfRangeException(nameof(value));volume=value;if(IsRunning)current.Volume=value;}
 }
 public bool Paused {get=>paused;set {paused=value;if(IsRunning)current.Paused=value;}}
 public void UpdateMix(AudioMusicMixState value)
 {
  mix=value??throw new ArgumentNullException(nameof(value));
  if(IsRunning)current.UpdateEventParameters(parameters,mix);
 }
 public void UpdateParameters(IReadOnlyDictionary<AudioParameter,AudioParameterValue> values)
 {
  ArgumentNullException.ThrowIfNull(values);
  parameters=values.ToDictionary(item=>item.Key,item=>item.Value);
  if(IsRunning)current.UpdateEventParameters(parameters,mix);
 }
 public async Task<bool> StartAsync(AudioBeatGrid grid,IReadOnlyDictionary<AudioParameter,AudioParameterValue> values,long initialSample=0)
 {
  if(disposed)throw new ObjectDisposedException(nameof(AudioMusicEventPlayback));
  if(initialSample<0 || initialSample>long.MaxValue-8192)throw new ArgumentOutOfRangeException(nameof(initialSample));
  CancelStartup();DisposeRetiring();
  // A replacement owns no output until the prior generation is detached.
  var old=current;current=null;old?.Dispose();
  UpdateParameters(values);
  var session=create();current=session;startupSeek=new StartupSeek{Sample=initialSample};
  try
  {
   session.Volume=0;session.Paused=true;
   var started=await session.StartEventBackingAsync(resource,grid,parameters);
   if(disposed || !ReferenceEquals(current,session))return false;
   if(!started){current=null;CancelStartup();session.Dispose();return false;}
   bool moved=false;
   while(true)
   {
    var target=startupSeek;
    var applied=(!moved && target.Sample==0) || await session.Seek(target.Sample);
    if(disposed || !ReferenceEquals(current,session))return false;
    moved=true;
    if(!ReferenceEquals(target,startupSeek))continue;
    if(!applied){current=null;CancelStartup();session.Dispose();return false;}
    break;
   }
   // Facts may have changed during preparation. Apply the latest copied set.
   session.UpdateEventParameters(parameters,mix);
   readySession=session;session.Paused=paused;session.Volume=volume;
   startupSeek.Completion.TrySetResult(true);startupSeek=null;
   return true;
  }
  catch
  {
   if(ReferenceEquals(current,session)){current=null;CancelStartup();}
   session.Dispose();throw;
  }
 }
 public async Task StopAsync(float fadeSeconds=0)
 {
  if(!float.IsFinite(fadeSeconds)||fadeSeconds<0||fadeSeconds>60)throw new ArgumentOutOfRangeException(nameof(fadeSeconds));
  var session=current;current=null;CancelStartup();
  if(session is null)return;
  retiring.Add(session);
  try {if(fadeSeconds>0)await session.StopWithFadeAsync(fadeSeconds);else await session.StopAsync();}
  finally {retiring.Remove(session);session.Dispose();}
 }
 public void Dispose()
 {
  if(disposed)return;
  disposed=true;var session=current;current=null;CancelStartup();session?.Dispose();DisposeRetiring();
 }
}
internal interface IAudioMusicEventSession : IDisposable
{
 IReadOnlyList<AudioMusicRuntimeSession.BackingPart> BackingParts {get;}
 IReadOnlyList<float> BackingGains {get;}
 bool IsRunning {get;}
 long Position {get;}
 Task<bool> Seek(long sample);
 float Volume {get;set;}
 bool Paused {get;set;}
 Task<bool> StartEventBackingAsync(AudioEvent resource,AudioBeatGrid grid,IReadOnlyDictionary<AudioParameter,AudioParameterValue> parameters);
 void UpdateEventParameters(IReadOnlyDictionary<AudioParameter,AudioParameterValue> parameters,AudioMusicMixState mix=null);
 Task<AudioMusicPcmSession.Scheduled> PrepareAndScheduleAsync(AudioEventInstance process,AudioInstrument instrument,SoundEventTrack source,AudioTone masterTone,AudioTone trackTone,int quantumBeats,float gain);
 Task<AudioMusicPcmSession.Scheduled> PrepareOwnedPhraseAsync(AudioMusicRequestOwner owner,AudioInstrument instrument,SoundEventTrack source,AudioTone masterTone,AudioTone trackTone,int quantumBeats,float gain);
 Task StopAsync();
 Task StopWithFadeAsync(float seconds);
}
