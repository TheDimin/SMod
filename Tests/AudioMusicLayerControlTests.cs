using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void ParentLayer_ExtendsOnlyAtBoundaryAndStopsAtLimit()
 {
  var state=new AudioLayerPlayback(new(4,2,4,.2f,1,.1f,.5f),0,0);
  state.Update(.1,1,true);Assert.IsTrue(state.Active);Assert.AreEqual(8d,state.NextBoundary);
  state.Update(1,2,true);Assert.AreEqual(1,state.Phrases);
  state.Update(2,20,true);Assert.AreEqual(8d,state.NextBoundary,"Bursts cannot build an unlimited queue.");
  state.Update(8,20,true);Assert.AreEqual(2,state.Phrases);Assert.AreEqual(12d,state.NextBoundary);
  state.Update(9,21,true);state.Update(12,22,true);Assert.IsFalse(state.Active);
  state.Update(13,23,true);Assert.IsFalse(state.Active,"Rest discards new requests.");
  state.Update(17,23,true);Assert.IsFalse(state.Active,"Old requests cannot play after rest.");
  state.Update(18,24,true);Assert.IsTrue(state.Active);
 }
 [TestMethod] public void ParentLayer_PauseKeepsGainAndSeekDiscardsExtensions()
 {
  var state=new AudioLayerPlayback(new(4,3,4,.2f,1,.1f,.5f),0,0);
  state.Update(1,1,true);state.Update(2,2,true);
  var gain=state.Gain;state.Update(2,2,true);Assert.AreEqual(gain,state.Gain);
  state.Update(.5,2,true);Assert.IsFalse(state.Active);
  state.Update(5,2,true);Assert.IsFalse(state.Active);
 }
 [TestMethod] public void ParentLayer_SectionExitReleasesAndDoesNotReplayCounter()
 {
  var state=new AudioLayerPlayback(new(4,3,4,.2f,1,.1f,.5f),0,0);
  state.Update(1,1,true);Assert.AreEqual(1f,state.Gain);
  state.Update(1.1,2,false);Assert.IsFalse(state.Active);Assert.IsTrue(state.Gain>.2f);
  state.Update(2,2,false);Assert.AreEqual(.2f,state.Gain);
  state.Update(10,2,true);Assert.IsFalse(state.Active);
 }
 [TestMethod] public void ParentLayer_InstancesHaveIndependentExtensionState()
 {
  var plan=new AudioLayerPlan(4,3,4,.2f,1,.1f,.5f);
  var first=new AudioLayerPlayback(plan,0,0);var second=new AudioLayerPlayback(plan,0,0);
  first.Update(1,1,true);second.Update(1,0,true);
  Assert.IsTrue(first.Active);Assert.IsFalse(second.Active);
 }
 [TestMethod] public void ParentLayer_ValidatorRejectsBadTargetAndSourceConflict()
 {
  var child=SharedSong();var root=Nested(child);var action=new AudioParameter{Name="Actions",Maximum=100};root.Parameters.Add(action);
  var control=new AudioInstrument{LayerControl=new(){SubEventInstrumentId=root.MasterTrack.Instruments[0].Id,ChildTrackId=child.MasterTrack.Id,TriggerParameter=action}};
  root.Tracks.Add(new(){Name="Activity",Instruments=new(){control}});
  Assert.IsTrue(AudioEventValidator.Validate(root).IsValid);
  control.LayerControl.ChildTrackId=Guid.NewGuid();Assert.IsFalse(AudioEventValidator.Validate(root).IsValid);
  control.LayerControl.ChildTrackId=child.MasterTrack.Id;
  var conflictingSource=CreateSoundEvent();root.Sources.Add(conflictingSource);control.SourceId=conflictingSource.Id;
  Assert.IsFalse(AudioEventValidator.Validate(root).IsValid);
 }
 [TestMethod] public void ParentLayer_UsesChildClockWithoutRestartingOrSchedulingAudio()
 {
  var child=SharedSong();var root=Nested(child);root.SustainUntilStopped=true;
  var action=new AudioParameter{Name="Actions",Maximum=100};root.Parameters.Add(action);
  var control=new AudioInstrument{LayerControl=new(){SubEventInstrumentId=root.MasterTrack.Instruments[0].Id,ChildTrackId=child.MasterTrack.Id,TriggerParameter=action,ActiveGain=2}};
  root.Tracks.Add(new(){Name="Activity",Instruments=new(){control}});
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var instance=AudioSystem.GetEvent(root).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();_timeline.Advance(.1f);var before=instance.CaptureLiveSnapshot();
   session.Position=44100;instance.SetParameterByName("Actions",1);
   session.Position=88200;_timeline.Advance(.1f);_ = instance.PlaybackState;
   Assert.AreEqual(2f,session.LastMix.Gain(child.MasterTrack));
   root.Tracks[0].Volume=.5f;instance.RefreshAuthoredPropertiesForAudition();Assert.AreEqual(1f,session.LastMix.Gain(child.MasterTrack));
   instance.SetTrackMutedForAudition(root.Tracks[0],true);Assert.AreEqual(0f,session.LastMix.Gain(child.MasterTrack));
   instance.SetTrackMutedForAudition(root.Tracks[0],false);Assert.AreEqual(1f,session.LastMix.Gain(child.MasterTrack));
   Assert.AreEqual(0,session.SeekCalls);Assert.AreEqual(0,session.PhraseCalls);
   Assert.AreEqual(0f,child.MasterTrack.Instruments[0].StartTime);
   Assert.IsTrue(instance.CaptureLiveSnapshot().Voices.Any(v=>v.InstrumentId==control.Id&&v.Reason.Contains("sustain")));
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void ParentFlow_LoopKeepsChildSourceAndTriggerState()
 {
  var parent=Nested(SharedRotationSong());parent.SustainUntilStopped=true;
  var selector=parent.MasterTrack.Instruments[0];selector.RetainSubEventPositionOnTimelineJump=true;
  parent.TimelineDuration=20;parent.LoopRegions.Add(new(){Name="Build",Start=new(0),End=new(4)});
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();session.Position=441000;_timeline.Advance(5);_=instance.PlaybackState;
   Assert.AreEqual(441000L,session.Position);Assert.AreEqual(0,session.SeekCalls);Assert.AreEqual(0,session.Disposals);
   Assert.AreEqual(1f,instance.TimelinePosition,.01f);
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void ParentPhrase_LocalCountersCannotTriggerParentOwnedBank()
 {
  var song=SharedOverlaySong();song.Tracks[0].Overlay.ParentControlled=true;
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var instance=AudioSystem.GetEvent(song).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();instance.SetParameterByName(song.Tracks[0].Overlay.TriggerParameter.Name,1);
   session.Position=441000;_=instance.PlaybackState;Assert.AreEqual(0,session.PhraseCalls);
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void ParentCue_OutgoingSongFadesWithoutReceiptChildAndSurvivesFlowJump()
 {
  var parent=Nested(SharedRotationSong());parent.SustainUntilStopped=true;parent.TimelineDuration=30;
  var phase=new AudioParameter{Name="Phase",Maximum=1};parent.Parameters.Add(phase);
  var selector=parent.MasterTrack.Instruments[0];selector.SubEventExitFadeSeconds=1.2f;selector.RetainSubEventPositionOnTimelineJump=true;
  selector.SubEvents[0].Conditions.Add(new(){Parameter=phase,Minimum=0,Maximum=0});
  var receipt=AddSourceInstrument(parent, instrument =>
  {
   instrument.Name="Receipt";instrument.StartTime=16;instrument.TriggerDelaySeconds=0;instrument.TrimEnd=6;
   instrument.StartTriggerConditions=new(){new(){Parameter=phase,Minimum=1,Maximum=1}};
  });
  parent.MasterTrack.Instruments.Add(receipt);
  var marker=new DestinationMarker{Name="Receipt",Position=new(16)};parent.DestinationMarkers.Add(marker);
  parent.TransitionRegions.Add(new(){Name="To receipt",Start=new(0),End=new(16),DestinationMarkerId=marker.Id,
   Conditions=new(){Conditions=new(){new(){Parameter=phase,Comparison=AudioParameterComparison.Equal,Value=1}}}});
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();session.Position=44100;instance.SetParameterByName("Phase",1);_timeline.Advance(.01f);_=instance.PlaybackState;
   Assert.AreEqual(0,session.Disposals);Assert.AreEqual(16.01f,instance.TimelinePosition,.001f);
   Assert.IsTrue(_backend.Voices.Single().IsPlaying);Assert.AreEqual(1,selector.SubEvents.Count);
   session.Position+=26460;instance.RefreshAuthoredPropertiesForAudition();Assert.IsTrue(session.Volume>0&&session.Volume<1);
   instance.Paused=true;var volume=session.Volume;_timeline.Advance(2);instance.RefreshAuthoredPropertiesForAudition();Assert.AreEqual(volume,session.Volume);
   instance.Paused=false;session.Position+=44100;instance.RefreshAuthoredPropertiesForAudition();Assert.AreEqual(1,session.Disposals);
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void ParentPhrase_ParentCounterRequestsBankWithoutSourceMoves()
 {
  var child=SharedOverlaySong();child.Tracks[0].Overlay.ParentControlled=true;
  var ready=new AudioParameter{Name="Ready",Maximum=1};child.Parameters.Add(ready);
  child.Tracks[0].Overlay.Conditions.Conditions.Add(new(){Parameter=ready,Comparison=AudioParameterComparison.Equal,Value=1});
  var parent=Nested(child);parent.SustainUntilStopped=true;parent.Parameters.AddRange(child.Parameters);
  var counter=child.Tracks[0].Overlay.TriggerParameter;
  var controller=new AudioInstrument{LayerControl=new(){Mode=AudioMusicControlMode.TriggerPhraseBank,
   SubEventInstrumentId=parent.MasterTrack.Instruments[0].Id,ChildTrackId=child.Tracks[0].Id,TriggerParameter=counter}};
  parent.Tracks.Add(new(){Name="Parent success",Instruments=new(){controller}});
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  session.PhraseFactory=owner=>System.Threading.Tasks.Task.FromResult(new AudioMusicPcmSession.Scheduled(session.Position+26460,System.Threading.Tasks.Task.FromResult(true)));
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();_=instance.PlaybackState;instance.SetParametersByName(new Dictionary<string,float>{{counter.Name,1},{"Ready",1}});
   Assert.AreEqual(1,session.PhraseCalls);Assert.AreEqual(0,session.SeekCalls);
   Assert.IsTrue(instance.CaptureLiveSnapshot().Voices.Any(v=>v.InstrumentId==controller.Id && v.Reason.Contains("changed to 1")));
   instance.SetTrackMutedForAudition(parent.Tracks[0],true);Assert.AreEqual(0f,session.RequestedOwner.Gain);
   instance.SetTrackMutedForAudition(parent.Tracks[0],false);Assert.IsTrue(session.RequestedOwner.Gain>0);
   Assert.IsTrue(instance.CaptureLiveSnapshot().Voices.Any(v=>v.InstrumentId==controller.Id&&v.State=="Scheduled"));
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void ParentPhrase_RejectedEntryExplainsWhyWithoutQueuingOldSuccess()
 {
  var child=SharedOverlaySong();var bank=child.Tracks[0];bank.Overlay.ParentControlled=true;
  foreach(var clip in bank.Instruments){clip.OverlayBars=new(){1};clip.MaximumMusicWaitBeats=1;}
  var parent=Nested(child);parent.SustainUntilStopped=true;parent.Parameters.AddRange(child.Parameters);
  var counter=bank.Overlay.TriggerParameter;
  var controller=new AudioInstrument{LayerControl=new(){Mode=AudioMusicControlMode.TriggerPhraseBank,
   SubEventInstrumentId=parent.MasterTrack.Instruments[0].Id,ChildTrackId=bank.Id,TriggerParameter=counter}};
  parent.Tracks.Add(new(){Name="Parent success",Instruments=new(){controller}});
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();_=instance.PlaybackState;session.Position=44100;
   instance.SetParameterByName(counter.Name,1);
   Assert.AreEqual(0,session.PhraseCalls);
   var voice=instance.CaptureLiveSnapshot().Voices.Single(v=>v.InstrumentId==controller.Id);
   StringAssert.Contains(voice.Reason,"no complete compatible phrase");
   session.Position+=44100;_=instance.PlaybackState;
   Assert.AreEqual(0,session.PhraseCalls,"Rejected requests must not become stale delayed responses.");
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void ParentControl_ChildFileDisambiguatesCopiedTrackIds()
 {
  var child=SharedOverlaySong();child.Tracks[0].Overlay.ParentControlled=true;
  var other=SharedSong();
  var unrelated=new AudioTrack{Id=child.Tracks[0].Id,Name="Unrelated part"};
  unrelated.Instruments.Add(AddSourceInstrument(other));
  other.Tracks.Add(unrelated);
  other.Tracks[0].Instruments[0].TrimEnd=2.4f;
  var parent=Nested(child);parent.Parameters.AddRange(child.Parameters);
  parent.MasterTrack.Instruments[0].SubEvents.Add(new(){Event=other});
  var rule=new AudioMusicLayerControl{Mode=AudioMusicControlMode.TriggerPhraseBank,SubEventInstrumentId=parent.MasterTrack.Instruments[0].Id,ChildTrackId=child.Tracks[0].Id,TriggerParameter=child.Tracks[0].Overlay.TriggerParameter};
  parent.Tracks.Add(new(){Name="Scoped control",Instruments=new(){new(){LayerControl=rule}}});
  Assert.IsFalse(AudioEventValidator.Validate(parent).IsValid);
  rule.ChildEvent=child;Assert.IsTrue(AudioEventValidator.Validate(parent).IsValid);
  rule.ChildEvent=other;Assert.IsFalse(AudioEventValidator.Validate(parent).IsValid);
 }
 [TestMethod] public void ParentLayer_InactiveBuildPhaseDoesNotDelayFirstServiceAction()
 {
  var state=new AudioLayerPlayback(new(4,3,4,.2f,1,.1f,.5f),0,0);
  state.Update(1,0,false);state.Update(2,0,false);
  state.Update(2.1,1,true);Assert.IsTrue(state.Active);
 }
}
