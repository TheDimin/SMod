using Sandbox;
using Smod;

public sealed partial class AudioEventTests
{
 [TestMethod]
 public void BeatGrid_MapsOneBasedBarsAndFractionalBeats()
 {
  var grid=new AudioBeatGrid {Start=new(10),End=new(30),BeatsPerMinute=120,BeatsPerBar=3};
  Assert.AreEqual(10d,grid.TimeAt(1).Seconds);
  Assert.AreEqual(11.75d,grid.TimeAt(2,1.5).Seconds);
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>grid.TimeAt(0));
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>grid.TimeAt(1,4));
 }
 AudioEvent BeatEvent(TransitionQuantization quantization)
 {
  var e=CreateEvent();
  e.BeatGrids.Add(new(){Name="Source",Start=new(2),End=new(6),BeatsPerMinute=120,BeatsPerBar=4,BarsPerPhrase=2});
  e.BeatGrids.Add(new(){Name="Target",Start=new(10),End=new(20),BeatsPerMinute=60});
  var target=new DestinationMarker {Name="Target",Position=new(10)};
  e.DestinationMarkers.Add(target);
  e.TransitionRegions.Add(new(){Start=new(2),End=new(6),DestinationMarkerId=target.Id,Quantization=quantization});
  return e;
 }
 [TestMethod]
 public void BeatTransition_UsesSectionOriginAndTempo()
 {
  var e=BeatEvent(TransitionQuantization.NextBeat);
  var i=AudioSystem.GetEvent(e).CreateInstance();i.TimelinePosition=2.2f;i.Start();
  _timeline.Advance(.1f);
  Assert.AreEqual(2.3f,i.TimelinePosition,.0001f);
  Assert.AreEqual(120000L,i.TimelineDebugSnapshot.ScheduledExecutionSample);
  _timeline.Advance(.25f);
  Assert.AreEqual(10.05f,i.TimelinePosition,.0001f);
  Assert.AreEqual(1f,i.TransitionGain);
 }
 [TestMethod]
 public void BarTransition_UsesSectionMeter()
 {
  var e=BeatEvent(TransitionQuantization.NextBar);e.BeatGrids[0].BeatsPerBar=3;
  var i=AudioSystem.GetEvent(e).CreateInstance();i.TimelinePosition=2.2f;i.Start();
  _timeline.Advance(.1f);
  Assert.AreEqual(168000L,i.TimelineDebugSnapshot.ScheduledExecutionSample);
  _timeline.Advance(1.3f);
  Assert.AreEqual(10.1f,i.TimelinePosition,.0001f);
 }
 [TestMethod]
 public void PhraseTransition_AtLoopEndWinsAndPreservesOvershoot()
 {
  var e=BeatEvent(TransitionQuantization.NextPhrase);
  e.LoopRegions.Add(new(){Start=new(2),End=new(6)});
  var i=AudioSystem.GetEvent(e).CreateInstance();i.TimelinePosition=2.1f;i.Start();
  _timeline.Advance(3.8f);
  Assert.AreEqual(5.9f,i.TimelinePosition,.0001f);
  _timeline.Advance(.3f);
  Assert.AreEqual(10.2f,i.TimelinePosition,.0001f);
  Assert.IsFalse(i.TimelineDebugSnapshot.JumpLimitExceeded);
 }
 [TestMethod]
 public void BeatFades_UseSourceAndDestinationTempo_AndPreserveMix()
 {
  var e=BeatEvent(TransitionQuantization.NextBeat);
  e.TransitionRegions[0].FadeOutBeats=1;e.TransitionRegions[0].FadeInBeats=1;
  var i=AudioSystem.GetEvent(e).CreateInstance();i.Volume=.4f;i.TimelinePosition=2;i.Start();
  _timeline.Advance(.25f);
  Assert.AreEqual(.5f,i.TransitionGain,.001f);
  Assert.AreEqual(.2f,_backend.Voices[0].Volume,.001f);
  _timeline.Advance(.25f);
  Assert.AreEqual(10f,i.TimelinePosition,.001f);
  Assert.AreEqual(0f,i.TransitionGain,.001f);
  _timeline.Advance(.5f);
  Assert.AreEqual(.5f,i.TransitionGain,.001f);
  i.Paused=true;_timeline.Advance(2);
  Assert.AreEqual(.5f,i.TransitionGain,.001f);
  i.Paused=false;_timeline.Advance(.5f);
  Assert.AreEqual(1f,i.TransitionGain,.001f);
  Assert.AreEqual(.4f,i.Volume);
  Assert.AreEqual(1f,_backend.Voices[0].Pitch);
  i.TimelinePosition=2;
  Assert.AreEqual(1f,i.TransitionGain);
 }
 [TestMethod]
 public void BeatFade_CancelledRequestRestoresGainAndKeepsSource()
 {
  var e=BeatEvent(TransitionQuantization.NextBeat);var p=AddLabeledState(e);
  e.TransitionRegions[0].Conditions=LabelCondition(p,"combat");e.TransitionRegions[0].FadeOutBeats=1;
  var i=AudioSystem.GetEvent(e).CreateInstance();i.TimelinePosition=2;i.SetParameterByNameWithLabel("MusicState","combat");i.Start();
  _timeline.Advance(.25f);Assert.AreEqual(.5f,i.TransitionGain,.001f);
  i.SetParameterByNameWithLabel("MusicState","exploration");_timeline.Advance(.1f);
  Assert.AreEqual(1f,i.TransitionGain);
  Assert.AreEqual(2.35f,i.TimelinePosition,.001f);
 }
 [TestMethod]
 public void BeatFade_LateRequestUsesRemainingTime()
 {
  var e=BeatEvent(TransitionQuantization.NextBeat);e.TransitionRegions[0].FadeOutBeats=1;
  var i=AudioSystem.GetEvent(e).CreateInstance();i.TimelinePosition=2.4f;i.Start();
  _timeline.Advance(.05f);Assert.AreEqual(.5f,i.TransitionGain,.001f);
  _timeline.Advance(.06f);Assert.AreEqual(10.01f,i.TimelinePosition,.001f);
 }
 [TestMethod]
 public void PendingPhrase_DoesNotBlockAnEarlierAuthoredMarker()
 {
  var e=BeatEvent(TransitionQuantization.NextPhrase);
  e.TransitionMarkers.Add(new(){Position=new(3),DestinationMarkerId=e.DestinationMarkers[0].Id});
  var i=AudioSystem.GetEvent(e).CreateInstance();i.TimelinePosition=2;i.Start();
  _timeline.Advance(.25f);Assert.AreNotEqual(Guid.Empty,i.TimelineDebugSnapshot.PendingTransitionId);
  _timeline.Advance(1);
  Assert.AreEqual(10.25f,i.TimelinePosition,.001f);
 }
 [TestMethod]
 public void BeatValidation_RejectsOverlapMissingTempoAndInvalidFades()
 {
  var e=BeatEvent(TransitionQuantization.NextPhrase);
  e.BeatGrids.Add(new(){Name="Overlap",Start=new(3),End=new(5)});
  e.TransitionRegions[0].FadeOutBeats=float.NaN;e.TransitionRegions[0].FadeInBeats=-1;
  var issues=AudioEventValidator.Validate(e).Issues;
  Assert.IsTrue(issues.Any(x=>x.Message.Contains("overlap")));
  Assert.IsTrue(issues.Any(x=>x.Path.EndsWith("/FadeOutBeats")));
  Assert.IsTrue(issues.Any(x=>x.Path.EndsWith("/FadeInBeats")));
  e=BeatEvent(TransitionQuantization.NextPhrase);e.BeatGrids.Clear();
  Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
 }
}
