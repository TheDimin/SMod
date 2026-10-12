using Smod;
using System.Threading.Tasks;
public sealed partial class AudioEventTests
{
 [TestMethod] public async Task MusicFade_UsesExactDurationFromCurrentGainAndCompletesAfterStereo()
 {
  var gain=new AudioMusicOutputGain{Volume=.25f};var state=new AudioMusicOutputGain.State();
  gain.MixChannel(state,0,new float[1]);gain.MixChannel(state,1,new float[1]);
  var fade=gain.FadeOutAsync(4);
  var left=new[]{1f,1f};var right=new[]{1f,1f};
  gain.MixChannel(state,0,left);gain.MixChannel(state,1,right);
  CollectionAssert.AreEqual(new[]{.1875f,.125f},left);CollectionAssert.AreEqual(left,right);Assert.IsFalse(fade.IsCompleted);
  left=new[]{1f,1f};right=new[]{1f,1f};gain.MixChannel(state,0,left);Assert.IsFalse(fade.IsCompleted);
  gain.MixChannel(state,1,right);Assert.IsTrue(await fade);
  CollectionAssert.AreEqual(new[]{.0625f,0f},left);CollectionAssert.AreEqual(left,right);
  left=new[]{1f};gain.MixChannel(state,0,left);Assert.AreEqual(0f,left[0]);
 }
 [TestMethod] public async Task MusicFade_ReplacementContinuesFromCurrentLevelAndCancelsOldWaiter()
 {
  var gain=new AudioMusicOutputGain{Volume=1};var state=new AudioMusicOutputGain.State();
  var first=gain.FadeOutAsync(4);var left=new[]{1f,1f};var right=new[]{1f,1f};gain.MixChannel(state,0,left);gain.MixChannel(state,1,right);
  var next=gain.FadeOutAsync(2);Assert.IsFalse(await first);
  left=new[]{1f,1f};right=new[]{1f,1f};gain.MixChannel(state,0,left);gain.MixChannel(state,1,right);
  CollectionAssert.AreEqual(new[]{.25f,0f},left);CollectionAssert.AreEqual(left,right);Assert.IsTrue(await next);
  var abandoned=gain.FadeOutAsync(100);gain.CancelFade();Assert.IsFalse(await abandoned);
 }
 [TestMethod] public async Task MusicRuntime_FadeKeepsOutputUntilAudioCompletionAndBlocksNewRequests()
 {
  var output=new MusicOutputFake();var tick=new TaskCompletionSource<bool>();
  using var runtime=new AudioMusicRuntimeSession(output,()=>tick.Task);
  runtime.StartPrepared(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},CreateSoundEvent().Tracks[0].Sound,new short[211680],1);
  var stop=runtime.StopWithFadeAsync(.5f);Assert.AreEqual(22050,output.FadeFrames);Assert.AreEqual(0,output.Disposals);
  Assert.IsFalse(runtime.IsRunning);Assert.IsFalse(stop.IsCompleted);
  output.FadeCompletion.SetResult(true);tick.SetResult(true);await stop;Assert.AreEqual(1,output.Disposals);Assert.IsTrue(runtime.IsStopped);
 }
 [TestMethod] public async Task MusicRuntime_StalledFadeReportsFailureAndReleasesOutput()
 {
  var output=new MusicOutputFake();
  using var runtime=new AudioMusicRuntimeSession(output,()=>{System.Threading.Thread.Sleep(10);return Task.CompletedTask;});
  runtime.StartPrepared(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},CreateSoundEvent().Tracks[0].Sound,new short[211680],1);
  await Assert.ThrowsExactlyAsync<TimeoutException>(()=>runtime.StopWithFadeAsync(.001f));
  Assert.IsTrue(runtime.IsStopped);Assert.AreEqual(1,output.Disposals);
  Assert.AreEqual(0L,runtime.ResidentBytes);Assert.IsNull(runtime.TrySchedule(null,null,1,1));
  runtime.Dispose();Assert.AreEqual(1,output.Disposals);
 }
 [TestMethod] public async Task MusicRuntime_StoppedNativeOutputClosesWithoutWaitingForFade()
 {
  var output=new MusicOutputFake();var tick=new TaskCompletionSource<bool>();
  using var runtime=new AudioMusicRuntimeSession(output,()=>tick.Task);
  runtime.StartPrepared(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},CreateSoundEvent().Tracks[0].Sound,new short[211680],1);
  var stop=runtime.StopWithFadeAsync(.5f);
  output.IsStopped=true;tick.SetResult(true);await stop;
  Assert.IsFalse(output.FadeCompletion.Task.IsCompleted,"The lost output never acknowledged a fade.");
  Assert.IsTrue(runtime.IsStopped);Assert.AreEqual(1,output.Disposals);Assert.AreEqual(0L,runtime.ResidentBytes);
 }
 [TestMethod] public async Task MusicRuntime_DisposeDuringFadeDoesNotReportTimeout()
 {
  var output=new MusicOutputFake();var tick=new TaskCompletionSource<bool>();
  using var runtime=new AudioMusicRuntimeSession(output,()=>tick.Task);
  runtime.StartPrepared(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},CreateSoundEvent().Tracks[0].Sound,new short[211680],1);
  var stop=runtime.StopWithFadeAsync(.5f);runtime.Dispose();tick.SetResult(true);await stop;
  Assert.IsTrue(runtime.IsStopped);Assert.AreEqual(1,output.Disposals);
 }
 [TestMethod] public void MusicFade_RejectsInvalidDurations()
 {
  var gain=new AudioMusicOutputGain();
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>gain.FadeOutAsync(0));
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>gain.FadeOutAsync(44100*60+1));
 }
}
