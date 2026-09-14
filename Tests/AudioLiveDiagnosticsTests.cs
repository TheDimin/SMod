using Sandbox;
using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod]
 public void LiveMonitor_HidesCompletedRetainedGraphFadeWithoutPlaybackPolling()
 {
  var parent=Nested(CreateEvent());parent.SustainUntilStopped=true;
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.Start();
  var id=instance.LiveId;
  instance.Stop(AudioStopMode.AllowFadeout);instance.Release();
  Assert.IsNotNull(instance.CaptureLiveSnapshot(),"A running fade must remain visible.");
  var voices=_backend.Voices.Count;
  _backend.Voices.Single().IsPlaying=false;
  // Do not read PlaybackState: the editor must recognize completion on its own.
  Assert.IsNull(instance.CaptureLiveSnapshot());
  Assert.IsFalse(AudioLiveDiagnostics.CaptureActive().Any(x=>x.Id==id));
  Assert.AreEqual(voices,_backend.Voices.Count);
 }
 [TestMethod]
 public void LiveMonitor_KeepsRetainedProcessAfterItsNativeVoiceEnds()
 {
  var e=CreateEvent();e.SustainUntilStopped=true;
  var i=AudioSystem.GetEvent(e).CreateInstance();i.Start();
  var id=i.CaptureLiveSnapshot().Id;
  _backend.Voices.Single().IsPlaying=false;
  Assert.AreEqual(AudioPlaybackState.Playing,i.PlaybackState);
  var snapshot=AudioLiveDiagnostics.CaptureActive().SingleOrDefault(x=>x.Id==id);
  Assert.IsNotNull(snapshot,"The process remains active while it waits for shared music.");
  Assert.AreEqual("Stopped",snapshot.Voices.Single().State);
  i.Paused=true;
  Assert.AreEqual("Paused",i.CaptureLiveSnapshot().State);
  i.Stop(AudioStopMode.Immediate);i.Release();
  Assert.IsFalse(AudioLiveDiagnostics.CaptureActive().Any(x=>x.Id==id));
 }

 [TestMethod]
 public void LiveMonitor_DistinguishesInstancesAndDropsStoppedPlayback()
 {
  var e=CreateEvent();var a=AudioSystem.GetEvent(e).CreateInstance();var b=AudioSystem.GetEvent(e).CreateInstance();
  Assert.IsNull(a.CaptureLiveSnapshot());a.Start();b.Start();
  var first=a.CaptureLiveSnapshot();var second=b.CaptureLiveSnapshot();
  Assert.AreNotEqual(first.Id,second.Id);
  Assert.IsTrue(AudioLiveDiagnostics.CaptureActive().Any(x=>x.Id==first.Id));
  a.Stop(AudioStopMode.Immediate);
  Assert.IsFalse(AudioLiveDiagnostics.CaptureActive().Any(x=>x.Id==first.Id));
  b.Stop(AudioStopMode.Immediate);
 }
 [TestMethod]
 public void LiveMonitor_ReportsSilentPausedAndReleasedVoicesWithoutOwningThem()
 {
  var i=AudioSystem.GetEvent(CreateEvent()).CreateInstance();i.Start();i.Volume=0;
  Assert.AreEqual("Silent",i.CaptureLiveSnapshot().Voices.Single().State);
  i.Paused=true;Assert.AreEqual("Paused",i.CaptureLiveSnapshot().Voices.Single().State);
  i.Paused=false;i.Release();
  Assert.IsNotNull(i.CaptureLiveSnapshot());
  Assert.IsTrue(_backend.Voices.Single().IsPlaying);
 }
 [TestMethod]
 public void LiveMonitor_CopiesParametersAndDoesNotDriveMusicTransitions()
 {
  var e=PlaylistEvent();var i=AudioSystem.GetEvent(e).CreateInstance();i.Start();
  var before=i.CaptureLiveSnapshot();i.SetParameterByName("WorldState",1);
  var after=i.CaptureLiveSnapshot();
  Assert.AreEqual("0",before.Parameters.Single().Value);Assert.AreEqual("1",after.Parameters.Single().Value);
  var count=_backend.Voices.Count;
  for(var n=0;n<20;n++)_=AudioLiveDiagnostics.CaptureActive();
  Assert.AreEqual(count,_backend.Voices.Count);Assert.AreEqual(before.Position,i.CaptureLiveSnapshot().Position);
  i.Stop(AudioStopMode.Immediate);
 }
}
