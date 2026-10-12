using Sandbox;
using Smod;
public sealed partial class AudioEventTests
{
 AudioEvent PlaylistEvent(bool rotate=false)
 {
  var e=CreateEvent();
  var world=new AudioParameter {Name="WorldState",Type=AudioParameterType.Discrete,Minimum=0,Maximum=1};
  e.Parameters.Add(world);e.MusicPlaylist=new(){ContextParameter=world,VariationBars=1};
  for(var n=0;n<2;n++)
  {
   var marker=new DestinationMarker {Name="Section"+n,Position=new(n*20)};e.DestinationMarkers.Add(marker);
   e.BeatGrids.Add(new(){Name="Grid"+n,Start=new(n*20),End=new(n*20+8),BeatsPerMinute=120,BeatsPerBar=4,BarsPerPhrase=1});
   e.LoopRegions.Add(new(){Start=new(n*20),End=new(n*20+8)});
   var conditions=new ParameterConditionSet();conditions.Conditions.Add(new(){Parameter=world,Comparison=AudioParameterComparison.Equal,Value=n});
   e.MusicPlaylist.Sections.Add(new(){DestinationMarkerId=marker.Id,Conditions=rotate ? new() : conditions,EntryConditions=conditions,RotateAtEnd=rotate});
  }
  return e;
 }
 [TestMethod]
 public void Music_WorldStateSelectsInitialSection()
 {
  var i=AudioSystem.GetEvent(PlaylistEvent()).CreateInstance();i.SetParameterByName("WorldState",1);i.Start();
  Assert.AreEqual("Section1",i.CurrentMusicSection);Assert.AreEqual(20f,i.TimelinePosition,.001f);
 }
 [TestMethod]
 public void Music_StateChangeWaitsForCompositionEnd()
 {
  var e=PlaylistEvent();var i=AudioSystem.GetEvent(e).CreateInstance();i.Start();i.SetParameterByName("WorldState",1);
  _timeline.Advance(3);Assert.AreEqual(3f,i.TimelinePosition,.001f);
  _timeline.Advance(4.9f);Assert.AreEqual(7.9f,i.TimelinePosition,.001f);
  _timeline.Advance(.2f);Assert.AreEqual(20.1f,i.TimelinePosition,.002f);
  Assert.AreEqual(0,e.TransitionRegions.Count,"Instance choices must not modify the resource.");
 }
 [TestMethod]
 public void Music_ReversedStateCancelsPendingChange()
 {
  var i=AudioSystem.GetEvent(PlaylistEvent()).CreateInstance();i.Start();i.SetParameterByName("WorldState",1);
  _timeline.Advance(7);_=i.TimelinePosition;i.SetParameterByName("WorldState",0);
  _timeline.Advance(1.1f);Assert.AreEqual(.1f,i.TimelinePosition,.002f);Assert.IsNull(i.PendingMusicSection);
 }
 [TestMethod]
 public void Music_RotatesWithoutSongParameter_AndInstancesAreIndependent()
 {
  var e=PlaylistEvent(true);var a=AudioSystem.GetEvent(e).CreateInstance();var b=AudioSystem.GetEvent(e).CreateInstance();
  a.Start();b.SetParameterByName("WorldState",1);b.Start();b.Paused=true;
  _timeline.Advance(7);_=a.TimelinePosition;_timeline.Advance(1.1f);
  Assert.AreEqual(20.1f,a.TimelinePosition,.002f);Assert.AreEqual(20f,b.TimelinePosition,.001f);
  Assert.AreEqual(0,e.TransitionRegions.Count);
 }
 [TestMethod]
 public void Music_VariationChangesOnBarsAndPauseHolds()
 {
  var i=AudioSystem.GetEvent(PlaylistEvent()).CreateInstance();i.Start();
  _timeline.Advance(.1f);_=i.TimelinePosition;
  _timeline.Advance(2);_=i.TimelinePosition;
  _timeline.Advance(.6f);_=i.TimelinePosition;
  Assert.IsTrue(i.MusicVariation>0);
  i.Paused=true;var balance=i.MusicVariation;_timeline.Advance(5);_=i.PlaybackState;
  Assert.AreEqual(balance,i.MusicVariation);
 }
 [TestMethod]
 public void Music_PreparesOnlyTheActiveComposition()
 {
  var e=PlaylistEvent();
  e.Tracks.Add(new(){Name="Target",Instruments=new(){AddSourceInstrument(e, instrument => { instrument.StartTime=20;instrument.TrimEnd=8; })}});
  var i=AudioSystem.GetEvent(e).CreateInstance();i.Start();
  Assert.AreEqual(1,_backend.Voices.Count);
  i.SetParameterByName("WorldState",1);_timeline.Advance(7);_=i.TimelinePosition;_timeline.Advance(1.1f);_=i.TimelinePosition;
  Assert.AreEqual(2,_backend.Voices.Count);Assert.IsFalse(_backend.Voices[0].IsPlaying);Assert.IsTrue(_backend.Voices[1].IsPlaying);
 }
 [TestMethod]
 public void Music_NonRotatingSectionKeepsSongAcrossIntensityChangesAndRepeatedLoops()
 {
  var e=PlaylistEvent();
  e.Parameters.Add(new(){Name="Intensity",Minimum=0,Maximum=1});
  var i=AudioSystem.GetEvent(e).CreateInstance();i.Start();
  for(var loop=0;loop<5;loop++)
  {
   i.SetParameterByName("Intensity",loop%2);
   _timeline.Advance(7);_=i.TimelinePosition;
   Assert.IsNull(i.PendingMusicSection);
   _timeline.Advance(1);_=i.TimelinePosition;
   Assert.AreEqual("Section0",i.CurrentMusicSection);
   Assert.IsNull(i.PendingMusicSection);
  }
 }
 [TestMethod]
 public void Music_ValidatorRejectsMissingDestinationAndInvalidBalance()
 {
  var e=PlaylistEvent();e.MusicPlaylist.Sections[0].DestinationMarkerId=Guid.NewGuid();
  e.Tracks.Add(new(){MusicVariationGains=new(){float.NaN}});
  var result=AudioEventValidator.Validate(e);Assert.IsFalse(result.IsValid);
  Assert.IsTrue(result.Issues.Any(x=>x.Message.Contains("destination markers")));
  Assert.IsTrue(result.Issues.Any(x=>x.Message.Contains("music gains")));
 }
}

