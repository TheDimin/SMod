using Smod;
public sealed partial class AudioEventTests
{
 static AudioEvent SharedSong()
 {
  var e=CreateEvent();e.SharedMusicOutput=true;e.TimelineDuration=2.4f;
  e.MasterTrack.Instruments[0].TrimEnd=2.4f;
  e.BeatGrids.Add(new(){Name="Song",End=new(2.4),BeatsPerMinute=100});
  e.LoopRegions.Add(new(){Name="Full song",End=new(2.4)});
  return e;
 }
 [TestMethod] public void SharedEvent_DelayedUpdateFoldsCompleteLoopsWithoutJumpLimit()
 {
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var instance=AudioSystem.GetEvent(SharedSong()).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();session.Position=105840L*20+22050;
   Assert.AreEqual(.5f,instance.TimelinePosition,.00001f);
   Assert.AreEqual(.5f,instance.CaptureSharedMusicMix().Position,.00001f);
   Assert.AreEqual(0,session.SeekCalls,"Catch-up must not restart the audio clock.");
   session.Position+=22050;Assert.AreEqual(1f,instance.TimelinePosition,.00001f);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void SharedEvent_RepeatedExactLoopBoundariesKeepMixPositionNonnegative()
 {
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var instance=AudioSystem.GetEvent(SharedSong()).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();
   for(var loop=1;loop<=100;loop++)
   {
    session.Position=105840L*loop;
    Assert.AreEqual(0f,instance.TimelinePosition,.00001f);
    Assert.AreEqual(0f,instance.CaptureSharedMusicMix().Position,.00001f);
   }
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void SharedChild_ResumeEntryIsSilentWhileItsClockStillStartsAtZero()
 {
  Assert.AreEqual(0f,AudioMusicalHandoff.Gain(AudioTransitionRole.Backing,-14,.15f,0,0));
  Assert.AreEqual(0f,AudioMusicalHandoff.Gain(AudioTransitionRole.Lead,-14,.15f,0,0));
 }
 [TestMethod] public void SharedChild_OptInResumeReturnsToSavedServicePosition()
 {
  var phase=new AudioParameter{Name="Phase",Minimum=0,Maximum=1};
  var parent=Nested(SharedRotationSong());parent.Parameters.Add(phase);
  var choice=parent.MasterTrack.Instruments[0].SubEvents[0];choice.ResumePlayback=true;choice.Conditions.Add(new(){Parameter=phase,Minimum=0,Maximum=0});
  parent.MasterTrack.Instruments[0].SubEvents.Add(new(){Event=SharedRotationSong(),Conditions=new(){new(){Parameter=phase,Minimum=1,Maximum=1}}});
  var sessions=new[]{new EventMusicSessionFake(),new EventMusicSessionFake(),new EventMusicSessionFake()};
  foreach(var session in sessions)session.Loaded.SetResult(true);
  var next=0;var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>sessions[next++];
  try
  {
   instance.Start();sessions[0].Position=441000;
   instance.SetParameterByName("Phase",1);Assert.AreEqual(1,sessions[0].Disposals);
   instance.SetParameterByName("Phase",0);
   Assert.AreEqual(441000L,sessions[2].Position,"The next service phase must not replay the song opening.");
   Assert.IsFalse(sessions[2].Paused);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void SharedEvent_OwnsLoadingClockPauseFactsMuteAndImmediateStop()
 {
  var e=SharedSong();var fact=new AudioParameter{Name="Pressure",Minimum=0,Maximum=1};e.Parameters.Add(fact);
  var session=new EventMusicSessionFake();var instance=AudioSystem.GetEvent(e).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();Assert.AreEqual(AudioPlaybackState.Starting,instance.PlaybackState);Assert.AreEqual(0,_backend.Voices.Count);
   _timeline.Advance(10);Assert.AreEqual(0f,instance.TimelinePosition);
   instance.SetParameterByName("Pressure",1);instance.Volume=.2f;instance.Paused=true;
   session.Loaded.SetResult(true);Assert.AreEqual(AudioPlaybackState.Playing,instance.PlaybackState);
   Assert.AreEqual(1f,session.LastParameters[fact].Number);Assert.AreEqual(.2f,session.Volume);Assert.IsTrue(session.Paused);
   instance.Paused=false;session.Position=22050;Assert.AreEqual(.5f,instance.TimelinePosition);
   _timeline.Advance(5);Assert.AreEqual(.5f,instance.TimelinePosition,"Wall time must not move the music clock.");
   instance.SetTrackMutedForAudition(e.MasterTrack,true);Assert.AreEqual(0f,session.LastMix.Gain(e.MasterTrack));
   instance.Stop(AudioStopMode.Immediate);Assert.AreEqual(1,session.Disposals);Assert.AreEqual(AudioPlaybackState.Stopped,instance.PlaybackState);
  }
  finally {if(instance.PlaybackState!=AudioPlaybackState.Stopped)instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void SharedEvent_FullSongLoopPreservesTheOutputClockAndSeekMovesIt()
 {
  var e=SharedSong();var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var instance=AudioSystem.GetEvent(e).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();session.Position=105840;Assert.AreEqual(0f,instance.TimelinePosition,.000001f);session.Position=110250;Assert.AreEqual(.1f,instance.TimelinePosition,.0001f);
   Assert.AreEqual(0,session.SeekCalls,"A natural loop must not reset the sample clock.");
   instance.TimelinePosition=1;Assert.AreEqual(44100L,session.Position);Assert.AreEqual(1,session.SeekCalls);
   Assert.AreEqual(1f,instance.TimelinePosition);Assert.AreEqual(0,_backend.Voices.Count);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void SharedEvent_StopDuringLoadPreventsLatePlayback()
 {
  var session=new EventMusicSessionFake();var instance=AudioSystem.GetEvent(SharedSong()).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  instance.Start();instance.Stop(AudioStopMode.Immediate);session.Loaded.SetResult(true);
  Assert.AreEqual(AudioPlaybackState.Stopped,instance.PlaybackState);Assert.AreEqual(1,session.Disposals);Assert.AreEqual(0,_backend.Voices.Count);instance.Release();
 }

 [TestMethod] public void SharedEvent_FadeCanFinishAfterReleaseAndRejectsUnsupportedInstanceControls()
 {
  var e=SharedSong();e.FadeOutDuration=.5f;var session=new EventMusicSessionFake{FadeGate=new()};session.Loaded.SetResult(true);
  var instance=AudioSystem.GetEvent(e).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  instance.Start();Assert.ThrowsExactly<InvalidOperationException>(()=>instance.Release());
  Assert.ThrowsExactly<InvalidOperationException>(()=>instance.Pitch=1.2f);
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>instance.Volume=2);
  instance.Stop(AudioStopMode.AllowFadeout);Assert.AreEqual(AudioPlaybackState.Stopping,instance.PlaybackState);
  instance.Release();session.FadeGate.SetResult(true);Assert.AreEqual(1,session.Disposals);Assert.AreEqual(AudioPlaybackState.Stopped,instance.PlaybackState);
 }

 [TestMethod] public void SharedChild_ReceivesParentFactsDuringLoadBeforeOutputStarts()
 {
  var song=SharedSong();var pressure=new AudioParameter{Name="Pressure",Minimum=0,Maximum=1};song.Parameters.Add(pressure);
  var parent=Nested(song);parent.Parameters.Add(pressure);var session=new EventMusicSessionFake();
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();instance.SetParameterByName("Pressure",1);instance.Volume=.3f;
   session.Loaded.SetResult(true);Assert.AreEqual(1f,session.LastParameters[pressure].Number);
   Assert.AreEqual(.3f,session.Volume);Assert.AreEqual(0,_backend.Voices.Count);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod]
 [DataRow(false)]
 [DataRow(true)]
 public void SharedChild_PhaseChangeWaitsForPreparationThenForTheChildClock(bool entryOverride)
 {
  var phase=new AudioParameter{Name="Phase",Minimum=0,Maximum=1};
  var a=SharedSong();var b=SharedSong();var parent=Nested(a);parent.Parameters.Add(phase);
  var instrument=parent.MasterTrack.Instruments[0];instrument.SubEventQuantization=entryOverride?TransitionQuantization.NextPhrase:TransitionQuantization.NextBeat;
  instrument.SubEventTransitionBeats=0;instrument.RotateSubEventsAtEnd=true;
  instrument.SubEvents[0].Conditions.Add(new(){Parameter=phase,Minimum=0,Maximum=0});
  instrument.SubEvents.Add(new(){Event=b,Conditions=new(){new(){Parameter=phase,Minimum=1,Maximum=1}}});
  if(entryOverride){instrument.SubEvents[1].EntryQuantization=TransitionQuantization.NextBeat;instrument.SubEvents[1].EntryFadeBeats=0;}
  var old=new EventMusicSessionFake();old.Loaded.SetResult(true);var next=new EventMusicSessionFake();
  var sessions=new Queue<EventMusicSessionFake>(new[]{old,next});
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>sessions.Dequeue();
  try
  {
   instance.Start();var original=instance.InstrumentPlaybackStates.Single().ChildInstanceId;
   old.Position=4410;instance.SetParameterByName("Phase",1);
   Assert.IsTrue(next.Paused);Assert.AreEqual(0,old.Disposals);
   _timeline.Advance(5);Assert.AreEqual(original,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
   old.Position=110250;instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreEqual(original,instance.InstrumentPlaybackStates.Single().ChildInstanceId,"A phase load must remain gated at song end.");
   next.Loaded.SetResult(true);instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreEqual(original,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
   _timeline.Advance(5);Assert.AreEqual(original,instance.InstrumentPlaybackStates.Single().ChildInstanceId,"Parent wall time must not end the child beat wait.");
   old.Position=132300;instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreNotEqual(original,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
   Assert.AreEqual(1,old.Disposals);Assert.IsFalse(next.Paused);Assert.AreEqual(0,sessions.Count);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
  Assert.AreEqual(1,next.Disposals);
 }
 [TestMethod] public void SharedChild_StateReversalCancelsLoadingReplacement()
 {
  var phase=new AudioParameter{Name="Phase",Minimum=0,Maximum=1};
  var parent=Nested(SharedSong());parent.Parameters.Add(phase);var instrument=parent.MasterTrack.Instruments[0];
  instrument.SubEvents[0].Conditions.Add(new(){Parameter=phase,Minimum=0,Maximum=0});
  instrument.SubEvents.Add(new(){Event=SharedSong(),Conditions=new(){new(){Parameter=phase,Minimum=1,Maximum=1}}});
  var old=new EventMusicSessionFake();old.Loaded.SetResult(true);var next=new EventMusicSessionFake();
  var sessions=new Queue<EventMusicSessionFake>(new[]{old,next});
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>sessions.Dequeue();
  try
  {
   instance.Start();var original=instance.InstrumentPlaybackStates.Single().ChildInstanceId;
   instance.SetParameterByName("Phase",1);instance.SetParameterByName("Phase",0);
   Assert.AreEqual(1,next.Disposals);next.Loaded.SetResult(true);
   Assert.AreEqual(original,instance.InstrumentPlaybackStates.Single().ChildInstanceId);Assert.AreEqual(0,old.Disposals);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 static AudioEvent SharedRotationSong()
 {
  var song=SharedSong();song.TimelineDuration=24; song.MasterTrack.Instruments[0].TrimEnd=24;
  song.BeatGrids[0].End=new(24);song.LoopRegions[0].End=new(24);return song;
 }
 [TestMethod]
 [DataRow(false)]
 [DataRow(true)]
 public void SharedChild_SongEndPreparesOneChoiceAndLateLoadWaitsForAnotherFullLoop(bool late)
 {
  var parent=Nested(SharedRotationSong());var instrument=parent.MasterTrack.Instruments[0];
  instrument.SubEvents.Add(new(){Event=SharedRotationSong()});instrument.RotateSubEventsAtEnd=true;
  instrument.SubEventTransitionBeats=1;
  var current=new EventMusicSessionFake();current.Loaded.SetResult(true);var next=new EventMusicSessionFake();
  var sessions=new Queue<EventMusicSessionFake>(new[]{current,next});
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>sessions.Dequeue();
  try
  {
   instance.Start();var original=instance.InstrumentPlaybackStates.Single().ChildInstanceId;
   current.Position=970200;instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreEqual(0,sessions.Count,"Only the current and one prepared song may load.");Assert.IsTrue(next.Paused);
   instance.Volume=.4f;Assert.AreEqual(original,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
   if(late)
   {
    current.Position=1062810;instance.RefreshAuthoredPropertiesForAudition();
    Assert.AreEqual(original,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
    Assert.AreEqual(0,current.Disposals,"A missed load deadline must keep the current composition running.");
   }
   next.Loaded.SetResult(true);instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreEqual(original,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
   _timeline.Advance(10);Assert.AreEqual(original,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
   current.Position=late ? 2116800 : 1058400;instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreNotEqual(original,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
   Assert.AreEqual(1,current.Disposals);Assert.IsFalse(next.Paused);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
  Assert.AreEqual(1,next.Disposals);
 }
 [TestMethod] public void SharedChild_ParentStopReleasesSongEndPreparationBeforeLateCompletion()
 {
  var parent=Nested(SharedRotationSong());var instrument=parent.MasterTrack.Instruments[0];
  instrument.SubEvents.Add(new(){Event=SharedRotationSong()});instrument.RotateSubEventsAtEnd=true;
  var current=new EventMusicSessionFake();current.Loaded.SetResult(true);var next=new EventMusicSessionFake();
  var sessions=new Queue<EventMusicSessionFake>(new[]{current,next});
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.SharedMusicSessionFactory=()=>sessions.Dequeue();
  instance.Start();current.Position=970200;instance.RefreshAuthoredPropertiesForAudition();
  instance.Stop(AudioStopMode.Immediate);instance.Release();next.Loaded.SetResult(true);
  Assert.AreEqual(1,current.Disposals);Assert.AreEqual(1,next.Disposals);
  Assert.AreEqual(AudioPlaybackState.Stopped,instance.PlaybackState);
 }
 [TestMethod] public void SharedChild_EntryTimingRejectsInvalidAuthoring()
 {
  var parent=Nested(SharedSong());var choice=parent.MasterTrack.Instruments[0].SubEvents[0];
  choice.EntryQuantization=TransitionQuantization.NextBeat;choice.EntryFadeBeats=.25f;
  Assert.IsTrue(AudioEventValidator.Validate(parent).IsValid);
  choice.EntryFadeBeats=float.NaN;Assert.IsFalse(AudioEventValidator.Validate(parent).IsValid);
  choice.EntryFadeBeats=-1;Assert.IsFalse(AudioEventValidator.Validate(parent).IsValid);
  choice.EntryFadeBeats=.25f;choice.EntryQuantization=(TransitionQuantization)999;
  Assert.IsFalse(AudioEventValidator.Validate(parent).IsValid);
 }
 [TestMethod] public void SharedEvent_ValidationAcceptsShortClipsAndRejectsClipsOutsideTheSong()
 {
  var e=SharedSong();Assert.IsTrue(AudioEventValidator.Validate(e).IsValid);
  e.MasterTrack.Instruments[0].TrimEnd=1;Assert.IsTrue(AudioEventValidator.Validate(e).IsValid);
  e.MasterTrack.Instruments[0].StartTime=2;Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
  e=SharedSong();e.LoopRegions[0].End=new(1);Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
  e=SharedSong();e.MasterTrack.Overlay=new(){Enabled=true};Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
 }
}
