using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void MusicPrewarm_PreservesCompletePhraseTailsAndAllSourceVariations()
 {
  var graph=CreateEvent();var source=graph.Sources.Single();
  source.Tracks[0].Offset=2;source.Tracks[0].Length=20;
  source.Tracks.Add(new SoundEventTrack{Sound=source.Tracks[0].Sound,Offset=10,Length=20});
  var clip=graph.MasterTrack.Instruments[0];clip.TrimStart=1;clip.TrimEnd=7.3f;clip.OverlayBodySeconds=4.8f;
  graph.MasterTrack.Overlay=new(){Enabled=true};graph.MasterTrack.Tone=new(){BassGainDb=-6};
  AudioSourceRuntime.Prepare(graph,new Dictionary<string,AudioParameter>());
  var plan=AudioMusicPrewarmPlan.Build(graph);
  Assert.AreEqual(2,plan.Length);
  Assert.AreEqual(3f,plan[0].Offset);Assert.AreEqual(11f,plan[1].Offset);
  Assert.AreEqual(6.3f,plan[0].Length,.00001f,"Prewarm must keep the complete recorded tail.");
  Assert.IsNull(plan[0].Tone,"The master tone is applied once by the bank, not again as a track tone.");
 }
 [TestMethod] public void MusicPrewarm_CollectsCompoundHitsAndSnapshotsTone()
 {
  var graph=CreateEvent();var source=graph.Sources.Single();source.Tracks[0].Offset=2;
  var track=graph.MasterTrack;graph.MasterTrack=new(){Name="Master"};graph.Tracks.Add(track);
  track.Overlay=new(){Enabled=true};track.Tone=new(){BassGainDb=-3};
  track.Instruments[0].CompoundHits=new(){new(){SourceOffset=4,Length=.2f,BeatOffset=0},new(){SourceOffset=7,Length=.3f,BeatOffset=1}};
  AudioSourceRuntime.Prepare(graph,new Dictionary<string,AudioParameter>());
  var plan=AudioMusicPrewarmPlan.Build(graph);track.Tone.BassGainDb=-9;
  Assert.AreEqual(2,plan.Length);Assert.AreEqual(6f,plan[0].Offset);Assert.AreEqual(9f,plan[1].Offset);
  Assert.AreEqual(.3f,plan[1].Length);Assert.AreEqual(-3f,plan[0].Tone.BassGainDb);
 }
 [TestMethod] public void MusicPrewarm_BorrowedBackingBudgetCountsOnlyAdditionalRanges()
 {
  Assert.IsTrue(AudioMusicSourcePreparation.FitsBudget(400,0,50,8792));
  Assert.IsFalse(AudioMusicSourcePreparation.FitsBudget(401,0,50,8792));
  Assert.IsFalse(AudioMusicSourcePreparation.FitsBudget(0,-1,50,8792));
 }
}
