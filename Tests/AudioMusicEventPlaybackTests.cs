using Smod;
using System.Threading.Tasks;
public sealed partial class AudioEventTests
{
 sealed class EventMusicSessionFake : IAudioMusicEventSession
 {
  public TaskCompletionSource<bool> FadeGate;
  public float FadeSeconds;
  public Task StopWithFadeAsync(float seconds){FadeSeconds=seconds;return FadeGate?.Task??StopAsync();}
  public readonly TaskCompletionSource<bool> Loaded=new();
  public IReadOnlyList<AudioMusicRuntimeSession.BackingPart> BackingParts=>Array.Empty<AudioMusicRuntimeSession.BackingPart>();
  public IReadOnlyList<float> BackingGains=>Array.Empty<float>();
  public bool IsRunning{get;private set;}
  public long Position{get;set;}
  public TaskCompletionSource<bool> SeekGate;
  public int SeekCalls;
  public Task<bool> Seek(long sample){SeekCalls++;Position=sample;return SeekGate?.Task??Task.FromResult(IsRunning);}
  public readonly TaskCompletionSource<AudioMusicPcmSession.Scheduled> Phrase=new();
  public int PhraseCalls;
  public AudioEventInstance RequestedProcess;
  public Task<AudioMusicPcmSession.Scheduled> PrepareAndScheduleAsync(AudioEventInstance process,AudioInstrument instrument,SoundEventTrack source,AudioTone masterTone,AudioTone trackTone,int quantumBeats,float gain)
  {PhraseCalls++;RequestedProcess=process;return Phrase.Task;}
  public AudioMusicRequestOwner RequestedOwner;
  public Func<AudioMusicRequestOwner,Task<AudioMusicPcmSession.Scheduled>> PhraseFactory;
  public Task<AudioMusicPcmSession.Scheduled> PrepareOwnedPhraseAsync(AudioMusicRequestOwner owner,AudioInstrument instrument,SoundEventTrack source,AudioTone masterTone,AudioTone trackTone,int quantumBeats,float gain)
  {RequestedOwner=owner;PhraseCalls++;return PhraseFactory?.Invoke(owner)??Phrase.Task;}
  public float Volume{get;set;}
  public bool Paused{get;set;}
  public int Disposals,Updates;
  public AudioMusicMixState LastMix;
  public IReadOnlyDictionary<AudioParameter,AudioParameterValue> LastParameters;
  public async Task<bool> StartEventBackingAsync(AudioEvent resource,AudioBeatGrid grid,IReadOnlyDictionary<AudioParameter,AudioParameterValue> parameters)
  {LastParameters=parameters;var result=await Loaded.Task;IsRunning=result&&Disposals==0;return result;}
  public void UpdateEventParameters(IReadOnlyDictionary<AudioParameter,AudioParameterValue> parameters,AudioMusicMixState mix=null){Updates++;LastParameters=parameters;LastMix=mix;}
  public Task StopAsync(){Dispose();return Task.CompletedTask;}
  public void Dispose(){if(Disposals==0)Disposals++;IsRunning=false;FadeGate?.TrySetResult(false);}
 }
 [TestMethod] public async Task MusicEventOwner_ProcessRequestsUseCurrentSessionClockAndRejectRetiredSong()
 {
  var first=new EventMusicSessionFake();var second=new EventMusicSessionFake();
  var sessions=new Queue<EventMusicSessionFake>(new[]{first,second});
  using var owner=new AudioMusicEventPlayback(CreateEvent(),()=>sessions.Dequeue());
  var process=AudioSystem.GetEvent(CreateEvent()).CreateInstance();process.Start();
  var grid=new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100};
  var facts=new Dictionary<AudioParameter,AudioParameterValue>();
  var instrument=new AudioInstrument();var source=new SoundEventTrack();
  try
  {
   var starting=owner.StartAsync(grid,facts);
   Assert.IsNull(await owner.PrepareAndScheduleAsync(process,instrument,source,null,null,1,.2f));
   Assert.AreEqual(0,first.PhraseCalls);Assert.AreEqual(0L,owner.Position);
   first.Loaded.SetResult(true);await starting;first.Position=44100;
   Assert.AreEqual(44100L,owner.Position);
   var oldRequest=owner.PrepareAndScheduleAsync(process,instrument,source,null,null,1,.2f);
   Assert.AreSame(process,first.RequestedProcess);
   var replacement=owner.StartAsync(grid,facts);second.Loaded.SetResult(true);await replacement;
   second.Position=22050;Assert.AreEqual(22050L,owner.Position);
   first.Phrase.SetResult(new(52920,Task.FromResult(true)));
   Assert.IsNull(await oldRequest,"Retired song results must not become current feedback.");
   var activeRequest=owner.PrepareAndScheduleAsync(process,instrument,source,null,null,1,.2f);
   var accepted=new AudioMusicPcmSession.Scheduled(26460,Task.FromResult(true));second.Phrase.SetResult(accepted);
   Assert.AreSame(accepted,await activeRequest);
   await owner.StopAsync();Assert.AreEqual(0L,owner.Position);
   Assert.IsNull(await owner.PrepareAndScheduleAsync(process,instrument,source,null,null,1,.2f));
  }
  finally {process.Stop(AudioStopMode.Immediate);process.Release();}
 }
 [TestMethod] public async Task MusicEventOwner_ProcessRestartRejectsItsOldPhraseCompletion()
 {
  var session=new EventMusicSessionFake();using var owner=new AudioMusicEventPlayback(CreateEvent(),()=>session);
  session.Loaded.SetResult(true);await owner.StartAsync(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},new Dictionary<AudioParameter,AudioParameterValue>());
  var process=AudioSystem.GetEvent(CreateEvent()).CreateInstance();process.Start();
  try
  {
   var pending=owner.PrepareAndScheduleAsync(process,new AudioInstrument(),new SoundEventTrack(),null,null,1,.2f);
   process.Stop(AudioStopMode.Immediate);process.Start();
   session.Phrase.SetResult(new(26460,Task.FromResult(true)));
   Assert.IsNull(await pending);
  }
  finally {process.Stop(AudioStopMode.Immediate);process.Release();}
 }
 [TestMethod] public async Task MusicEventOwner_InitialSeekKeepsOutputPausedAndMutedUntilPublished()
 {
  var session=new EventMusicSessionFake{SeekGate=new()};session.Loaded.SetResult(true);
  using var owner=new AudioMusicEventPlayback(CreateEvent(),()=>session){Volume=.7f};
  var starting=owner.StartAsync(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},new Dictionary<AudioParameter,AudioParameterValue>(),44100);
  Assert.IsFalse(starting.IsCompleted);Assert.IsFalse(owner.IsRunning);Assert.IsTrue(session.Paused);Assert.AreEqual(0f,session.Volume);
  owner.Volume=.3f;owner.Paused=false;Assert.IsTrue(session.Paused);Assert.AreEqual(0f,session.Volume);
  session.SeekGate.SetResult(true);Assert.IsTrue(await starting);Assert.IsTrue(owner.IsRunning);
  Assert.AreEqual(44100L,owner.Position);Assert.IsFalse(session.Paused);Assert.AreEqual(.3f,session.Volume);
 }
 [TestMethod] public async Task MusicEventOwner_LoadingSeeksUseLatestPositionAndStopResolvesWaiters()
 {
  var session=new EventMusicSessionFake();using var owner=new AudioMusicEventPlayback(CreateEvent(),()=>session);
  var start=owner.StartAsync(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},new Dictionary<AudioParameter,AudioParameterValue>());
  var first=owner.Seek(22050);var latest=owner.Seek(44100);Assert.IsFalse(await first);
  session.Loaded.SetResult(true);Assert.IsTrue(await start);Assert.IsTrue(await latest);Assert.AreEqual(44100L,owner.Position);
  Assert.AreEqual(1,session.SeekCalls);
  await owner.StopAsync();Assert.IsFalse(await owner.Seek(100));
 }
 [TestMethod] public async Task MusicEventOwner_StopWhileInitialSeekPendingCannotUnmuteLate()
 {
  var session=new EventMusicSessionFake{SeekGate=new()};session.Loaded.SetResult(true);
  using var owner=new AudioMusicEventPlayback(CreateEvent(),()=>session);
  var start=owner.StartAsync(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},new Dictionary<AudioParameter,AudioParameterValue>(),44100);
  var queued=owner.Seek(22050);await owner.StopAsync();Assert.IsFalse(await queued);
  session.SeekGate.SetResult(true);Assert.IsFalse(await start);Assert.AreEqual(0f,session.Volume);Assert.IsFalse(owner.IsRunning);
 }
 [TestMethod] public async Task MusicEventOwner_StopDuringLoadCannotStartLateBacking()
 {
  var session=new EventMusicSessionFake();using var owner=new AudioMusicEventPlayback(CreateEvent(),()=>session);
  var started=owner.StartAsync(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},new Dictionary<AudioParameter,AudioParameterValue>());
  await owner.StopAsync();session.Loaded.SetResult(true);
  Assert.IsFalse(await started);Assert.IsFalse(owner.IsRunning);Assert.AreEqual(0,session.Updates);Assert.AreEqual(1,session.Disposals);
 }
 [TestMethod] public async Task MusicEventOwner_RestartRejectsOldCompletionAndUsesLatestFacts()
 {
  var first=new EventMusicSessionFake();var second=new EventMusicSessionFake();var sessions=new Queue<EventMusicSessionFake>(new[]{first,second});
  using var owner=new AudioMusicEventPlayback(CreateEvent(),()=>sessions.Dequeue());
  var fact=new AudioParameter{Name="Pressure"};var values=new Dictionary<AudioParameter,AudioParameterValue>{{fact,new(0,null,false)}};
  var grid=new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100};
  var oldStart=owner.StartAsync(grid,values);var newStart=owner.StartAsync(grid,values);
  owner.Paused=true;owner.Volume=.2f;
  Assert.AreEqual(0f,second.Volume,"Loading output must remain muted until the latest facts are applied.");
  values[fact]=new(1,null,false);owner.UpdateParameters(values);values[fact]=new(0,null,false);
  first.Loaded.SetResult(true);Assert.IsFalse(await oldStart);
  second.Loaded.SetResult(true);Assert.IsTrue(await newStart);
  Assert.AreEqual(1,first.Disposals);Assert.AreEqual(0,first.Updates);
  Assert.IsTrue(second.Paused);Assert.AreEqual(.2f,second.Volume);Assert.AreEqual(1f,second.LastParameters[fact].Number);
  owner.Paused=false;owner.Volume=0;Assert.IsFalse(second.Paused);Assert.AreEqual(0f,second.Volume);
  owner.Dispose();Assert.IsFalse(owner.IsRunning);Assert.AreEqual(1,second.Disposals);
 }
 [TestMethod] public async Task MusicEventOwner_LoadFailureCleansUpItsSession()
 {
  var session=new EventMusicSessionFake();using var owner=new AudioMusicEventPlayback(CreateEvent(),()=>session);
  var start=owner.StartAsync(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},new Dictionary<AudioParameter,AudioParameterValue>());
  session.Loaded.SetException(new InvalidOperationException("load failed"));
  await Assert.ThrowsExactlyAsync<InvalidOperationException>(async()=>await start);
  Assert.IsFalse(owner.IsRunning);Assert.AreEqual(1,session.Disposals);
 }
 [TestMethod] public async Task MusicEventOwner_DisposeCancelsRetiredFadeAndReleasesItsSession()
 {
  var session=new EventMusicSessionFake{FadeGate=new()};session.Loaded.SetResult(true);
  var owner=new AudioMusicEventPlayback(CreateEvent(),()=>session);
  await owner.StartAsync(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},new Dictionary<AudioParameter,AudioParameterValue>());
  var stopping=owner.StopAsync(.5f);Assert.AreEqual(.5f,session.FadeSeconds);Assert.IsFalse(stopping.IsCompleted);Assert.AreEqual(0,session.Disposals);
  owner.Dispose();await stopping;Assert.AreEqual(1,session.Disposals);
 }

}
