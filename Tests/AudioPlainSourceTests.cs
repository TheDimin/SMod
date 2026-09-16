using System.Text.Json;
using Smod;

public sealed partial class AudioEventTests
{
 [TestMethod] public void PlainSource_EventInstancesPlayAndAvoidRepeats()
 {
  var graph=CreateEvent();var source=graph.Sources.Single();
  source.SelectionMode=SoundTrackSelectionMode.RandomExclusive;
  source.Tracks.Add(CreateSoundEvent().Tracks[0]);
  var first=AudioSystem.GetEvent(graph).CreateInstance();var second=AudioSystem.GetEvent(graph).CreateInstance();
  try
  {
   first.Start();second.Start();Assert.AreEqual(2,_backend.Voices.Count);
   Assert.AreNotSame(_backend.Voices[0].Sound.Sounds[0],_backend.Voices[1].Sound.Sounds[0]);
  }
  finally{first.Stop(AudioStopMode.Immediate);second.Stop(AudioStopMode.Immediate);first.Release();second.Release();}
 }

 [TestMethod] public void PlainSource_SharedEventKeepsClockGainAndCleanup()
 {
  var graph=SharedSong();var source=graph.Sources.Single();source.Volume=new(.6f);
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var instance=AudioSystem.GetEvent(graph).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();session.Position=22050;Assert.AreEqual(.5f,instance.TimelinePosition);
   var backing=new AudioMusicEventBacking(graph,graph.BeatGrids[0],new Dictionary<AudioParameter,AudioParameterValue>(),true);
   Assert.AreEqual(.6f,backing.EvaluateGains(new Dictionary<AudioParameter,AudioParameterValue>())[0],.00001f);
   instance.Paused=true;Assert.IsTrue(session.Paused);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
  Assert.AreEqual(1,session.Disposals);
 }

 [TestMethod] public void PlainSource_GraphRejectsMissingSourceAndParentFallback()
 {
  var graph=CreateEvent();var source=graph.Sources.Single();var clip=graph.MasterTrack.Instruments[0];
  Assert.IsTrue(AudioEventValidator.Validate(graph).IsValid);

  clip.SourceId=Guid.NewGuid();
  Assert.IsFalse(AudioEventValidator.Validate(graph).IsValid);
  clip.SourceId=source.Id;

  graph.Sources.Clear();
  Assert.IsFalse(AudioEventValidator.Validate(graph).IsValid);

  var parent=CreateEvent();
  var child=new AudioEvent
  {
   MasterTrack=new AudioTrack
   {
    Name="ChildMaster",
    Instruments=new(){new AudioInstrument{SourceId=parent.Sources.Single().Id}}
   }
  };
  parent.MasterTrack.Instruments.Clear();
  parent.MasterTrack.Instruments.Add(new AudioInstrument{SubEvents=new(){new(){Event=child}}});
  Assert.IsFalse(AudioEventValidator.Validate(parent).IsValid,
   "A child source ID must resolve in the child graph, not in its parent.");
 }

 [TestMethod] public void PlainSource_SourceIdsCannotCollideWithGraphElements()
 {
  var graph=CreateEvent();var source=graph.Sources.Single();
  var ids=new[]{graph.MasterTrack.Id,graph.MasterTrack.Instruments[0].Id};
  foreach(var id in ids)
  {
   source.Id=id;graph.MasterTrack.Instruments[0].SourceId=id;
   var result=AudioEventValidator.Validate(graph);
   Assert.IsFalse(result.IsValid);
   Assert.IsTrue(result.Issues.Any(issue=>issue.Message.Contains("collides")));
   source.Id=Guid.NewGuid();graph.MasterTrack.Instruments[0].SourceId=source.Id;
  }
  Assert.IsTrue(AudioEventValidator.Validate(graph).IsValid);
 }

 [TestMethod] public void PlainSource_RejectsStaleBindingUntilGraphIsRevalidated()
 {
  var graph=CreateEvent();var sourceA=graph.Sources.Single();var sourceB=CreateSoundEvent();
  graph.Sources.Add(sourceB);
  var clip=graph.MasterTrack.Instruments[0];
  Assert.IsTrue(AudioEventValidator.Validate(graph).IsValid);

  clip.SourceId=sourceB.Id;
  Assert.ThrowsExactly<InvalidOperationException>(()=>graph.Source(clip));

  Assert.IsTrue(AudioEventValidator.Validate(graph).IsValid);
  Assert.AreSame(sourceB,graph.Source(clip));
 }

 [TestMethod] public void PlainSource_UsesNativeSettingsAndSharedGain()
 {
  var source=CreateSoundEvent();source.Volume=new(.7f);source.Pitch=new(1f);
  source.Tracks[0].Offset=.25f;source.Tracks[0].Length=.5f;
  var copy=new AudioSourceDefinition{Volume=source.Volume,Pitch=source.Pitch,
   SelectionMode=source.SelectionMode,Tracks=source.Tracks};
  var graph=CreateEvent(source);
  var sourceVoice=graph.CreatePlaybackEvent(source,source.Tracks[0]);
  var copyVoice=graph.CreatePlaybackEvent(copy,copy.Tracks[0]);
  Assert.AreEqual(sourceVoice.Volume,copyVoice.Volume);
  Assert.AreEqual(sourceVoice.Pitch,copyVoice.Pitch);
  Assert.AreEqual(sourceVoice.Decibels,copyVoice.Decibels);
  Assert.AreSame(sourceVoice.Sounds[0],copyVoice.Sounds[0]);
  Assert.AreEqual(AudioMusicEventBacking.Gain(source,.8f,1f),AudioMusicEventBacking.Gain(copy,.8f,1f));
  var instrument=new AudioInstrument{TrimStart=.1f,TrimEnd=.3f};
  Assert.AreEqual(AudioMusicSourcePreparation.ResolveRange(instrument,source.Tracks[0]),
   AudioMusicSourcePreparation.ResolveRange(instrument,copy.Tracks[0]));
  copy.Pitch=new(1.1f);
  Assert.ThrowsExactly<InvalidOperationException>(()=>AudioMusicEventBacking.Gain(copy,1,1));
 }

 [TestMethod] public void PlainSource_JsonRoundtripPreservesIdSettingsAndSourceLinks()
 {
  var source=new AudioSourceDefinition
  {
   Volume=new(.75f),Pitch=new(1.1f),SelectionMode=SoundTrackSelectionMode.RandomExclusive,Tracks=new()
  };
  var json=JsonSerializer.Serialize(source);
  var restored=JsonSerializer.Deserialize<AudioSourceDefinition>(json);
  Assert.IsNotNull(restored);
  Assert.AreEqual(source.Id,restored.Id);
  Assert.AreEqual(source.SelectionMode,restored.SelectionMode);
  Assert.AreEqual(source.Volume.Min,restored.Volume.Min,.00001f);
  Assert.AreEqual(source.Pitch.Min,restored.Pitch.Min,.00001f);

  var graph=CreateEvent();
  var linked=CreateSoundEvent();graph.Sources.Add(linked);
  graph.MasterTrack.Instruments.Add(new AudioInstrument{SourceId=linked.Id});
  var linksJson=JsonSerializer.Serialize(graph.MasterTrack.Instruments.Select(instrument=>instrument.SourceId).ToArray());
  var restoredLinks=JsonSerializer.Deserialize<Guid[]>(linksJson);
  Assert.IsNotNull(restoredLinks);
  Assert.AreEqual(linked.Id,graph.MasterTrack.Instruments[1].SourceId);
  Assert.AreEqual(linked.Id,restoredLinks[1]);
 }

 [TestMethod] public void PlainSource_UsesTheSameValidationForInvalidRangesAndMissingInputs()
 {
  var plain=new AudioSourceDefinition{Volume=new(-1f),Tracks=new(){new(){Offset=-1f}}};
  var result=new AudioEventValidationResult();
  AudioEventValidator.ValidateSoundEvent(plain,"Plain",new Dictionary<string,AudioParameter>(),result);
  Assert.IsFalse(result.IsValid);
  plain.Volume=new(1f);plain.Tracks=CreateSoundEvent().Tracks;
  result=new();AudioEventValidator.ValidateSoundEvent(plain,"Plain",new Dictionary<string,AudioParameter>(),result);
  Assert.IsTrue(result.IsValid);
  plain.Tracks[0].Conditions.Add(new(){Parameter=new AudioParameter{Name="Undeclared"}});
  result=new();AudioEventValidator.ValidateSoundEvent(plain,"Plain",new Dictionary<string,AudioParameter>(),result);
  Assert.IsFalse(result.IsValid);
 }
}
