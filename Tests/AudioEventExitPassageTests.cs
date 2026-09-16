using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void ExitPassage_OverlapStartsDestinationAndPreservesCompleteTail()
 {
  var phase=new AudioParameter{Name="Phase",Minimum=0,Maximum=1};
  var parent=Nested(SharedRotationSong());parent.Parameters.Add(phase);
  var instrument=parent.MasterTrack.Instruments[0];
  instrument.SubEvents[0].Conditions.Add(new(){Parameter=phase,Minimum=0,Maximum=0});
  instrument.SubEvents[0].ExitEvent=SharedSong();instrument.SubEvents[0].ExitEventOverlapSeconds=1.2f;
  instrument.SubEvents.Add(new(){Event=SharedRotationSong(),Conditions=new(){new(){Parameter=phase,Minimum=1,Maximum=1}}});
  var old=new EventMusicSessionFake();var next=new EventMusicSessionFake();var bridge=new EventMusicSessionFake();
  foreach(var session in new[]{old,next,bridge})session.Loaded.SetResult(true);
  var queue=new Queue<EventMusicSessionFake>(new[]{old,next,bridge});
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>queue.Dequeue();
  try
  {
   instance.Start();instance.SetParameterByName("Phase",1);
   bridge.Position=52919;instance.RefreshAuthoredPropertiesForAudition();Assert.IsTrue(next.Paused);
   bridge.Position=52920;instance.RefreshAuthoredPropertiesForAudition();
   Assert.IsFalse(next.Paused);Assert.AreEqual(0,bridge.Disposals,"Keep the source tail after destination entry.");
   var volume=bridge.Volume;bridge.Position=90000;instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreEqual(volume,bridge.Volume,.0001f,"Do not impose another fade over the recorded decay.");
   instance.Paused=true;Assert.IsTrue(bridge.Paused);Assert.IsTrue(next.Paused);instance.Paused=false;
   bridge.Position=105840;instance.RefreshAuthoredPropertiesForAudition();Assert.AreEqual(1,bridge.Disposals);
   Assert.AreEqual(0,next.Disposals);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
  Assert.AreEqual(1,next.Disposals);
 }
 [TestMethod] public void ExitPassage_OverlapRequiresAValidExitAndBoundedDuration()
 {
  var parent=Nested(SharedSong());var choice=parent.MasterTrack.Instruments[0].SubEvents[0];
  choice.ExitEventOverlapSeconds=.5f;Assert.IsFalse(AudioEventValidator.Validate(parent).IsValid);
  choice.ExitEvent=SharedSong();Assert.IsTrue(AudioEventValidator.Validate(parent).IsValid);
  foreach(var value in new[]{-1f,float.NaN,float.PositiveInfinity,2.4f,3f})
  {choice.ExitEventOverlapSeconds=value;Assert.IsFalse(AudioEventValidator.Validate(parent).IsValid);}
 }

 [TestMethod]
 public void ExitPassage_PreparesThenPlaysOnceBeforeTheDestination()
 {
  var phase=new AudioParameter{Name="Phase",Minimum=0,Maximum=1};
  var parent=Nested(SharedRotationSong());parent.Parameters.Add(phase);
  var instrument=parent.MasterTrack.Instruments[0];
  instrument.SubEvents[0].Conditions.Add(new(){Parameter=phase,Minimum=0,Maximum=0});
  instrument.SubEvents[0].ExitEvent=SharedSong();
  instrument.SubEvents.Add(new(){Event=SharedRotationSong(),Conditions=new(){new(){Parameter=phase,Minimum=1,Maximum=1}}});
  var old=new EventMusicSessionFake();old.Loaded.SetResult(true);
  var next=new EventMusicSessionFake();next.Loaded.SetResult(true);
  var bridge=new EventMusicSessionFake();
  var sessions=new Queue<EventMusicSessionFake>(new[]{old,next,bridge});
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>sessions.Dequeue();
  try
  {
   instance.Start();instance.SetParameterByName("Phase",1);
   Assert.AreEqual(0,old.Disposals,"Keep the old music while the passage loads.");
   Assert.IsTrue(next.Paused);
   bridge.Loaded.SetResult(true);instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreEqual(105840L,bridge.EndSample);
   Assert.AreEqual(1,old.Disposals);Assert.IsFalse(bridge.Paused);Assert.IsTrue(next.Paused);
   var passage=instance.InstrumentPlaybackStates.Single().ChildInstanceId;
   instance.Paused=true;Assert.IsTrue(bridge.Paused);Assert.IsTrue(next.Paused);
   instance.Paused=false;
   bridge.Position=105839;instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreEqual(passage,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
   bridge.Position=105840;instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreNotEqual(passage,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
   Assert.AreEqual(1,bridge.Disposals);Assert.IsFalse(next.Paused);
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
  Assert.AreEqual(1,next.Disposals);
 }

 [TestMethod]
 public void ExitPassage_RejectsUnsupportedPlaybackAndGraphCycles()
 {
  var parent=Nested(SharedSong());var choice=parent.MasterTrack.Instruments[0].SubEvents[0];
  choice.ExitEvent=CreateEvent();Assert.IsFalse(AudioEventValidator.Validate(parent).IsValid);
  choice.ExitEvent=SharedSong();Assert.IsTrue(AudioEventValidator.Validate(parent).IsValid);
  choice.ExitEvent=parent;Assert.IsFalse(AudioEventValidator.Validate(parent).IsValid);
 }

 [TestMethod]
 public void ExitPassage_OutputStopsAtItsExactSampleEvenAcrossALargeBlock()
 {
  var backing=new AudioMusicPcmBacking(new short[]{16384,16384,16384,16384},1);
  var render=new AudioMusicPcmRenderer(2,backing:backing);render.LimitPlaybackTo(3);
  render.Schedule(new AudioMusicRequestOwner(),new short[]{8192,8192,8192,8192,8192,8192,8192,8192},0,1);
  var output=new float[12];render.Render(output);
  CollectionAssert.AreEqual(new[]{.75f,.75f,.75f,.75f,.75f,.75f,0,0,0,0,0,0},output);
  Assert.AreEqual(6L,render.Position);Assert.AreEqual(0,render.ActiveCount);
  Assert.IsFalse(render.Schedule(new AudioMusicRequestOwner(),new short[]{1,1},6,1));
  Array.Clear(output);render.Render(output);Assert.IsTrue(output.All(v=>v==0));
 }
 [TestMethod]
 [DataRow(false)]
 [DataRow(true)]
 public void ExitPassage_ReversalDoesNotRestartThePassage(bool beforeStart)
 {
  var phase=new AudioParameter{Name="Phase",Minimum=0,Maximum=1};
  var parent=Nested(SharedRotationSong());parent.Parameters.Add(phase);
  var instrument=parent.MasterTrack.Instruments[0];
  var source=instrument.SubEvents[0];source.ResumePlayback=true;
  source.Conditions.Add(new(){Parameter=phase,Minimum=0,Maximum=0});source.ExitEvent=SharedSong();
  instrument.SubEvents.Add(new(){Event=SharedRotationSong(),Conditions=new(){new(){Parameter=phase,Minimum=1,Maximum=1}}});
  var old=new EventMusicSessionFake();old.Loaded.SetResult(true);
  var next=new EventMusicSessionFake();next.Loaded.SetResult(true);
  var bridge=new EventMusicSessionFake();if(!beforeStart)bridge.Loaded.SetResult(true);
  var resumed=new EventMusicSessionFake();resumed.Loaded.SetResult(true);
  var sessions=new Queue<EventMusicSessionFake>(new[]{old,next,bridge,resumed});
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>sessions.Dequeue();
  try
  {
   instance.Start();old.Position=441000;instance.SetParameterByName("Phase",1);
   var active=instance.InstrumentPlaybackStates.Single().ChildInstanceId;
   instance.SetParameterByName("Phase",0);Assert.AreEqual(1,next.Disposals);
   if(beforeStart)
   {
    Assert.AreEqual(1,bridge.Disposals);bridge.Loaded.SetResult(true);
    Assert.AreEqual(0,old.Disposals);Assert.AreEqual(active,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
   }
   else
   {
    Assert.AreEqual(active,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
    bridge.Position=105840;instance.RefreshAuthoredPropertiesForAudition();
    Assert.AreEqual(1,bridge.Disposals);Assert.IsFalse(resumed.Paused);Assert.AreEqual(441000L,resumed.Position);
   }
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }

 [TestMethod]
 public void ExitPassage_NoEligibleDestinationReleasesPreparedAndActiveAudio()
 {
  var phase=new AudioParameter{Name="Phase",Minimum=0,Maximum=2};
  var parent=Nested(SharedRotationSong());parent.Parameters.Add(phase);
  var instrument=parent.MasterTrack.Instruments[0];
  instrument.SubEvents[0].Conditions.Add(new(){Parameter=phase,Minimum=0,Maximum=0});
  instrument.SubEvents[0].ExitEvent=SharedSong();
  instrument.SubEvents.Add(new(){Event=SharedSong(),Conditions=new(){new(){Parameter=phase,Minimum=1,Maximum=1}}});
  var sessions=new[]{new EventMusicSessionFake(),new EventMusicSessionFake(),new EventMusicSessionFake()};
  foreach(var session in sessions)session.Loaded.SetResult(true);
  var queue=new Queue<EventMusicSessionFake>(sessions);
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>queue.Dequeue();
  try
  {
   instance.Start();instance.SetParameterByName("Phase",1);instance.SetParameterByName("Phase",2);
   Assert.IsTrue(sessions.All(s=>s.Disposals==1));
   Assert.IsTrue(instance.InstrumentPlaybackStates.All(s=>s.ChildInstanceId==Guid.Empty));
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
}
