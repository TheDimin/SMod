using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void EchoRepeatsImpulseAndPreservesDrySignalAcrossBlocks()
 {
  var settings=new AudioEchoEffect{DelaySeconds=.1f,WetGain=.25f,Feedback=.5f};
  var whole=new float[4000];whole[0]=1;var split=whole.ToArray();
  new AudioEchoDelay(settings,8000).Process(whole);
  var echo=new AudioEchoDelay(settings,8000);echo.Process(split.AsSpan(0,973));echo.Process(split.AsSpan(973));
  CollectionAssert.AreEqual(whole,split);Assert.AreEqual(1,whole[0]);Assert.AreEqual(.25f,whole[800]);Assert.AreEqual(.125f,whole[1600]);
 }
 [TestMethod] public void EchoTailEndsPausesAndKeepsChannelsSeparate()
 {
  var effect=new AudioEchoProcessor(new(){DelaySeconds=.02f,TailSeconds=.2f,WetGain=.5f,Feedback=.5f});
  var state=new AudioEchoProcessor.State();
  var left=new float[882];left[0]=1;effect.ProcessChannel(state,0,left);effect.ProcessChannel(state,1,new float[882]);
  effect.BeginTail();effect.Paused=true;
  for(int i=0;i<20;i++){effect.ProcessChannel(state,0,new float[882]);effect.ProcessChannel(state,1,new float[882]);}
  Assert.IsFalse(effect.Finished);effect.Paused=false;
  var repeat=new float[882];effect.ProcessChannel(state,0,repeat);var right=new float[882];effect.ProcessChannel(state,1,right);
  Assert.IsTrue(repeat[0]>0);Assert.IsTrue(right.All(x=>x==0));
  for(int i=0;i<12;i++){effect.ProcessChannel(state,0,new float[882]);effect.ProcessChannel(state,1,new float[882]);}
  Assert.IsTrue(effect.Finished);var ended=new float[882];effect.ProcessChannel(state,0,ended);Assert.IsTrue(ended.All(x=>x==0));
 }
 [TestMethod] public void EchoRejectsInvalidAndUnsupportedAuthoring()
 {
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>new AudioEchoDelay(new(){Feedback=1}));
  Assert.IsFalse(new AudioEchoEffect{DelaySeconds=float.NaN}.IsValid);
  var graph=new AudioEvent();graph.MasterTrack.Instruments.Add(new(){Echo=new()});
  Assert.IsTrue(AudioEventValidator.Validate(graph).Issues.Any(x=>x.Path.EndsWith("/Echo")));
  graph.UI=true;Assert.IsFalse(AudioEventValidator.Validate(graph).Issues.Any(x=>x.Path.EndsWith("/Echo")));
  graph.SharedMusicOutput=true;Assert.IsTrue(AudioEventValidator.Validate(graph).Issues.Any(x=>x.Path.EndsWith("/Echo")));
 }

 [TestMethod] public void EchoMuteDrainsSilentlyAndImmediateCancelClearsOutput()
 {
  var effect=new AudioEchoProcessor(new(){DelaySeconds=.02f,TailSeconds=.1f});var state=new AudioEchoProcessor.State();
  var signal=new float[882];signal[0]=1;effect.ProcessChannel(state,0,signal);effect.ProcessChannel(state,1,new float[882]);
  effect.BeginTail();effect.Suppressed=true;
  for(int n=0;n<6;n++){var block=new float[882];effect.ProcessChannel(state,0,block);Assert.IsTrue(block.All(x=>x==0));effect.ProcessChannel(state,1,new float[882]);}
  Assert.IsTrue(effect.Finished);effect.Killed=true;var dry=Enumerable.Repeat(1f,882).ToArray();effect.ProcessChannel(state,0,dry);Assert.IsTrue(dry.All(x=>x==0));
 }
}
