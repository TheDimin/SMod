using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void PlaybackPlanInspection_DoesNotAdvanceAudibleVariationHistory()
 {
  var sound=CreateSoundEvent(); sound.SelectionMode=SoundTrackSelectionMode.RandomExclusive;
  sound.Tracks.Add(CreateSoundEvent().Tracks[0]);
  var e=CreateEvent(sound);var first=AudioSystem.GetEvent(e).CreateInstance();first.Start();
  var audible=_backend.Voices[0].Sound.Sounds.Single();
  // One inspection used to select the other variation on the shared resource,
  // causing the next real play to repeat the last audible sound.
  AudioEventEvaluator.BuildPlaybackPlan(e,new Dictionary<AudioParameter,AudioParameterValue>());
  var second=AudioSystem.GetEvent(e).CreateInstance();second.Start();
  Assert.AreNotSame(audible,_backend.Voices[1].Sound.Sounds.Single());
  first.Stop(AudioStopMode.Immediate);second.Stop(AudioStopMode.Immediate);
 }
 [TestMethod] public void SelectedMusicVoice_PreservesGatesAutomationAndSource()
 {
  var e=CreateEvent(); var instrument=e.MasterTrack.Instruments[0];
  var track=new AudioTrack { Name="Layer", Volume=.5f, Instruments=new(){instrument} };
  e.MasterTrack.Instruments.Clear();e.Tracks.Add(track);e.MasterTrack.Volume=.8f;
  var work=new AudioParameter{Name="Work",Maximum=1};e.Parameters.Add(work);
  instrument.Automations.Add(new(){Parameter=work,Target=AudioAutomationTarget.Volume,
   Points=new(){new(){Input=0,Output=.25f},new(){Input=1,Output=1}}});
  AudioEventValidator.Validate(e).AssertValid();
  var values=new Dictionary<AudioParameter,AudioParameterValue>{{work,new(1,null,false)}};
  var selected=AudioEventEvaluator.BuildPlaybackPlan(e,values).Single();
  Assert.AreEqual(.4f,AudioEventEvaluator.EvaluateSelectedVoice(e,selected,values).Volume,.00001f);
  values[work]=new(0,null,false);
  Assert.AreEqual(.1f,AudioEventEvaluator.EvaluateSelectedVoice(e,selected,values).Volume,.00001f);
  foreach(var conditions in new[]{e.MasterTrack.Conditions,track.Conditions,instrument.Conditions,selected.SoundTrack.Conditions})
  {
   conditions.Add(new(){Parameter=work,Minimum=1,Maximum=1});
   Assert.IsFalse(AudioEventEvaluator.EvaluateSelectedVoice(e,selected,values).Active);
   conditions.Clear();
  }
  var source=SourceOf(e,instrument);
  source.Tracks.Remove(selected.SoundTrack);
  source.Tracks.Add(new(){Sound=selected.SoundTrack.Sound});
  Assert.IsFalse(AudioEventEvaluator.EvaluateSelectedVoice(e,selected,values).Active,"A retained voice must not silently select a replacement source.");
 }
 [TestMethod] public void SelectedMusicVoice_AllocatesLessThanRebuildingSongPlan()
 {
  var e=CreateEvent();
  var work=new AudioParameter{Name="Work",Maximum=1};e.Parameters.Add(work);
  var parameters=new Dictionary<AudioParameter,AudioParameterValue>{{work,new(.5f,null,false)}};
  e.MasterTrack.Conditions.Add(new(){Parameter=work,Minimum=0,Maximum=1});
  e.MasterTrack.Automations.Add(new(){Parameter=work,Target=AudioAutomationTarget.Volume,
   Points=new(){new(){Input=0,Output=.2f},new(){Input=1,Output=1}}});
  for(var i=0;i<15;i++)
  {
   var partSource=CreateSoundEvent();e.Sources.Add(partSource);
   e.Tracks.Add(new(){Name="Part "+i,Instruments=new(){new AudioInstrument{SourceId=partSource.Id}}});
  }
  AudioEventValidator.Validate(e).AssertValid();
  var selected=AudioEventEvaluator.BuildPlaybackPlan(e,parameters).Single(r=>r.Track==e.MasterTrack);
  for(var i=0;i<100;i++)
  {
   AudioEventEvaluator.BuildPlaybackPlan(e,parameters);
   AudioEventEvaluator.EvaluateSelectedVoice(e,selected,parameters);
  }
  const int count=2000;
  var baselineStart=GC.GetAllocatedBytesForCurrentThread();
  var watch=System.Diagnostics.Stopwatch.StartNew();
  float oldGain=0;
  for(var i=0;i<count;i++) oldGain+=AudioEventEvaluator.BuildPlaybackPlan(e,parameters).Single(r=>r.Track==e.MasterTrack).Volume;
  watch.Stop();var oldMs=watch.Elapsed.TotalMilliseconds;
  var oldBytes=GC.GetAllocatedBytesForCurrentThread()-baselineStart;
  var newStart=GC.GetAllocatedBytesForCurrentThread();watch.Restart();float newGain=0;
  for(var i=0;i<count;i++) newGain+=AudioEventEvaluator.EvaluateSelectedVoice(e,selected,parameters).Volume;
  watch.Stop();var newBytes=GC.GetAllocatedBytesForCurrentThread()-newStart;
  Assert.AreEqual(oldGain,newGain,.00001f);
  Assert.IsTrue(newBytes<oldBytes,"Retained voice updates should avoid full plan allocation.");
  Console.WriteLine($"SelectedVoice: iterations={count}, parts=16, oldBytes={oldBytes}, newBytes={newBytes}, oldMs={oldMs:F3}, newMs={watch.Elapsed.TotalMilliseconds:F3}. Controlled test host; not native frame-time evidence.");
 }
}
