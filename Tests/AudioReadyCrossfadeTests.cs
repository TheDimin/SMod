using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod]
 [DataRow(false)] [DataRow(true)]
 public void ReceiptCrossfade_RetainsOrdinaryChildAndReleasesItAfterFade(bool stopEarly)
 {
  var receipt=CreateEvent();receipt.SustainUntilStopped=true;receipt.TimelineDuration=6;receipt.MasterTrack.Instruments[0].TrimEnd=6;
  var parent=Nested(receipt);var phase=new AudioParameter{Name="Phase",Minimum=0,Maximum=1};parent.Parameters.Add(phase);
  var choices=parent.MasterTrack.Instruments[0].SubEvents;choices[0].Conditions.Add(new(){Parameter=phase,Minimum=0,Maximum=0});
  choices.Add(new(){Event=SharedRotationSong(),EntryCrossfadeSeconds=1.2f,Conditions=new(){new(){Parameter=phase,Minimum=1,Maximum=1}}});
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();var receiptVoice=_backend.Voices.Single();_timeline.Advance(.5f);
   instance.SetParameterByName("Phase",1);Assert.IsTrue(receiptVoice.IsPlaying,"The receipt sound must keep its tail during entry.");
   instance.Paused=true;_timeline.Advance(2);instance.RefreshAuthoredPropertiesForAudition();Assert.IsTrue(receiptVoice.IsPlaying);instance.Paused=false;
   instance.Volume=0;Assert.AreEqual(0f,receiptVoice.Volume);instance.Volume=1;
   if(stopEarly)instance.Stop(AudioStopMode.Immediate);
   else {_timeline.Advance(1.3f);instance.RefreshAuthoredPropertiesForAudition();}
   Assert.IsFalse(receiptVoice.IsPlaying);
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
  Assert.AreEqual(1,session.Disposals);
 }
 [TestMethod]
 public void MasterAutomation_ReachesAllTracksWithoutApplyingTwice()
 {
  var e=CreateEvent();var parameter=new AudioParameter{Name="Recovery",Minimum=0,Maximum=1};e.Parameters.Add(parameter);
  e.MasterTrack.Volume=.8f;
  e.MasterTrack.Automations.Add(new(){Parameter=parameter,Target=AudioAutomationTarget.Volume,Points=new(){new(){Input=0,Output=0},new(){Input=1,Output=1}}});
  e.MasterTrack.Automations.Add(new(){Parameter=parameter,Target=AudioAutomationTarget.Pitch,Points=new(){new(){Input=0,Output=1},new(){Input=1,Output=1.4f}}});
  e.Tracks.Add(new(){Name="Child",Volume=.4f,Pitch=.8f,Instruments=new(){AddSourceInstrument(e)}});
  var values=new Dictionary<AudioParameter,AudioParameterValue>{{parameter,new AudioParameterValue(.5f,null,false)}};
  AudioEventValidator.Validate(e).AssertValid();
  var plan=AudioEventEvaluator.BuildPlaybackPlan(e,values);
  Assert.AreEqual(.4f,plan.Single(x=>x.Track==e.MasterTrack).Volume,.0001f);
  Assert.AreEqual(.16f,plan.Single(x=>x.Track==e.Tracks[0]).Volume,.0001f);
  Assert.AreEqual(1.2f,plan.Single(x=>x.Track==e.MasterTrack).Pitch,.0001f);
  Assert.AreEqual(.96f,plan.Single(x=>x.Track==e.Tracks[0]).Pitch,.0001f);
 }
 [TestMethod]
 [DataRow(false)]
 [DataRow(true)]
 public void ReadyCrossfade_OverlapsAndKeepsTheSameServiceChild(bool cancel)
 {
  var phase=new AudioParameter{Name="Phase",Minimum=0,Maximum=1};
  var ready=new AudioParameter{Name="Ready",Minimum=0,Maximum=1};
  var parent=Nested(SharedRotationSong());parent.Parameters.AddRange(new[]{phase,ready});
  var instrument=parent.MasterTrack.Instruments[0];
  instrument.SubEvents[0].Conditions.AddRange(new AudioParameterCondition[]{new(){Parameter=phase,Minimum=0,Maximum=0},new(){Parameter=ready,Minimum=0,Maximum=0}});
  instrument.SubEvents.Add(new(){Event=SharedRotationSong(),EntryCrossfadeSeconds=3,EntryFadeBeats=5,ExitEvent=SharedSong(),
   Conditions=new(){new(){Parameter=phase,Minimum=1,Maximum=1}},EarlyEntryConditions=new(){new(){Parameter=ready,Minimum=1,Maximum=1}}});
  var sessions=new[]{new EventMusicSessionFake(),new EventMusicSessionFake(),new EventMusicSessionFake()};
  foreach(var session in sessions)session.Loaded.SetResult(true);
  var queue=new Queue<EventMusicSessionFake>(sessions);
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>queue.Dequeue();
  try
  {
   instance.Start();instance.SetParameterByName("Ready",1);
   Assert.AreEqual(2,instance.InstrumentPlaybackStates.Count,"Both songs remain visible during overlap.");
   Assert.AreEqual(0,sessions[0].Disposals);Assert.IsFalse(sessions[1].Paused);
   var service=instance.InstrumentPlaybackStates.First().ChildInstanceId;
   instance.Paused=true;Assert.IsTrue(sessions[0].Paused);Assert.IsTrue(sessions[1].Paused);instance.Paused=false;
   instance.Volume=0;Assert.AreEqual(0f,sessions[0].Volume);Assert.AreEqual(0f,sessions[1].Volume);instance.Volume=1;
   sessions[0].Position=3*44100;instance.RefreshAuthoredPropertiesForAudition();Assert.AreEqual(1,sessions[0].Disposals);
   if(cancel)
   {
    instance.SetParameterByName("Ready",0);
    Assert.IsFalse(sessions[2].Paused,"Cancelled early service returns directly to build, without its exit passage.");
    Assert.AreEqual(1,sessions[1].Disposals);
   }
   else
   {
    instance.SetParameterByName("Phase",1);instance.SetParameterByName("Ready",0);
    Assert.AreEqual(service,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
    Assert.AreEqual(1,queue.Count,"No extra song or passage was prepared at gameplay start.");
   }
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
  Assert.IsTrue(sessions.Take(cancel?3:2).All(s=>s.Disposals==1));
 }
 [TestMethod]
 public void ReadyCrossfade_StopReleasesBothSongsDuringOverlap()
 {
  var phase=new AudioParameter{Name="Phase",Minimum=0,Maximum=1};var parent=Nested(SharedRotationSong());parent.Parameters.Add(phase);
  var choices=parent.MasterTrack.Instruments[0].SubEvents;choices[0].Conditions.Add(new(){Parameter=phase,Minimum=0,Maximum=0});
  choices.Add(new(){Event=SharedRotationSong(),EntryCrossfadeSeconds=3,Conditions=new(){new(){Parameter=phase,Minimum=1,Maximum=1}}});
  var sessions=new[]{new EventMusicSessionFake(),new EventMusicSessionFake()};foreach(var session in sessions)session.Loaded.SetResult(true);
  var queue=new Queue<EventMusicSessionFake>(sessions);var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>queue.Dequeue();
  instance.Start();instance.SetParameterByName("Phase",1);instance.Stop(AudioStopMode.Immediate);instance.Release();
  Assert.IsTrue(sessions.All(s=>s.Disposals==1));
 }
}
