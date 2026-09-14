using Smod;
public sealed partial class AudioEventTests
{
 AudioEvent OverlayEvent()
 {
  var e=PlaylistEvent();
  var count=new AudioParameter{Name="ServiceCount",Minimum=0,Maximum=100};e.Parameters.Add(count);
  e.Tracks.Add(new(){Name="Phrases",Overlay=new(){Enabled=true,TriggerParameter=count,BeatsPerMinute=120,CooldownBeats=4},Instruments=new(){new(){Sound=CreateSoundEvent(),TrimStart=1,TrimEnd=2},new(){Sound=CreateSoundEvent(),TrimStart=3,TrimEnd=4}}});
  return e;
 }
 [TestMethod] public void Overlay_StartsOnNextBarWithoutRestartingBacking()
 {
  var e=OverlayEvent();var i=AudioSystem.GetEvent(e).CreateInstance();i.Start();
  _timeline.Advance(.2f);_=i.TimelinePosition;i.SetParameterByName("ServiceCount",1);
  _timeline.Advance(1.7f);_=i.TimelinePosition;Assert.AreEqual(1,_backend.Voices.Count);
  _timeline.Advance(.1f);_=i.TimelinePosition;Assert.AreEqual(2,_backend.Voices.Count);
  Assert.IsTrue(_backend.Voices[0].IsPlaying);Assert.IsTrue(_backend.Voices[1].Time>=1);
  _=i.TimelinePosition;Assert.AreEqual(2,_backend.Voices.Count);
  _timeline.Advance(1.1f);_=i.TimelinePosition;Assert.IsFalse(_backend.Voices[1].IsPlaying);
 }
 [TestMethod] public void Overlay_PauseMuteStopAndNoImmediateRepeat()
 {
  var e=OverlayEvent();var i=AudioSystem.GetEvent(e).CreateInstance();i.Start();
  _timeline.Advance(.1f);_=i.TimelinePosition;i.SetParameterByName("ServiceCount",1);
  _timeline.Advance(1.9f);_=i.TimelinePosition;
  var first=_backend.Voices[1];i.Paused=true;_timeline.Advance(5);_=i.PlaybackState;Assert.IsTrue(first.IsPlaying);Assert.IsTrue(first.Paused);
  i.Paused=false;i.Volume=0;Assert.AreEqual(0f,first.Volume);
  i.SetParameterByName("ServiceCount",2);_timeline.Advance(2);_=i.TimelinePosition;
  Assert.AreEqual(3,_backend.Voices.Count);Assert.AreNotEqual(first.Time,_backend.Voices[2].Time);
  i.Stop(AudioStopMode.Immediate);Assert.IsFalse(_backend.Voices[2].IsPlaying);
 }
 [TestMethod] public void Overlay_RejectsTempoMismatchAndIncompatibleBars()
 {
  var e=OverlayEvent();e.Tracks[0].Overlay.BeatsPerMinute=100;
  var i=AudioSystem.GetEvent(e).CreateInstance();i.Start();_timeline.Advance(.1f);_=i.TimelinePosition;i.SetParameterByName("ServiceCount",1);
  _timeline.Advance(1.9f);_=i.TimelinePosition;Assert.AreEqual(1,_backend.Voices.Count);
  i.Stop(AudioStopMode.Immediate);e.Tracks[0].Overlay.BeatsPerMinute=120;
  foreach(var clip in e.Tracks[0].Instruments)clip.OverlayBars=new(){3};
  i.Start();_timeline.Advance(.1f);_=i.TimelinePosition;i.SetParameterByName("ServiceCount",2);
  _timeline.Advance(1.9f);_=i.TimelinePosition;Assert.AreEqual(2,_backend.Voices.Count);
  _timeline.Advance(2);_=i.TimelinePosition;Assert.AreEqual(3,_backend.Voices.Count);
 }
 [TestMethod] public void Overlay_ValidationRejectsMissingTriggerAndBadTrim()
 {
  var e=OverlayEvent();e.Tracks[0].Overlay.TriggerParameter=null;
  Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
  e.Tracks[0].Overlay.TriggerParameter=e.Parameters.Last();e.Tracks[0].Instruments[0].TrimEnd=0;
  Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
 }
 [TestMethod] public void Overlay_ActivityReleaseKeepsPhraseAndWrongGridCannotStart()
 {
  var e=OverlayEvent();var intensity=new AudioParameter{Name="Busy",Minimum=0,Maximum=1};e.Parameters.Add(intensity);
  var rule=e.Tracks[0].Overlay;rule.BeatGridName="Grid0";rule.ActivityConditions=new(){Conditions=new(){new(){Parameter=intensity,Comparison=AudioParameterComparison.GreaterOrEqual,Value=.5f}}};
  var i=AudioSystem.GetEvent(e).CreateInstance();i.Start();i.SetParameterByName("Busy",1);
  _timeline.Advance(2);_=i.TimelinePosition;Assert.AreEqual(2,_backend.Voices.Count);
  i.SetParameterByName("Busy",0);Assert.IsTrue(_backend.Voices[1].IsPlaying);
  var live=AudioLiveDiagnostics.CaptureActive().Last(x=>x.Voices.Any(v=>v.Track=="Phrases" && v.State=="Silent"));
  Assert.IsTrue(live.Voices.Any(x=>x.Track=="Phrases" && x.SourcePosition>=1));
  _timeline.Advance(2);_=i.TimelinePosition;Assert.AreEqual(2,_backend.Voices.Count);
  i.Stop(AudioStopMode.Immediate);i.SetParameterByName("WorldState",1);i.SetParameterByName("Busy",1);i.Start();
  var before=_backend.Voices.Count;_timeline.Advance(2);_=i.TimelinePosition;Assert.AreEqual(before,_backend.Voices.Count);
 }
 [TestMethod] public void Overlay_DisabledInlineRulePreservesTimelinePlayback()
 {
  var e=CreateEvent();e.MasterTrack.Overlay=new();e.Tracks.Add(new(){Name="Ordinary",Overlay=new(),Instruments=new(){new(){Sound=CreateSoundEvent()}}});
  var i=AudioSystem.GetEvent(e).CreateInstance();i.Start();Assert.AreEqual(2,_backend.Voices.Count);
 }
 [TestMethod] public void Overlay_DrumGroupStartsOnBeatBesideSoloAndIgnoresRepeatedActions()
 {
  var e=OverlayEvent();var count=new AudioParameter{Name="Interactions",Minimum=0,Maximum=100};e.Parameters.Add(count);
  e.Tracks.Add(new(){Name="Drums",Overlay=new(){Enabled=true,TriggerParameter=count,VoiceGroup="drums",QuantizationBeats=1,BeatsPerMinute=120,CooldownBeats=4,QueueWhilePlaying=false,IgnoreTriggersDuringCooldown=true,ActivityConditions=new()},Instruments=new(){new(){Sound=CreateSoundEvent(),TrimStart=1,TrimEnd=2}}});
  var i=AudioSystem.GetEvent(e).CreateInstance();i.Start();i.SetParameterByName("ServiceCount",1);
  _timeline.Advance(2);_=i.TimelinePosition;Assert.AreEqual(2,_backend.Voices.Count);
  i.SetParameterByName("Interactions",1);_timeline.Advance(.5f);_=i.TimelinePosition;Assert.AreEqual(3,_backend.Voices.Count);
  Assert.IsTrue(_backend.Voices[1].IsPlaying);Assert.IsTrue(_backend.Voices[2].IsPlaying);
  i.SetParameterByName("Interactions",2);_timeline.Advance(.5f);_=i.TimelinePosition;Assert.AreEqual(3,_backend.Voices.Count);
  _timeline.Advance(.5f);_=i.TimelinePosition;i.SetParameterByName("Interactions",3);
  _timeline.Advance(1);_=i.TimelinePosition;Assert.AreEqual(3,_backend.Voices.Count,"No delayed fill from busy input or an empty inline activity rule.");
  i.SetParameterByName("Interactions",4);_timeline.Advance(.5f);_=i.TimelinePosition;Assert.AreEqual(4,_backend.Voices.Count);
  i.Stop(AudioStopMode.Immediate);Assert.IsTrue(_backend.Voices.All(x=>!x.IsPlaying));
 }
 [TestMethod] public void Overlay_RejectsInvalidGroupAndQuantization()
 {
  var e=OverlayEvent();e.Tracks[0].Overlay.VoiceGroup="";Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
  e.Tracks[0].Overlay.VoiceGroup="drums";e.Tracks[0].Overlay.QuantizationBeats=-1;Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
 }

 [TestMethod] public void Overlay_ShortWorkUsesNextBeatAndSharesInteractionBudget()
 {
  var e=OverlayEvent();var work=new AudioParameter{Name="ActiveWork",Minimum=0,Maximum=64};e.Parameters.Add(work);
  var actions=new AudioParameter{Name="Actions",Minimum=0,Maximum=100};e.Parameters.Add(actions);
  var activity=new ParameterConditionSet{Conditions=new(){new(){Parameter=work,Comparison=AudioParameterComparison.GreaterOrEqual,Value=1}}};
  e.Tracks.Add(new(){Name="Work rhythm",Overlay=new(){Enabled=true,VoiceGroup="drums",QuantizationBeats=1,BeatsPerMinute=120,CooldownBeats=4,QueueWhilePlaying=false,ActivityConditions=activity},Instruments=new(){new(){Sound=CreateSoundEvent(),TrimStart=1,TrimEnd=1.55f}}});
  e.Tracks.Add(new(){Name="Action rhythm",Overlay=new(){Enabled=true,VoiceGroup="drums",QuantizationBeats=1,BeatsPerMinute=120,CooldownBeats=4,QueueWhilePlaying=false,IgnoreTriggersDuringCooldown=true,TriggerParameter=actions},Instruments=new(){new(){Sound=CreateSoundEvent(),TrimStart=2,TrimEnd=2.55f}}});
  var instance=AudioSystem.GetEvent(e).CreateInstance();instance.Start();
  _timeline.Advance(.1f);instance.SetParameterByName("ActiveWork",1);
  _timeline.Advance(.4f);_=instance.PlaybackState;
  Assert.AreEqual(2,_backend.Voices.Count,"Work enters at the first beat, before a two-second process can finish.");
  instance.SetParameterByName("ActiveWork",4);instance.SetParameterByName("Actions",1);
  _timeline.Advance(.5f);_=instance.PlaybackState;Assert.AreEqual(2,_backend.Voices.Count,"Many machines and handling cannot stack the drum role.");
  instance.SetParameterByName("ActiveWork",0);
  _timeline.Advance(2);_=instance.PlaybackState;Assert.AreEqual(2,_backend.Voices.Count,"No late work response after work has ended.");
  Assert.IsFalse(_backend.Voices[1].IsPlaying);
  instance.SetParameterByName("ActiveWork",1);instance.SetParameterByName("Actions",2);
  _timeline.Advance(.5f);_=instance.PlaybackState;
  Assert.AreEqual(3,_backend.Voices.Count);Assert.AreEqual(2f,_backend.Voices[2].Time,.001f,"Pending interaction wins the shared role before sustained work.");
  instance.Stop(AudioStopMode.Immediate);
 }
}

