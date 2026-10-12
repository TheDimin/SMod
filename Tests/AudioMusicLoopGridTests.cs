using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void MusicLoopTail_RequiresExplicitSharedPhraseBody()
 {
  var e=SharedOverlaySong();var clip=e.Tracks[0].Instruments[0];clip.AllowReleaseTailAcrossLoop=true;
  Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
  clip.OverlayBodySeconds=.3f;Assert.IsTrue(AudioEventValidator.Validate(e).IsValid);
  e.SharedMusicOutput=false;Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
 }
 [TestMethod] public void MusicSession_LoopTailRendersAfterCompositionBoundary()
 {
  var session=new AudioMusicPcmSession(new AudioBeatGrid{End=new(9.6),BeatsPerMinute=100},leadSamples:512,backingPcm:new short[846720],repeatComposition:true);
  var process=AudioSystem.GetEvent(CreateEvent()).CreateInstance();process.Start();
  try
  {
   var pcm=Enumerable.Repeat((short)8192,264600).ToArray();
   Assert.IsNull(session.TrySchedule(process.MusicRequestOwner,pcm,4,1,new[]{4}));
   var scheduled=session.TrySchedule(process.MusicRequestOwner,pcm,4,1,new[]{4},bodyFrames:105840);
   Assert.IsNotNull(scheduled);Assert.AreEqual(317520L,scheduled.Sample);
   var left=new float[512];var right=new float[512];var heardTail=false;
   while(session.Position<441000)
   {
    Array.Clear(left);Array.Clear(right);
    session.Processor.MixChannel(0,left);session.Processor.MixChannel(1,right);
    if(session.Position>424000 && left.Any(sample=>sample>.1f))heardTail=true;
   }
   Assert.IsTrue(scheduled.Admission.Result);Assert.IsTrue(heardTail,"The complete PCM tail must play beyond the backing loop.");
  }
  finally{session.CloseRequests();process.Stop(AudioStopMode.Immediate);process.Release();}
 }
 [TestMethod] public void MusicLoopGrid_OnlyExplicitReleaseTailCanCrossLoopEnd()
 {
  var grid=new AudioMusicLoopGrid(new AudioBeatGrid{End=new(9.6),BeatsPerMinute=100},423360);
  Assert.IsNull(grid.Next(1,4,132300,new[]{4}));
  Assert.AreEqual(317520L,grid.Next(1,4,132300,new[]{4},105840));
  Assert.IsNull(grid.Next(1,4,132300,new[]{4},105841));
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>grid.Next(1,4,100,new[]{1},101));
 }
 [TestMethod] public void MusicLoopGrid_DefersCompletePhraseAcrossCompositionEnd()
 {
  var grid=new AudioMusicLoopGrid(new AudioBeatGrid{Start=new(240),End=new(242.4),BeatsPerMinute=100},105840);
  Assert.AreEqual(79380L,grid.Next(70000,1,20000));
  Assert.AreEqual(105840L,grid.Next(70000,1,30000));
  Assert.AreEqual(132300L,grid.Next(105841,1,100));
  Assert.IsNull(grid.Next(0,1,105841));
 }
 [TestMethod] public void MusicLoopGrid_UsesSourceFramePeriodOverTwentyMinutes()
 {
  const long period=105840;
  var grid=new AudioMusicLoopGrid(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},period);
  for(long cycle=0;cycle<=500;cycle++)Assert.AreEqual(cycle*period+26460,grid.Next(cycle*period+1,1,100));
  Assert.IsNull(grid.Next(long.MaxValue,1,100));
 }
 [TestMethod] public void MusicLoopGrid_RejectsMismatchedOrPartialBarBacking()
 {
  Assert.ThrowsExactly<ArgumentException>(()=>new AudioMusicLoopGrid(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},105839));
  Assert.ThrowsExactly<ArgumentException>(()=>new AudioMusicLoopGrid(new AudioBeatGrid{End=new(1.2),BeatsPerMinute=100},52920));
 }
 [TestMethod] public void MusicSession_AcceptsNewPhraseAfterTheFirstBackingLoop()
 {
  var session=new AudioMusicPcmSession(new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},leadSamples:512,backingPcm:new short[211680],repeatComposition:true);
  for(var i=0;i<210;i++){session.Processor.MixChannel(0,new float[512]);session.Processor.MixChannel(1,new float[512]);}
  var process=AudioSystem.GetEvent(CreateEvent()).CreateInstance();process.Start();
  var request=session.TrySchedule(process,new short[]{1,1},1,1);
  Assert.IsNotNull(request);Assert.AreEqual(132300L,request.Sample);
  session.CloseRequests();Assert.IsFalse(request.Admission.Result);process.Stop(AudioStopMode.Immediate);
 }

 [TestMethod] public void MusicPhraseGrid_RestrictsWholeBarEntriesAndPreservesPhraseEnd()
 {
  var authored=new AudioBeatGrid{Start=new(240),End=new(259.2),BeatsPerMinute=100};
  var grid=new AudioMusicLoopGrid(authored,846720);
  Assert.AreEqual(211680L,grid.Next(1,1,211680,new[]{7,3}));
  Assert.AreEqual(635040L,grid.Next(211681,1,211680,new[]{3,7}));
  Assert.AreEqual(1058400L,grid.Next(635041,1,211680,new[]{3,7}));
  Assert.IsNull(grid.Next(1,1,211681,new[]{7}));
  Assert.IsNull(grid.Next(1,16,100,new[]{3,7}));
  Assert.IsNull(grid.Next(1,1,100,new[]{9}));
  Assert.AreEqual(10795680L,AudioMusicPhraseGrid.Next(authored,10584001,1,211680,new[]{3}));
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>grid.Next(1,1,100,new[]{0}));
 }
}
