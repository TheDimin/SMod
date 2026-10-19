using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public async System.Threading.Tasks.Task MusicRuntime_SeekRejectsPreparationFromOldTimelineWithoutRestartingOutput()
 {
  var output=new MusicOutputFake();using var runtime=new AudioMusicRuntimeSession(output);
  runtime.StartPrepared(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},CreateSoundEvent().Tracks[0].Sound,new short[211680],1);
  var process=AudioSystem.GetEvent(CreateEvent()).CreateInstance();process.Start();
  try
  {
   var prepared=new System.Threading.Tasks.TaskCompletionSource<short[]>();
   var old=runtime.PrepareAndScheduleAsync(process,()=>prepared.Task,1,.2f);
   runtime.Paused=true;var seek=runtime.Seek(44100);
   Assert.IsNull(runtime.TrySchedule(process,new short[]{1,1},1,.2f),"Do not schedule against the old published clock while a seek is pending.");
   output.Render();Assert.IsTrue(await seek);Assert.AreEqual(44100L,runtime.Position);
   prepared.SetResult(new short[]{1,1});Assert.IsNull(await old);
   var fresh=runtime.TrySchedule(process,new short[]{1,1},1,.2f);Assert.IsNotNull(fresh);
   runtime.Paused=false;output.Render();Assert.IsTrue(await fresh.Admission);Assert.AreEqual(1,output.Starts);
  }
  finally {process.Stop(AudioStopMode.Immediate);process.Release();}
 }
 [TestMethod] public void MusicRuntime_ContinuousMixChangesDoNotRestartNativeOutput()
 {
  var output=new MusicOutputFake();using var runtime=new AudioMusicRuntimeSession(output);
  var grid=new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100};
  var a=Enumerable.Repeat((short)8192,211680).ToArray();
  var b=Enumerable.Repeat((short)4096,211680).ToArray();
  runtime.StartPrepared(grid,CreateSoundEvent().Tracks[0].Sound,new AudioMusicPcmBacking.Stem[]{new(a,1),new(b,0)});
  output.Render();runtime.SetBackingGains(new float[]{1,1});output.Render();output.Render();
  var left=new float[1];var right=new float[1];
  output.Processor.MixChannel(0,left);output.Processor.MixChannel(1,right);
  Assert.AreEqual(.375f,left[0]);Assert.AreEqual(.375f,right[0]);
  Assert.AreEqual(1,output.Starts);Assert.AreEqual(1537L,runtime.Position);
 }
 [TestMethod] public void MusicRuntime_MismatchedContinuousStemsNeverStartOutput()
 {
  var output=new MusicOutputFake();using var runtime=new AudioMusicRuntimeSession(output);
  Assert.ThrowsExactly<ArgumentException>(()=>runtime.StartPrepared(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},
   CreateSoundEvent().Tracks[0].Sound,new AudioMusicPcmBacking.Stem[]{new(new short[211680],1),new(new short[211678],1)}));
  Assert.AreEqual(0,output.Starts);Assert.IsTrue(runtime.IsStopped);
 }
 [TestMethod] public void MusicOutputGain_MutesAndRampsWithoutSplittingStereo()
 {
  var gain=new AudioMusicOutputGain{Volume=0};var state=new AudioMusicOutputGain.State();
  var left=Enumerable.Repeat(1f,512).ToArray();var right=left.ToArray();gain.MixChannel(state,0,left);gain.MixChannel(state,1,right);
  Assert.IsTrue(left.All(x=>x==0)&&right.All(x=>x==0));
  gain.Volume=1;left=Enumerable.Repeat(1f,1024).ToArray();right=left.ToArray();gain.MixChannel(state,0,left);gain.Volume=0;gain.MixChannel(state,1,right);
  CollectionAssert.AreEqual(left,right);Assert.IsTrue(left[0]<.002f);Assert.AreEqual(1f,left[^1]);
  left=Enumerable.Repeat(1f,1024).ToArray();gain.MixChannel(state,0,left);Assert.AreEqual(0f,left[^1]);
 }

 sealed class MusicOutputFake : IAudioMusicOutput
 {
  public AudioMusicPcmProcessor Processor;
  public float Volume {get;set;}
  public int Starts,Disposals;
  public bool IsStopped {get;set;}
  public readonly System.Threading.Tasks.TaskCompletionSource<bool> FadeCompletion=new();
  public int FadeFrames;
  public System.Threading.Tasks.Task<bool> FadeOutAsync(int frames){FadeFrames=frames;return FadeCompletion.Task;}
  public bool Fail;
  public void Start(AudioMusicPcmProcessor processor,Sandbox.SoundFile source,float volume)
  {
   Starts++;if(Fail)throw new InvalidOperationException("test start failure");Processor=processor;Volume=volume;
  }
  public void Render(){Processor.MixChannel(0,new float[512]);Processor.MixChannel(1,new float[512]);}
  public void Dispose()=>Disposals++;
 }
 [TestMethod] public async System.Threading.Tasks.Task MusicRuntime_PauseVolumeAndStopOwnTheOutputLifetime()
 {
  var output=new MusicOutputFake();var runtime=new AudioMusicRuntimeSession(output,()=>{output.Render();return System.Threading.Tasks.Task.CompletedTask;});
  runtime.Paused=true;runtime.Volume=0;
  runtime.StartPrepared(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},CreateSoundEvent().Tracks[0].Sound,new short[211680],1);
  Assert.AreEqual(0f,output.Volume);output.Render();Assert.AreEqual(0L,runtime.Position);
  runtime.Paused=false;runtime.Volume=.25f;output.Render();Assert.AreEqual(512L,runtime.Position);Assert.AreEqual(.25f,output.Volume);
  await runtime.StopAsync();Assert.IsTrue(runtime.IsStopped);Assert.AreEqual(1,output.Disposals);
  await runtime.StopAsync();runtime.Dispose();Assert.AreEqual(1,output.Disposals);
  Assert.IsNull(runtime.TrySchedule(null,null,1,1));Assert.AreEqual(0L,runtime.ResidentBytes);
 }
 [TestMethod] public void MusicRuntime_StartFailureCleansUpAndCannotRestart()
 {
  var output=new MusicOutputFake{Fail=true};var runtime=new AudioMusicRuntimeSession(output);
  var grid=new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100};var source=CreateSoundEvent().Tracks[0].Sound;
  Assert.ThrowsExactly<InvalidOperationException>(()=>runtime.StartPrepared(grid,source,new short[211680],1));
  Assert.IsTrue(runtime.IsStopped);Assert.AreEqual(1,output.Disposals);
  Assert.ThrowsExactly<InvalidOperationException>(()=>runtime.StartPrepared(grid,source,new short[211680],1));
 }
 [TestMethod] public async System.Threading.Tasks.Task MusicRuntime_CloseBeforeStartCreatesNoOutput()
 {
  var output=new MusicOutputFake();var runtime=new AudioMusicRuntimeSession(output);await runtime.StopAsync();
  Assert.AreEqual(0,output.Starts);Assert.IsTrue(runtime.IsStopped);
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>runtime.Volume=float.NaN);
 }

 [TestMethod] public async System.Threading.Tasks.Task MusicRuntime_LoadedPhraseCannotMoveToRestartedProcess()
 {
  var output=new MusicOutputFake();using var runtime=new AudioMusicRuntimeSession(output);
  runtime.StartPrepared(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},CreateSoundEvent().Tracks[0].Sound,new short[211680],1);
  var process=AudioSystem.GetEvent(new AudioEvent{SustainUntilStopped=true}).CreateInstance();process.Start();
  var pending=new System.Threading.Tasks.TaskCompletionSource<short[]>();
  var request=runtime.PrepareAndScheduleAsync(process,()=>pending.Task,1,.2f);
  process.Stop(AudioStopMode.Immediate);process.Start();
  pending.SetResult(new short[]{1,1});Assert.IsNull(await request);
  Assert.IsFalse(process.MusicRequestOwner.IsCancelled);
  process.Stop(AudioStopMode.Immediate);process.Release();
 }
 [TestMethod] public async System.Threading.Tasks.Task MusicRuntime_LoadUsesFutureBeatAndStopRejectsPendingPreparation()
 {
  var output=new MusicOutputFake();using var runtime=new AudioMusicRuntimeSession(output);
  runtime.StartPrepared(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},CreateSoundEvent().Tracks[0].Sound,new short[211680],1);
  var process=AudioSystem.GetEvent(new AudioEvent{SustainUntilStopped=true}).CreateInstance();process.Start();
  var pending=new System.Threading.Tasks.TaskCompletionSource<short[]>();
  var request=runtime.PrepareAndScheduleAsync(process,()=>pending.Task,1,.2f);
  for(var i=0;i<80;i++)output.Render();
  pending.SetResult(new short[]{1,1});var scheduled=await request;
  Assert.AreEqual(52920L,scheduled.Sample);output.Render();Assert.IsTrue(await scheduled.Admission);
  pending=new System.Threading.Tasks.TaskCompletionSource<short[]>();
  request=runtime.PrepareAndScheduleAsync(process,()=>pending.Task,1,.2f);
  runtime.Dispose();pending.SetResult(new short[]{1,1});Assert.IsNull(await request);
  process.Stop(AudioStopMode.Immediate);process.Release();
 }

 [TestMethod] public async System.Threading.Tasks.Task MusicRuntime_CapturesAuthoredEntryBarsBeforeLoading()
 {
  var output=new MusicOutputFake();using var runtime=new AudioMusicRuntimeSession(output);
  runtime.StartPrepared(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},CreateSoundEvent().Tracks[0].Sound,new short[211680],1);
  var process=AudioSystem.GetEvent(new AudioEvent{SustainUntilStopped=true}).CreateInstance();process.Start();
  var bars=new List<int>{1};var pending=new System.Threading.Tasks.TaskCompletionSource<short[]>();
  var request=runtime.PrepareAndScheduleAsync(process,()=>pending.Task,1,.2f,bars);
  bars[0]=9;pending.SetResult(new short[]{1,1});
  var scheduled=await request;Assert.AreEqual(105840L,scheduled.Sample,"Restricted entry waits for the next bar start and ignores later resource edits.");
  process.Stop(AudioStopMode.Immediate);process.Release();
 }

 [TestMethod] public async System.Threading.Tasks.Task MusicRuntime_RequestDeadlineIncludesLoadingAndRejectsDistantBars()
 {
  var output=new MusicOutputFake();using var runtime=new AudioMusicRuntimeSession(output);
  runtime.StartPrepared(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},CreateSoundEvent().Tracks[0].Sound,new short[211680],1);
  var process=AudioSystem.GetEvent(new AudioEvent{SustainUntilStopped=true}).CreateInstance();process.Start();
  var pending=new System.Threading.Tasks.TaskCompletionSource<short[]>();
  var request=runtime.PrepareAndScheduleAsync(process,()=>pending.Task,1,.2f,maximumWaitBeats:1);
  for(var i=0;i<53;i++)output.Render();
  pending.SetResult(new short[]{1,1});Assert.IsNull(await request,"Source loading cannot restart the response deadline.");
  Assert.IsNull(await runtime.PrepareAndScheduleAsync(process,()=>System.Threading.Tasks.Task.FromResult(new short[]{1,1}),1,.2f,new[]{1},1),"A distant allowed bar must not create stale feedback.");
  process.Stop(AudioStopMode.Immediate);process.Release();
 }
 [TestMethod] public async System.Threading.Tasks.Task MusicRuntime_PausedClockDoesNotExpireRequest()
 {
  var output=new MusicOutputFake();using var runtime=new AudioMusicRuntimeSession(output);runtime.Paused=true;
  runtime.StartPrepared(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},CreateSoundEvent().Tracks[0].Sound,new short[211680],1);
  var process=AudioSystem.GetEvent(new AudioEvent{SustainUntilStopped=true}).CreateInstance();process.Start();
  var pending=new System.Threading.Tasks.TaskCompletionSource<short[]>();
  var request=runtime.PrepareAndScheduleAsync(process,()=>pending.Task,1,.2f,maximumWaitBeats:1);
  for(var i=0;i<80;i++)output.Render();
  pending.SetResult(new short[]{1,1});Assert.AreEqual(26460L,(await request).Sample);
  process.Stop(AudioStopMode.Immediate);process.Release();
 }
 [TestMethod] public void MusicRuntime_ValidatesAuthoredWaitBudget()
 {
  var resource=CreateEvent();var instrument=resource.MasterTrack.Instruments[0];
  instrument.MaximumMusicWaitBeats=65;Assert.IsFalse(AudioEventValidator.Validate(resource).IsValid);
  instrument.MaximumMusicWaitBeats=-1;Assert.IsFalse(AudioEventValidator.Validate(resource).IsValid);
  instrument.MaximumMusicWaitBeats=1;Assert.IsTrue(AudioEventValidator.Validate(resource).IsValid);
 }
}
