using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void TimelineBacking_ReusesClipsAtExactStartsAcrossBlockAndSongBoundaries()
 {
  var pcm=new short[]{8192,-8192,16384,-16384};
  var backing=new AudioMusicPcmBacking(new AudioMusicPcmBacking.Stem[]{new(pcm,1,1),new(pcm,.5f,4)},6);
  var core=new AudioMusicPcmRenderer(1,backing:backing);
  var first=new float[6];core.Render(first);var second=new float[10];core.Render(second);
  CollectionAssert.AreEqual(new float[]{0,0,.25f,-.25f,.5f,-.5f},first);
  CollectionAssert.AreEqual(new float[]{0,0,.125f,-.125f,.25f,-.25f,0,0,.25f,-.25f},second);
  Assert.AreEqual(8L,core.Position);Assert.AreEqual(6,backing.FrameCount);
 }
 [TestMethod] public void TimelineBacking_FadesUseClipLocalSamplesAndSnapshotAuthoring()
 {
  var envelope=new AudioInstrument{FadeInDuration=4/44100f};
  var backing=new AudioMusicPcmBacking(new AudioMusicPcmBacking.Stem[]{new(Enumerable.Repeat((short)16384,8).ToArray(),1,2,envelope)},8);
  envelope.FadeInDuration=0;var output=new float[16];backing.Render(output,0);
  var expected=new float[]{0,0,0,0,0,0,.125f,.125f,.25f,.25f,.375f,.375f,0,0,0,0};
  for(var i=0;i<expected.Length;i++)Assert.AreEqual(expected[i],output[i],.0000001f);
 }
 [TestMethod] public void TimelineBacking_RejectsOutOfSongPlacementAndBoundsSlots()
 {
  Assert.ThrowsExactly<ArgumentException>(()=>new AudioMusicPcmBacking(new AudioMusicPcmBacking.Stem[]{new(new short[4],1,3)},4));
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>new AudioMusicPcmBacking(Enumerable.Repeat(new AudioMusicPcmBacking.Stem(new short[2],1),129).ToArray(),4));
 }
 [TestMethod] public void TimelineBinding_IncludesShortDrumsAndLeavesFadeForTheSampleRenderer()
 {
  var e=CreateEvent();e.MasterTrack.Instruments[0].TrimEnd=2.4f;
  var drum=new AudioInstrument{Sound=CreateSoundEvent(),StartTime=.6f,TrimEnd=.6f,FadeInDuration=.1f};
  e.Tracks.Add(new(){Name="Drums",Instruments=new(){drum}});
  var binding=new AudioMusicEventBacking(e,new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},new Dictionary<AudioParameter,AudioParameterValue>(),includeTimeline:true);
  Assert.AreEqual(2,binding.Parts.Count);Assert.AreSame(drum,binding.Parts[1].Instrument);
  Assert.AreEqual(1f,binding.EvaluateGains(new Dictionary<AudioParameter,AudioParameterValue>(),new AudioMusicMixState(0))[1],"The sample renderer applies the future clip fade; frame updates must not suppress its first samples.");
 }
}
