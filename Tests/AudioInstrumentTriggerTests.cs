using Smod;
public sealed partial class AudioEventTests
{
 private (AudioEvent Parent, AudioInstrument Passage, EventMusicSessionFake[] Sessions, AudioEventInstance Instance) TriggerFixture()
 {
  var ready=new AudioParameter{Name="Ready",Minimum=0,Maximum=1};
  var parent=Nested(SharedRotationSong());parent.SustainUntilStopped=true;parent.Parameters.Add(ready);
  var passage=AddSourceInstrument(parent, instrument =>
  {
   instrument.Name="Passage";instrument.TrimStart=10;instrument.TrimEnd=12;
   instrument.StartTriggerConditions=new(){new(){Parameter=ready,Minimum=1,Maximum=1}};
  });
  var selector=parent.MasterTrack.Instruments[0];
  selector.SubEvents[0].Conditions.Add(new(){Parameter=ready,Minimum=0,Maximum=0});
  selector.SubEvents.Add(new(){Event=SharedRotationSong(),EntryAfterInstrument=passage.Id,EntryQuantization=TransitionQuantization.NextPhrase,
   Conditions=new(){new(){Parameter=ready,Minimum=1,Maximum=1}}});
  parent.MasterTrack.Instruments.Add(passage);
  var sessions=Enumerable.Range(0,5).Select(_=>new EventMusicSessionFake()).ToArray();
  foreach(var session in sessions)session.Loaded.SetResult(true);
  var queue=new Queue<EventMusicSessionFake>(sessions);var instance=AudioSystem.GetEvent(parent).CreateInstance();
  instance.SharedMusicSessionFactory=()=>queue.Dequeue();return(parent,passage,sessions,instance);
 }
 [TestMethod]
 public void Trigger_NativeCompletionStartsPreparedSongWithoutAnotherBeatWait()
 {
  var f=TriggerFixture();
  try
  {
   f.Instance.Start();_timeline.Advance(30);f.Instance.SetParameterByName("Ready",1);
   var voice=_backend.Voices.Single();Assert.AreEqual(10f,voice.Time,.001f);Assert.IsTrue(f.Sessions[1].Paused);
   Assert.IsTrue(f.Instance.CaptureLiveSnapshot().Voices.Any(v=>v.Reason?.Contains("Waiting for parent instrument")==true));
   _timeline.Advance(20);voice.Time=11.99f;_=f.Instance.PlaybackState;
   Assert.IsTrue(f.Sessions[1].Paused,"Wall time does not finish a pitched native passage.");
   voice.Time=12;_=f.Instance.PlaybackState;Assert.IsFalse(f.Sessions[1].Paused);Assert.IsFalse(voice.IsPlaying);
   f.Instance.SetParameterByName("Ready",1);_=f.Instance.PlaybackState;Assert.AreEqual(1,_backend.Voices.Count);
  }
  finally{f.Instance.Stop(AudioStopMode.Immediate);f.Instance.Release();}
 }
 [TestMethod]
 public void Trigger_CancellationRearmsAndDropsOldDestination()
 {
  var f=TriggerFixture();
  try
  {
   f.Instance.Start();f.Instance.SetParameterByName("Ready",1);var first=_backend.Voices.Single();
   f.Instance.SetParameterByName("Ready",0);Assert.AreEqual(1,f.Sessions[1].Disposals);
   Assert.IsTrue(first.IsPlaying,"Keep the voice for its cancellation pitch/fade.");
   first.Time=12;_=f.Instance.PlaybackState;Assert.IsFalse(f.Sessions[0].Paused);
   f.Instance.SetParameterByName("Ready",1);Assert.AreEqual(2,_backend.Voices.Count);Assert.IsTrue(f.Sessions[2].Paused);
   _backend.Voices.Last().Time=12;_=f.Instance.PlaybackState;Assert.IsFalse(f.Sessions[2].Paused);
  }
  finally{f.Instance.Stop(AudioStopMode.Immediate);f.Instance.Release();}
 }
 [TestMethod]
 public void Trigger_PauseMuteSeekAndStopKeepOwnership()
 {
  var f=TriggerFixture();
  try
  {
   f.Instance.Start();f.Instance.Paused=true;f.Instance.SetParameterByName("Ready",1);var first=_backend.Voices.Single();
   Assert.IsTrue(first.Paused);Assert.IsTrue(f.Sessions[1].Paused);f.Instance.Volume=0;Assert.AreEqual(0f,first.Volume);
   _timeline.Advance(20);_=f.Instance.PlaybackState;Assert.IsTrue(f.Sessions[1].Paused);
   f.Instance.Paused=false;f.Instance.Volume=1;Assert.IsFalse(first.Paused);
   f.Instance.TimelinePosition=40;Assert.IsFalse(first.IsPlaying);Assert.AreEqual(2,_backend.Voices.Count);
   Assert.AreEqual(10f,_backend.Voices.Last().Time,.001f);
  }
  finally{f.Instance.Stop(AudioStopMode.Immediate);f.Instance.Release();}
  Assert.IsTrue(_backend.Voices.All(v=>!v.IsPlaying));Assert.AreEqual(1,f.Sessions[0].Disposals);Assert.AreEqual(1,f.Sessions[1].Disposals);
 }
 [TestMethod]
 public void Trigger_ExplicitStopIsCancellationNotCompletionAndNextEntryRearms()
 {
  var f=TriggerFixture();
  var ready=f.Parent.Parameters.Single(p=>p.Name=="Ready");
  f.Passage.StopTriggerConditions.Add(new(){Parameter=ready,Minimum=0,Maximum=0});
  try
  {
   f.Instance.Start();f.Instance.SetParameterByName("Ready",1);var first=_backend.Voices.Single();
   f.Instance.SetParameterByName("Ready",0);Assert.IsFalse(first.IsPlaying);
   Assert.IsTrue(f.Instance.CaptureLiveSnapshot().Voices.Any(v=>v.InstrumentId==f.Passage.Id && v.Reason.Contains("cancelled")));
   _timeline.Advance(20);_=f.Instance.PlaybackState;Assert.AreEqual(1,_backend.Voices.Count);
   f.Instance.SetParameterByName("Ready",1);Assert.AreEqual(2,_backend.Voices.Count);Assert.IsTrue(f.Sessions[2].Paused);
   _backend.Voices.Last().Time=12;_=f.Instance.PlaybackState;Assert.IsFalse(f.Sessions[2].Paused);
  }
  finally{f.Instance.Stop(AudioStopMode.Immediate);f.Instance.Release();}
 }
 [TestMethod]
 public void Trigger_RejectsForeignIdsAndUnsupportedPaths()
 {
  var f=TriggerFixture();var choice=f.Parent.MasterTrack.Instruments[0].SubEvents[1];
  Assert.IsTrue(AudioEventValidator.Validate(f.Parent).IsValid);choice.EntryAfterInstrument=Guid.NewGuid();
  Assert.IsFalse(AudioEventValidator.Validate(f.Parent).IsValid);choice.EntryAfterInstrument=f.Passage.Id;f.Passage.TrimEnd=0;
  Assert.IsFalse(AudioEventValidator.Validate(f.Parent).IsValid);f.Passage.TrimEnd=12;f.Parent.SharedMusicOutput=true;
  Assert.IsFalse(AudioEventValidator.Validate(f.Parent).IsValid);f.Instance.Release();
 }
}
