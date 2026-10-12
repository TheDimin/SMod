using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void SharedMusic_QuarterBeatEntryHasABoundedPreparedWait()
 {
  var grid=new AudioBeatGrid{End=new(24),BeatsPerMinute=100};
  for(long action=0;action<44100;action+=73)
  {
   var next=AudioMusicPhraseGrid.Next(grid,action+AudioMusicPcmSession.DefaultLeadSamples,.25,100);
   Assert.IsTrue(next.HasValue);
   Assert.IsTrue(next.Value-action<=9262,"Prepared admission must be within 60 ms plus a quarter beat.");
   Assert.AreEqual(0,next.Value%6615);
  }
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>AudioMusicPhraseGrid.Next(grid,0,double.NaN,100));
 }
 [TestMethod] public void MusicGain_BoostsBackingAndQueuedPhraseWithoutClamping()
 {
  var backing=new AudioMusicPcmBacking(new short[]{4096,-4096},2);
  var renderer=new AudioMusicPcmRenderer(1,backing:backing);
  var commands=new AudioMusicPcmCommands();
  var owner=new AudioMusicRequestOwner {Gain=2};
  var request=commands.TryQueue(owner,new short[]{2048,2048},0,2);
  commands.Drain(renderer);
  var output=new float[2];renderer.Render(output);
  Assert.IsTrue(request.Admission.Result);
  Assert.AreEqual(.5f,output[0]);Assert.AreEqual(0f,output[1]);
  backing.SetGains(new float[]{3});renderer.Render(new float[2000]);
  output=new float[2];renderer.Render(output);
  Assert.AreEqual(.375f,output[0]);Assert.AreEqual(-.375f,output[1]);
  foreach(var invalid in new[]{-1f,float.NaN,float.PositiveInfinity,float.NegativeInfinity})
  {
   Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>backing.SetGains(new[]{invalid}));
   Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>owner.Gain=invalid);
   Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>commands.TryQueue(owner,new short[2],0,invalid));
  }
 }
 [TestMethod] public void MusicBacking_StemMixKeepsPhaseAcrossMutePauseAndLoopWrap()
 {
  var backing=new AudioMusicPcmBacking(new AudioMusicPcmBacking.Stem[]{
   new(new short[]{8192,-8192,8192,-8192,8192,-8192},1),
   new(new short[]{4096,4096,8192,8192,12288,12288},0)});
  var renderer=new AudioMusicPcmRenderer(1,backing:backing);
  renderer.Render(new float[10]); // Five frames: deliberately not a loop boundary.
  var gains=new float[]{1,1};backing.SetGains(gains);gains[1]=0;
  renderer.Paused=true;var paused=new float[20];renderer.Render(paused);
  Assert.AreEqual(5L,renderer.Position);Assert.IsTrue(paused.All(x=>x==0));
  renderer.Paused=false;var output=new float[1800];renderer.Render(output);
  Assert.AreEqual(905L,renderer.Position);
  Assert.IsTrue(output[0]>.25f && output[0]<.251f,"Gain must ramp from silence.");
  // Frame 904 is source frame 1. A restarted part would use frame 2 instead.
  Assert.AreEqual(.5f,output[1798],.00001f);
  Assert.AreEqual(0f,output[1799],.00001f);
  backing.SetGains(new float[]{1,0});renderer.Render(new float[1800]);
  var quiet=new float[2];renderer.Render(quiet);
  Assert.AreEqual(.25f,quiet[0]);Assert.AreEqual(-.25f,quiet[1]);
 }
 [TestMethod] public void MusicBacking_RejectsMisalignedStemsAndInvalidMixWithoutChangingTargets()
 {
  Assert.ThrowsExactly<ArgumentException>(()=>new AudioMusicPcmBacking(new AudioMusicPcmBacking.Stem[]{
   new(new short[2],1),new(new short[4],1)}));
  var backing=new AudioMusicPcmBacking(new AudioMusicPcmBacking.Stem[]{new(new short[]{8192,8192},1),new(new short[]{8192,8192},0)});
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>backing.SetGains(new float[]{0,float.NaN}));
  Assert.ThrowsExactly<ArgumentException>(()=>backing.SetGains(new float[]{0}));
  var output=new float[2000];backing.Render(output,0);
  Assert.IsTrue(output.All(x=>x==.25f));
 }
 [TestMethod] public void MusicSession_MixesContinuousStemsAndRejectsChangesAfterClose()
 {
  var session=new AudioMusicPcmSession(new AudioBeatGrid{End=new(8),BeatsPerMinute=100},
   backingStems:new AudioMusicPcmBacking.Stem[]{new(new short[]{8192,-8192},1),new(new short[]{8192,8192},1)});
  var left=new float[1];var right=new float[1];
  session.Processor.MixChannel(0,left);session.SetBackingGains(new float[]{0,0});
  session.Processor.MixChannel(1,right);
  Assert.AreEqual(.5f,left[0]);Assert.AreEqual(0f,right[0],"A gain change must not split a stereo block.");
  session.CloseRequests();
  Assert.ThrowsExactly<InvalidOperationException>(()=>session.SetBackingGains(new float[]{1,1}));
 }
 [TestMethod] public void MusicBacking_LoopsAndPausesWithTheCompletePhrase()
 {
  var backing = new AudioMusicPcmBacking(new short[]{8192,-8192,16384,-16384}, 1);
  var renderer = new AudioMusicPcmRenderer(1, backing:backing);
  var commands = new AudioMusicPcmCommands();
  var processor = new AudioMusicPcmProcessor(renderer, 8, commands);
  var request = commands.TryQueue(new(), new short[]{4096,4096,8192,8192,12288,12288}, 1, 1);
  var left = new float[2];var right = new float[2];
  processor.MixChannel(0,left);
  commands.SetPaused(true); // Must not split an in-flight stereo block.
  processor.MixChannel(1,right);
  CollectionAssert.AreEqual(new float[]{.25f,.625f},left);
  CollectionAssert.AreEqual(new float[]{-.25f,-.375f},right);
  Assert.AreEqual(2L,processor.PublishedPosition);Assert.IsTrue(request.Admission.Result);
  left=new float[2];right=new float[2];processor.MixChannel(0,left);processor.MixChannel(1,right);
  Assert.IsTrue(left.All(x=>x==0) && right.All(x=>x==0));Assert.AreEqual(2L,processor.PublishedPosition);Assert.AreEqual(2L,processor.PublishedPausedFrames);
  commands.SetPaused(false);processor.MixChannel(0,left);processor.MixChannel(1,right);
  CollectionAssert.AreEqual(new float[]{.5f,.875f},left);
  CollectionAssert.AreEqual(new float[]{0f,-.125f},right);
  Assert.AreEqual(4L,processor.PublishedPosition);Assert.AreEqual(0,renderer.ActiveCount);
 }
 [TestMethod] public void MusicSession_CloseWhilePausedReleasesItsBackingAndRejectsResume()
 {
  var session=new AudioMusicPcmSession(new AudioBeatGrid{End=new(8),BeatsPerMinute=100},backingPcm:new short[]{16384,16384});
  session.SetPaused(true);var left=new float[512];var right=new float[512];
  session.Processor.MixChannel(0,left);session.Processor.MixChannel(1,right);Assert.AreEqual(0L,session.Position);
  session.CloseRequests();session.Processor.MixChannel(0,left);session.Processor.MixChannel(1,right);
  Assert.AreEqual(.5f,left[0]);Assert.AreEqual(.5f,right[0]);Assert.AreEqual(0f,left[128]);
  Assert.IsTrue(left.Skip(128).All(x=>x==0));
  Assert.ThrowsExactly<InvalidOperationException>(()=>session.SetPaused(false));
 }
 [TestMethod] public void MusicBacking_RejectsInvalidSourceAndGain()
 {
  Assert.ThrowsExactly<ArgumentException>(()=>new AudioMusicPcmBacking(new short[]{1},1));
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>new AudioMusicPcmBacking(new short[]{1,1},float.NaN));
 }
}
