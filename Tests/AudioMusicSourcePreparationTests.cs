using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public async System.Threading.Tasks.Task MusicSourcePreparation_OwnedNeutralBackingAvoidsDuplicateArray()
 {
  short[] source={1,2,3,4,5,6};
  var result=await AudioMusicSourcePreparation.PrepareOwnedAsync(source,0,3,null,null);
  Assert.AreSame(source,result);
  Assert.AreNotSame(source,AudioMusicSourcePreparation.Prepare(source,0,3,null,null));
  var trimmed=await AudioMusicSourcePreparation.PrepareOwnedAsync(source,1,2,null,null);
  CollectionAssert.AreEqual(new short[]{3,4,5,6},trimmed);Assert.AreNotSame(source,trimmed);
  var filtered=await AudioMusicSourcePreparation.PrepareOwnedAsync(source,0,3,null,new AudioTone{BassGainDb=-6});
  Assert.AreNotSame(source,filtered);CollectionAssert.AreEqual(new short[]{1,2,3,4,5,6},source);
  using var cancel=new System.Threading.CancellationTokenSource();cancel.Cancel();
  await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>AudioMusicSourcePreparation.PrepareOwnedAsync(source,0,3,null,null,cancellation:cancel.Token));
 }
 [TestMethod] public void MusicSourcePreparation_RetainedBackingStaysWithinBudget()
 {
  Assert.IsFalse(AudioMusicSourcePreparation.FitsBudget(91445760,7621504,7620480,134217728));
  Assert.IsTrue(AudioMusicSourcePreparation.FitsBudget(91445760,7621504,7620480,134217728,true));
  Assert.IsFalse(AudioMusicSourcePreparation.FitsBudget(0,10,11,9000,true));
  Assert.IsFalse(AudioMusicSourcePreparation.FitsBudget(0,long.MaxValue,1,9000,true));
  Assert.IsFalse(AudioMusicSourcePreparation.CanRetainDecoded(0,10,11,null,null));
  Assert.IsFalse(AudioMusicSourcePreparation.CanRetainDecoded(0,10,10,null,new AudioTone{BassGainDb=-6}));
 }
 [TestMethod] public async System.Threading.Tasks.Task MusicSourcePreparation_YieldingPreservesSamples()
 {
  var source=Enumerable.Range(0,80000).Select(i=>(short)(Math.Sin(i*.071)*12000)).ToArray();
  var tone=new AudioTone{BassGainDb=-6};var expected=AudioMusicSourcePreparation.Prepare(source,2000,36000,null,tone);
  int yields=0;var actual=await AudioMusicSourcePreparation.PrepareAsync(source,2000,36000,null,tone,()=>{yields++;return System.Threading.Tasks.Task.CompletedTask;});
  Assert.IsTrue(yields>0);CollectionAssert.AreEqual(expected,actual);
 }
 [TestMethod] public async System.Threading.Tasks.Task MusicSourcePreparation_CancellationStopsToneAndNeutralCopy()
 {
  foreach(var tone in new AudioTone[]{null,new(){BassGainDb=-6}})
  {
   using var cancellation=new System.Threading.CancellationTokenSource();var source=new short[200000];
   await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>AudioMusicSourcePreparation.PrepareAsync(source,0,100000,null,tone,()=>{cancellation.Cancel();return System.Threading.Tasks.Task.CompletedTask;},cancellation.Token));
  }
 }

 [TestMethod] public void MusicSourcePreparation_UsesAuthoredOffsetsAndClampsToSourceRange()
 {
  var source=new SoundEventTrack{Offset=10,Length=4};var instrument=new AudioInstrument{TrimStart=1,TrimEnd=8};
  var range=AudioMusicSourcePreparation.ResolveRange(instrument,source);
  Assert.AreEqual(11f,range.Offset);Assert.AreEqual(3f,range.Length);
  instrument.TrimEnd=0;source.Length=0;range=AudioMusicSourcePreparation.ResolveRange(instrument,source);
  Assert.AreEqual(11f,range.Offset);Assert.AreEqual(0f,range.Length);
  source.Length=1;Assert.ThrowsExactly<ArgumentException>(()=>AudioMusicSourcePreparation.ResolveRange(instrument,source));
 }

 [TestMethod] public void MusicSourcePreparation_TrimIsExactAndSourceIsUnchanged()
 {
  short[] source={1,2,3,4,5,6,7,8};var saved=source.ToArray();
  var result=AudioMusicSourcePreparation.Prepare(source,1,2,null,null);
  CollectionAssert.AreEqual(new short[]{3,4,5,6},result);CollectionAssert.AreEqual(saved,source);
  result[0]=99;CollectionAssert.AreEqual(saved,source);
 }
 [TestMethod] public void MusicSourcePreparation_ToneWarmsFromSourceContextAndKeepsStereoSeparate()
 {
  var source=new short[20000];for(var i=0;i<10000;i++){source[i*2]=16000;source[i*2+1]=-8000;}
  var tone=new AudioTone{BassGainDb=-6,BassFrequencyHz=180};
  var whole=AudioMusicSourcePreparation.Prepare(source,0,10000,null,tone);
  var slice=AudioMusicSourcePreparation.Prepare(source,7000,512,null,tone);
  CollectionAssert.AreEqual(whole.Skip(14000).Take(1024).ToArray(),slice);
  Assert.IsTrue(slice[0]>7900 && slice[0]<8100);Assert.IsTrue(slice[1]<-3900 && slice[1]>-4100);
 }
 [TestMethod] public void MusicSourcePreparation_RejectsClippingAndBounds()
 {
  Assert.ThrowsExactly<InvalidOperationException>(()=>AudioMusicSourcePreparation.Prepare(Enumerable.Repeat((short)32000,2000).ToArray(),0,1000,null,new AudioTone{BassGainDb=6}));
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>AudioMusicSourcePreparation.Prepare(new short[]{1,2},1,1,null,null));
  Assert.IsTrue(AudioMusicSourcePreparation.FitsBudget(0,100,50,8792));
  Assert.IsFalse(AudioMusicSourcePreparation.FitsBudget(1,100,50,8792));
  Assert.IsFalse(AudioMusicSourcePreparation.FitsBudget(long.MaxValue,1,1,8192));
  Assert.IsFalse(AudioMusicSourcePreparation.FitsBudget(0,long.MaxValue,1,8192));
 }
}
