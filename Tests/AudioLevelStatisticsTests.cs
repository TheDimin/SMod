using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod]
 public void MeterTap_BoundsQueueAndKeepsListenersSeparate()
 {
  var tap=new Editor.Mcp.AudioSessionMeterTap(null,1d/44100);
  var a=new Editor.Mcp.AudioSessionMeterTap.State();var b=new Editor.Mcp.AudioSessionMeterTap.State();
  var input=new[]{.25f};
  foreach(var state in new[]{a,b}){tap.Observe(state,0,input,1);tap.Observe(state,1,input,1);}
  var listeners=new HashSet<int>();while(tap.TryRead(out var row))listeners.Add(row.Listener);
  Assert.AreEqual(2,listeners.Count);
  var burst=Enumerable.Repeat(.5f,512).ToArray();tap.Observe(a,0,burst,1);tap.Observe(a,1,burst,1);
  var count=0;while(tap.TryRead(out _))count++;
  Assert.AreEqual(256,count);Assert.IsTrue(tap.Dropped>0);Assert.IsTrue(burst.All(v=>v==.5f));
 }
 [TestMethod]
 public void MeterTap_DoesNotChangeSamplesAndKeepsAllChannelsSeparate()
 {
  var tap=new Editor.Mcp.AudioSessionMeterTap(null,.25);var state=new Editor.Mcp.AudioSessionMeterTap.State();
  var left=Enumerable.Repeat(.5f,441).ToArray();var right=Enumerable.Repeat(-.5f,441).ToArray();
  for(var i=0;i<25;i++)for(var channel=0;channel<8;channel++)tap.Observe(state,channel,channel==1?right:left,.5f);
  Assert.IsTrue(left.All(v=>v==.5f));Assert.IsTrue(right.All(v=>v==-.5f));
  var windows=new List<Editor.Mcp.AudioSessionMeterTap.Window>();while(tap.TryRead(out var row))windows.Add(row);
  Assert.AreEqual(9,windows.Count);Assert.AreEqual(9,windows.Select(w=>w.Channel).Distinct().Count());
  Assert.IsTrue(windows.All(w=>Math.Abs(w.Levels.PeakDbfs+12.0412)<.001));
  Assert.AreEqual(-1d,windows.Single(w=>w.Channel==-1).Levels.StereoCorrelation.Value,.0001);
  Assert.AreEqual(0,tap.Dropped);Assert.AreEqual(0,tap.UnsupportedChannels);Assert.AreEqual(0,tap.PairingErrors);
 }
 [TestMethod]
 public void Meter_ReportsPeakRmsAndSilenceWithoutNonFiniteJson()
 {
  var meter=new AudioLevelStatistics();for(var i=0;i<1000;i++)meter.Add(.5f,.5f);
  var r=meter.Read();Assert.AreEqual(-6.0206,r.PeakDbfs,.0001);Assert.AreEqual(r.PeakDbfs,r.RmsDbfs,.0001);
  Assert.AreEqual(0d,r.MonoLossDb.Value,.0001);Assert.AreEqual(1d,r.StereoCorrelation.Value,.0001);
  var silent=new AudioLevelStatistics();silent.Add(0,0);Assert.AreEqual(-160d,silent.Read().RmsDbfs);
 }
 [TestMethod]
 public void Meter_FindsSpeakerCancellationAndClipping()
 {
  var meter=new AudioLevelStatistics();for(var i=0;i<100;i++)meter.Add(.5f,-.5f);
  var r=meter.Read();Assert.AreEqual(-1d,r.StereoCorrelation.Value,.0001);Assert.IsTrue(r.MonoLossDb < -100);
  meter.Add(1.2f,0);meter.Add(float.NaN,0);r=meter.Read();Assert.AreEqual(1L,r.ClippedFrames);Assert.AreEqual(1L,r.InvalidFrames);
  Assert.IsTrue(double.IsFinite(r.RmsDbfs));
 }
 [TestMethod]
 public void Meter_SineRmsAndGainScalingUseAmplitudeDecibels()
 {
  var meter=new AudioLevelStatistics();var quieter=new AudioLevelStatistics();
  for(var i=0;i<44100;i++){var sample=(float)Math.Sin(2*Math.PI*1000*i/44100);meter.Add(sample,sample);quieter.Add(sample*.25f,sample*.25f);}
  Assert.AreEqual(-3.0103,meter.Read().RmsDbfs,.001);
  Assert.AreEqual(-12.0412,quieter.Read().RmsDbfs-meter.Read().RmsDbfs,.001);
 }
}
