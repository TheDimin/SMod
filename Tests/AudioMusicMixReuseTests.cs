using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void StableMixCaptureReusesSnapshotAndMuteKeepsPriorSnapshotUnchanged()
 {
  var resource=CreateEvent();var instance=AudioSystem.GetEvent(resource).CreateInstance();
  try
  {
   var original=instance.CaptureSharedMusicMix();
   for(int i=0;i<100;i++)instance.CaptureSharedMusicMix();
   var before=GC.GetAllocatedBytesForCurrentThread();
   for(int i=0;i<1000;i++)Assert.AreSame(original,instance.CaptureSharedMusicMix());
   Assert.AreEqual(0L,GC.GetAllocatedBytesForCurrentThread()-before);
   instance.SetTrackMutedForAudition(resource.MasterTrack,true);
   var muted=instance.CaptureSharedMusicMix();Assert.AreEqual(0f,muted.Gain(resource.MasterTrack));
   Assert.IsFalse(muted.TailAllowed(resource.MasterTrack));Assert.AreEqual(1f,original.Gain(resource.MasterTrack));
   instance.SetTrackMutedForAudition(resource.MasterTrack,false);
   Assert.AreEqual(1f,instance.CaptureSharedMusicMix().Gain(resource.MasterTrack));
   Assert.AreEqual(0f,muted.Gain(resource.MasterTrack));
  }
  finally{instance.Release();}
 }
 [TestMethod] public void RepositionedMixSharesValuesWithoutChangingOldPosition()
 {
  var track=new AudioTrack();var mix=new AudioMusicMixState(0,new Dictionary<AudioTrack,float>{{track,.25f}},new[]{track});
  var next=mix.WithPosition(2);
  Assert.AreEqual(0f,mix.Position);Assert.AreEqual(2f,next.Position);Assert.AreEqual(.25f,next.Gain(track));Assert.IsFalse(next.TailAllowed(track));
 }
}
