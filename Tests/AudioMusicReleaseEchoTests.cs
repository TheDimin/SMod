using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void ReleaseEchoUsesOldNoteAndRejectsNewNote()
 {
  var echo=new AudioMusicReleaseEcho(new(){DelaySeconds=.02f,WetGain=.5f,Feedback=.5f,TailSeconds=.1f});
  for(int n=0;n<1000;n++){float l=.4f,r=.2f;echo.Process(ref l,ref r,true,true);Assert.AreEqual(.4f,l);}
  float peak=0;
  for(int n=0;n<5000;n++){float l=-1,r=-1;echo.Process(ref l,ref r,false,true);Assert.IsTrue(l>=0&&r>=0,"The next negative note must not enter the tail.");peak=Math.Max(peak,l);if(n>4410)Assert.AreEqual(0f,l);}
  Assert.IsTrue(peak>0);
 }
 [TestMethod] public void ReleaseEchoMuteClearsHistoryAndRetriggerStartsFresh()
 {
  var echo=new AudioMusicReleaseEcho(new(){DelaySeconds=.02f});
  for(int n=0;n<1000;n++){float l=.4f,r=0;echo.Process(ref l,ref r,true,true);}
  float a=0,b=0;echo.Process(ref a,ref b,false,false);
  for(int n=0;n<1000;n++){a=b=0;echo.Process(ref a,ref b,false,true);Assert.AreEqual(0f,a);}
  for(int n=0;n<1000;n++){a=0;b=.4f;echo.Process(ref a,ref b,true,true);}
  a=b=0;echo.Process(ref a,ref b,false,true);Assert.AreEqual(0f,a);Assert.IsTrue(b>0);
 }
 [TestMethod] public void SharedBackingReleaseEchoSurvivesGainCloseButNotSeekOrMute()
 {
  var pcm=Enumerable.Repeat((short)10000,44100*2).ToArray();
  var backing=new AudioMusicPcmBacking(new AudioMusicPcmBacking.Stem[]{new(pcm,1,Echo:new(){ReleaseOnly=true,DelaySeconds=.02f})});
  var renderer=new AudioMusicPcmRenderer(1,backing:backing);
  renderer.Render(new float[2205*2]);backing.SetGains(new[]{0f},new[]{true});
  var tail=new float[1000*2];renderer.Render(tail);Assert.IsTrue(tail.Any(x=>x>0));
  renderer.Seek(renderer.Position);var clear=new float[1000*2];renderer.Render(clear);Assert.IsTrue(clear.All(x=>x==0));
  backing.SetGains(new[]{1f});renderer.Render(new float[2205*2]);backing.SetGains(new[]{0f},new[]{false});
  renderer.Render(new float[1000*2]);renderer.Render(clear);Assert.IsTrue(clear.All(x=>x==0));
 }
 [TestMethod] public void RetainedBackingWithoutNewEffectFieldsStillRendersAfterReload()
 {
  var backing=new AudioMusicPcmBacking(Enumerable.Repeat((short)10000,2000).ToArray(),.5f);
  // Simulate old objects retained when the editor adds the new fields.
  var fields=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
  typeof(AudioMusicPcmBacking).GetField("echoes",fields).SetValue(backing,null);
  typeof(AudioMusicPcmBacking).GetField("targetGains",fields).SetValue(backing,null);
  backing.ResetEffects();
  var output=new float[200];backing.Render(output,0);
  Assert.IsTrue(output.All(x=>x>0),"A retained backing must still produce its dry music.");
  backing.SetGains(new[]{0f});backing.Render(new float[2000],100);
  var silent=new float[200];backing.Render(silent,1100);
  Assert.IsTrue(silent.All(x=>x==0),"Updated gain controls must still work.");
 }
}
