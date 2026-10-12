using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void SustainedEvent_HoldsSilentProcessAndCancelsPendingMusicOnStop()
 {
  var resource=new AudioEvent{SustainUntilStopped=true};
  Assert.IsTrue(AudioEventValidator.Validate(resource).IsValid);
  var process=AudioSystem.GetEvent(resource).CreateInstance();process.Start();
  Assert.AreEqual(AudioPlaybackState.Playing,process.PlaybackState);Assert.AreEqual(0,_backend.Voices.Count);
  var owner=process.MusicRequestOwner;
  _timeline.Advance(20);Assert.AreEqual(AudioPlaybackState.Playing,process.PlaybackState);Assert.IsFalse(owner.IsCancelled);
  process.Paused=true;var at=process.TimelinePosition;_timeline.Advance(3);Assert.AreEqual(at,process.TimelinePosition);process.Paused=false;
  var session=new AudioMusicPcmSession(new AudioBeatGrid{End=new(8),BeatsPerMinute=100});
  var request=session.TrySchedule(process,new short[]{100,100},1,1);Assert.IsNotNull(request);
  Assert.ThrowsExactly<InvalidOperationException>(()=>process.Release());
  process.Stop(AudioStopMode.Immediate);Assert.IsTrue(owner.IsCancelled);Assert.AreEqual(AudioPlaybackState.Stopped,process.PlaybackState);
  session.Processor.MixChannel(0,new float[512]);session.Processor.MixChannel(1,new float[512]);
  Assert.IsFalse(request.Admission.Result);
  process.Start();Assert.AreNotSame(owner,process.MusicRequestOwner);Assert.IsFalse(process.MusicRequestOwner.IsCancelled);
  process.Stop(AudioStopMode.Immediate);process.Release();session.CloseRequests();
 }
 [TestMethod] public void SustainedEvent_StopsAfterFadeAndNormalEventsStillComplete()
 {
  var resource=CreateEvent();resource.SustainUntilStopped=true;
  var process=AudioSystem.GetEvent(resource).CreateInstance();process.Start();
  _backend.Voices[0].IsPlaying=false;Assert.AreEqual(AudioPlaybackState.Playing,process.PlaybackState);
  process.Start();process.Stop(AudioStopMode.AllowFadeout);Assert.AreEqual(AudioPlaybackState.Stopping,process.PlaybackState);
  foreach(var voice in _backend.Voices)voice.IsPlaying=false;
  Assert.AreEqual(AudioPlaybackState.Stopped,process.PlaybackState);process.Release();
  var normal=AudioSystem.GetEvent(CreateEvent()).CreateInstance();normal.Start();var owner=normal.MusicRequestOwner;
  foreach(var voice in _backend.Voices)voice.IsPlaying=false;
  Assert.AreEqual(AudioPlaybackState.Stopped,normal.PlaybackState);Assert.IsTrue(owner.IsCancelled);normal.Release();
 }
}
