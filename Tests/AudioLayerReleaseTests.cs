using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void LayerRelease_EasesBothEndsAndReachesExactIdle()
 {
  var values=Enumerable.Range(0,25).Select(i=>AudioLayerPlayback.ReleaseGain(1,.15f,i/24d)).ToArray();
  Assert.AreEqual(1f,values[0]);Assert.AreEqual(.15f,values[^1]);
  for(int i=1;i<values.Length;i++)Assert.IsTrue(values[i]<=values[i-1]);
  double Db(int i)=>20*Math.Log10(values[i]);
  Assert.IsTrue(Math.Abs(Db(1)-Db(0))<.1);
  Assert.IsTrue(Math.Abs(Db(24)-Db(23))<.1);
  Assert.IsTrue(Math.Abs(Db(12)-Db(11))<1.1);
  Assert.AreEqual(0f,AudioLayerPlayback.ReleaseGain(1,0,1));
  Assert.AreEqual(0f,AudioLayerPlayback.ReleaseGain(0,0,.5));
 }
 [TestMethod] public void LayerRelease_FollowsPhraseEndAcrossSparseUpdatesAndRetriggersContinuously()
 {
  var plan=new AudioLayerPlan(1,1,1,.15f,1,.1f,2.4f,AudioLayerReleaseCurve.SmoothDecibels);
  var a=new AudioLayerPlayback(plan,0,0);var b=new AudioLayerPlayback(plan,0,0);
  a.Update(.1,1,true);b.Update(.1,1,true);a.Update(1,1,true);b.Update(1,1,true);
  for(int n=0;n<=12;n++)a.Update(2+n*.1,1,true);
  b.Update(3.2,1,true);Assert.AreEqual(a.Gain,b.Gain,.00001f);
  var before=b.Gain;b.Update(3.2,1,true);Assert.AreEqual(before,b.Gain);
  b.Update(3.2,2,true);Assert.IsTrue(b.Active);Assert.AreEqual(before,b.Gain);
  b.Update(3.3,2,true);Assert.IsTrue(b.Gain>before);
  a.Update(4.4,1,true);Assert.AreEqual(.15f,a.Gain);
 }
}
