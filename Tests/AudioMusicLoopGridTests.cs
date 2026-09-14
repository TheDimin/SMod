using Smod;
public sealed partial class AudioEventTests
{
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
