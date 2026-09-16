using Smod;
using Smod.Editor;
public sealed partial class AudioEventTests
{
 [TestMethod] public void ParentControlDescriptionDoesNotRequireItsOwnSource()
 {
  var child=new AudioEvent();var track=new AudioTrack{Name="Dreamy drums"};child.Tracks.Add(track);
  var node=new AudioInstrument{LayerControl=new(){ChildEvent=child,ChildTrackId=track.Id,TriggerParameter=new(){Name="InteractionCount"}}};
  var text=AudioTimelineInspectorUx.DescribeLayerControl(node);
  StringAssert.Contains(text,"Dreamy drums");StringAssert.Contains(text,"InteractionCount");
  StringAssert.Contains(text,"needs no source of its own");
  Assert.IsFalse(text.Contains("Missing Audio Source"));
  node.LayerControl.Mode=AudioMusicControlMode.TriggerPhraseBank;
  StringAssert.Contains(AudioTimelineInspectorUx.DescribeLayerControl(node),"Trigger complete phrases");
  Assert.IsNull(AudioTimelineInspectorUx.DescribeLayerControl(new AudioInstrument()));
 }

 [TestMethod] public void LiveAudition_SourceGainAndPitchChangeWithoutRestartingVoice()
 {
  var graph=CreateEvent();var source=graph.Sources.Single();
  var instance=AudioSystem.GetEvent(graph).CreateInstance();instance.Start();
  try
  {
   var voice=_backend.Voices.Single();voice.Time=.2f;
   source.Volume=new(.25f);source.Pitch=new(.8f);
   instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreEqual(.25f,voice.Volume,.00001f);
   Assert.AreEqual(.8f,voice.Pitch,.00001f);
   Assert.AreEqual(.2f,voice.Time,.00001f);
   Assert.AreSame(voice,_backend.Voices.Single());
   source.Volume=new(1f);source.Pitch=new(1f);
   instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreEqual(1f,voice.Volume,.00001f);
   Assert.AreEqual(1f,voice.Pitch,.00001f);
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void LiveAudition_MuteSoloClearAndDetachAffectOnlyTheSelectedInstance()
 {
  var e=CreateEvent();var a=e.MasterTrack;e.MasterTrack=new(){Name="Master"};a.Name="A";
  var bEvent=CreateEvent();var b=bEvent.MasterTrack;b.Name="B";e.Sources.AddRange(bEvent.Sources);e.Tracks.Add(a);e.Tracks.Add(b);
  var selected=AudioSystem.GetEvent(e).CreateInstance();var other=AudioSystem.GetEvent(e).CreateInstance();
  selected.Start();other.Start();var voices=_backend.Voices.ToArray();
  try
  {
   using(var controls=new AudioLiveAuditionSession(selected))
   {
    controls.Apply(id=>id==a.Id);Assert.AreEqual(0f,voices[0].Volume);Assert.IsTrue(voices[1].Volume>0);
    Assert.IsTrue(voices[2].Volume>0);Assert.IsTrue(voices[3].Volume>0);
    controls.Apply(id=>id==b.Id);Assert.IsTrue(voices[0].Volume>0);Assert.AreEqual(0f,voices[1].Volume);
    controls.Apply(_=>false);Assert.IsTrue(voices[0].Volume>0);Assert.IsTrue(voices[1].Volume>0);
    controls.Apply(_=>true);
   }
   Assert.IsTrue(voices[0].Volume>0);Assert.IsTrue(voices[1].Volume>0);
   Assert.AreEqual(4,_backend.Voices.Count,"Audition must retain the existing voices.");
  }
  finally {selected.Stop(AudioStopMode.Immediate);selected.Release();other.Stop(AudioStopMode.Immediate);other.Release();}
 }
 [TestMethod] public void LiveAudition_RestoresExistingMuteAndCanCloseAfterInstanceRelease()
 {
  var e=CreateEvent();var instance=AudioSystem.GetEvent(e).CreateInstance();instance.Start();
  instance.SetTrackMutedForAudition(e.MasterTrack,true);
  var controls=new AudioLiveAuditionSession(instance);controls.Apply(_=>false);controls.Dispose();
  Assert.IsTrue(instance.IsTrackMutedForAudition(e.MasterTrack));
  controls=new AudioLiveAuditionSession(instance);instance.Stop(AudioStopMode.Immediate);instance.Release();controls.Dispose();
 }
 [TestMethod] public void LiveAudition_SharedOutputReceivesMuteAndRestoresMixWithoutRestart()
 {
  var e=SharedSong();var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var instance=AudioSystem.GetEvent(e).CreateInstance();instance.SharedMusicSessionFactory=()=>session;instance.Start();
  try
  {
   using(var controls=new AudioLiveAuditionSession(instance))
   {
    controls.Apply(_=>true);Assert.AreEqual(0f,session.LastMix.Gain(e.MasterTrack));
    Assert.AreEqual(0,session.Disposals);
   }
   Assert.AreEqual(1f,session.LastMix.Gain(e.MasterTrack));Assert.AreEqual(0,session.SeekCalls);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
}
