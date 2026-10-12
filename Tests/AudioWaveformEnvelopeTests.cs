using Editor;
public sealed partial class AudioEventTests
{
 [TestMethod] public void LongMusicUsesBoundedDisplayMemory()
 {
  var samples=new short[6773760*2];samples[0]=-24000;samples[^1]=28000;
  var envelope=AudioWaveformEnvelope.Create(samples,2);
  Assert.AreEqual(6773760,envelope.FrameCount);
  Assert.IsTrue(envelope.StorageBytes<=2*1024*1024,"A music preview must not build one tree leaf per sample frame.");
  Assert.AreEqual(new AudioWaveformPeak(-24000,28000),envelope.GetPeak(0,envelope.FrameCount));
 }
 [TestMethod] public void BinRangesKeepStereoPeaksAndFinalPartialBin()
 {
  var samples=new short[130*2];
  for(var frame=0;frame<130;frame++){samples[frame*2]=(short)-frame;samples[frame*2+1]=(short)(frame+1);}
  var envelope=AudioWaveformEnvelope.Create(samples,2);
  Assert.AreEqual(new AudioWaveformPeak(-63,64),envelope.GetPeak(0,64));
  Assert.AreEqual(new AudioWaveformPeak(-127,128),envelope.GetPeak(64,128));
  Assert.AreEqual(new AudioWaveformPeak(-129,130),envelope.GetPeak(128,130));
  Assert.AreEqual(new AudioWaveformPeak(-127,128),envelope.GetPeak(70,80),"Partial bins conservatively include nearby peaks.");
  Assert.AreEqual(default(AudioWaveformPeak),envelope.GetPeak(130,200));
 }
 [TestMethod] public void EmptyWaveformRemainsSafe()
 {
  var envelope=AudioWaveformEnvelope.Create(Array.Empty<short>(),2);
  Assert.AreEqual(0,envelope.FrameCount);Assert.AreEqual(default(AudioWaveformPeak),envelope.GetPeak(-1,10));
 }
}
