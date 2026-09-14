using Smod;
public sealed partial class AudioEventTests
{
	[TestMethod] public void MusicEventOwner_StopAndRestartDoNotAffectAnotherInstance()
	{
		var first=AudioSystem.GetEvent(CreateEvent()).CreateInstance();var second=AudioSystem.GetEvent(CreateEvent()).CreateInstance();
		first.Start();second.Start();var a=first.MusicRequestOwner;var b=second.MusicRequestOwner;
		Assert.AreSame(a,first.MusicRequestOwner);first.Stop(AudioStopMode.Immediate);
		Assert.IsTrue(a.IsCancelled);Assert.IsFalse(b.IsCancelled);
		first.Start();var fresh=first.MusicRequestOwner;Assert.AreNotSame(a,fresh);Assert.IsFalse(fresh.IsCancelled);
		first.Stop(AudioStopMode.Immediate);second.Stop(AudioStopMode.Immediate);
	}
	[TestMethod] public void MusicEventOwner_ReleasePreservesScheduledWorkButPreventsNewAccess()
	{
		var instance=AudioSystem.GetEvent(CreateEvent()).CreateInstance();instance.Start();var owner=instance.MusicRequestOwner;
		instance.Release();Assert.IsFalse(owner.IsCancelled);
		Assert.ThrowsExactly<InvalidOperationException>(()=>{_=instance.MusicRequestOwner;});
	}
	[TestMethod] public void MusicEventOwner_NaturalCompletionCancelsPendingRequests()
	{
		var instance=AudioSystem.GetEvent(CreateEvent()).CreateInstance();instance.Start();var owner=instance.MusicRequestOwner;
		_backend.Voices[^1].IsPlaying=false;Assert.AreEqual(AudioPlaybackState.Stopped,instance.PlaybackState);
		Assert.IsTrue(owner.IsCancelled);Assert.ThrowsExactly<InvalidOperationException>(()=>{_=instance.MusicRequestOwner;});
	}
	[TestMethod] public void MusicEventOwner_TwoEventsShareQueueAndStoppingOneKeepsOtherPhrase()
	{
		var coffee=AudioSystem.GetEvent(CreateEvent()).CreateInstance();var oven=AudioSystem.GetEvent(CreateEvent()).CreateInstance();coffee.Start();oven.Start();
		var queue=new AudioMusicPcmCommands(2);var renderer=new AudioMusicPcmRenderer(2);
		var a=queue.TryQueue(coffee.MusicRequestOwner,new short[]{8192,8192},1,1);
		var b=queue.TryQueue(oven.MusicRequestOwner,new short[]{16384,-16384},1,1);
		coffee.Stop(AudioStopMode.Immediate);queue.Drain(renderer);var output=new float[6];renderer.Render(output);
		Assert.IsFalse(a.Admission.Result);Assert.IsTrue(b.Admission.Result);
		CollectionAssert.AreEqual(new float[]{0,0,.5f,-.5f,0,0},output);oven.Stop(AudioStopMode.Immediate);
	}}

