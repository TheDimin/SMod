using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void MusicalHandoffReleasesLeadBeforeBackingAndKeepsRhythm()
 {
  Assert.IsTrue(AudioMusicalHandoff.Gain(AudioTransitionRole.Lead,7,0,8,2)<1);
  Assert.AreEqual(1,AudioMusicalHandoff.Gain(AudioTransitionRole.Backing,7,0,8,2),.001f);
  Assert.AreEqual(1,AudioMusicalHandoff.Gain(AudioTransitionRole.Rhythm,7.5f,0,8,2),.001f);
  foreach(var role in Enum.GetValues<AudioTransitionRole>())Assert.AreEqual(0,AudioMusicalHandoff.Gain(role,8,0,8,2),.001f);
 }
 [TestMethod] public void MusicalHandoffEntersBackingBeforeSolosAndDoesNotBoostGain()
 {
  Assert.IsTrue(AudioMusicalHandoff.Gain(AudioTransitionRole.Backing,.5f,2,0,0)>AudioMusicalHandoff.Gain(AudioTransitionRole.Lead,.5f,2,0,0));
  foreach(var role in Enum.GetValues<AudioTransitionRole>())
  {
   Assert.AreEqual(0,AudioMusicalHandoff.Gain(role,0,2,0,0),.001f);
   Assert.AreEqual(1,AudioMusicalHandoff.Gain(role,4,2,0,0),.001f);
  }
 }
 [TestMethod] public void MusicIntensityChangesNeverChooseAnotherEligibleSong()
 {
  var intensity=new AudioParameter{Name="Intensity",Minimum=0,Maximum=1};
  var a=CreateEvent();a.Parameters.Add(intensity);a.TimelineDuration=100;
  var b=CreateEvent();b.Parameters.Add(intensity);b.TimelineDuration=100;
  var parent=Nested(a);parent.Parameters.Add(intensity);
  var block=parent.MasterTrack.Instruments[0];block.RotateSubEventsAtEnd=true;block.SubEventTransitionBeats=4;block.SubEvents.Add(new(){Event=b});
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.Start();
  var id=instance.InstrumentPlaybackStates.Single().ChildInstanceId;
  foreach(var value in new[]{0f,1f,.5f,.1f,.9f}){instance.SetParameterByName("Intensity",value);Assert.AreEqual(id,instance.InstrumentPlaybackStates.Single().ChildInstanceId);}
  instance.Stop(AudioStopMode.Immediate);
 }

 [TestMethod] public void MusicalHandoffUsesNativeVoiceGainAndCancelsOnPhaseReversal()
 {
  var phase=new AudioParameter{Name="Phase",Minimum=0,Maximum=1};
  var a=CreateEvent();foreach(var t in a.Tracks.Prepend(a.MasterTrack))t.TransitionRole=AudioTransitionRole.Lead;
  a.BeatGrids.Add(new(){Name="Song",Start=new(0),End=new(8),BeatsPerMinute=120,BeatsPerBar=4});
  var parent=Nested(a);parent.Parameters.Add(phase);var block=parent.MasterTrack.Instruments[0];
  block.SubEventQuantization=TransitionQuantization.NextBar;block.SubEventTransitionBeats=1;
  block.SubEvents[0].Conditions.Add(new(){Parameter=phase,Minimum=0,Maximum=0});
  block.SubEvents.Add(new(){Event=CreateEvent(),Conditions=new(){new(){Parameter=phase,Minimum=1,Maximum=1}}});
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.Start();
  var full=_backend.Voices[0].Volume;
  _timeline.Advance(.1f);instance.SetParameterByName("Phase",1);_timeline.Advance(1.65f);
  _=instance.PlaybackState;
  Assert.IsTrue(_backend.Voices[0].Volume<full*.9f,"The outgoing lead must fade before the phase boundary.");
  instance.SetParameterByName("Phase",0);_=instance.PlaybackState;
  Assert.AreEqual(full,_backend.Voices[0].Volume,.001f);
  instance.Stop(AudioStopMode.Immediate);
 }
}
