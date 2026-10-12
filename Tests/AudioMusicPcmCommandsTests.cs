using Smod;
public sealed partial class AudioEventTests
{
	[TestMethod] public void MusicCommands_FadeRejectsFuturePhrasesAndPreservesCurrentSound()
	{
		var backing=new AudioMusicPcmBacking(new short[]{4096,4096,4096,4096},1);
		var core=new AudioMusicPcmRenderer(3,backing:backing);var commands=new AudioMusicPcmCommands(4);
		var owner=new AudioMusicRequestOwner();var pcm=Enumerable.Repeat((short)8192,40).ToArray();
		var active=commands.TryQueue(owner,pcm,0,1);var future=commands.TryQueue(owner,pcm,4,1);
		commands.Drain(core);core.Render(new float[2]);
		var pending=commands.TryQueue(owner,pcm,6,1);var seek=commands.Seek(100);
		commands.BeginFade();commands.BeginFade();
		Assert.IsFalse(pending.Admission.Result);Assert.IsFalse(seek.Result);
		Assert.IsNull(commands.TryQueue(owner,pcm,8,1));
		commands.Drain(core);var output=new float[12];core.Render(output);
		Assert.IsTrue(active.Admission.Result);Assert.IsTrue(future.Admission.Result);
		Assert.IsTrue(output.All(x=>x==.375f),"Current phrase and backing must continue without a new phrase or a short release ramp.");
		Assert.AreEqual(1,core.ActiveCount);Assert.AreEqual(7L,core.Position);Assert.IsFalse(owner.IsCancelled);
	}
	[TestMethod] public void MusicCommands_SeekMovesBackingTogetherAndRejectsOldPhrases()
	{
		var backing=new AudioMusicPcmBacking(new short[]{1000,-1000,2000,-2000,3000,-3000,4000,-4000},1);
		var core=new AudioMusicPcmRenderer(2,backing:backing);var commands=new AudioMusicPcmCommands(4);
		var processor=new AudioMusicPcmProcessor(core,4,commands);var owner=new AudioMusicRequestOwner();
		var old=commands.TryQueue(owner,new short[]{16000,16000},3,1);
		var seek=commands.Seek(2);Assert.IsFalse(seek.IsCompleted);
		var left=new float[1];var right=new float[1];processor.MixChannel(0,left);
		Assert.IsFalse(seek.IsCompleted,"A left channel alone must not complete a stereo seek.");
		processor.MixChannel(1,right);Assert.IsTrue(seek.Result);Assert.IsFalse(old.Admission.Result);
		Assert.AreEqual(3000/32768f,left[0]);Assert.AreEqual(-3000/32768f,right[0]);Assert.AreEqual(3L,processor.PublishedPosition);
		Assert.AreEqual(0,processor.PublishedActiveVoices);Assert.IsFalse(owner.IsCancelled);
		var fresh=commands.TryQueue(owner,new short[]{8192,-8192},3,1);
		processor.MixChannel(0,new float[1]);processor.MixChannel(1,new float[1]);Assert.IsTrue(fresh.Admission.Result);
	}
	[TestMethod] public void MusicCommands_SeekPreservesPauseCoalescesAndClosesPendingResult()
	{
		var core=new AudioMusicPcmRenderer(2);var commands=new AudioMusicPcmCommands(4);var processor=new AudioMusicPcmProcessor(core,4,commands);
		core.Schedule(new(),new short[]{100,100,100,100},0,1);
		commands.SetPaused(true);var first=commands.Seek(40);var latest=commands.Seek(80);
		Assert.IsFalse(first.Result);processor.MixChannel(0,new float[2]);processor.MixChannel(1,new float[2]);
		Assert.IsTrue(latest.Result);Assert.AreEqual(80L,processor.PublishedPosition);Assert.AreEqual(0,core.ActiveCount);
		commands.SetPaused(false);processor.MixChannel(0,new float[2]);processor.MixChannel(1,new float[2]);Assert.AreEqual(82L,processor.PublishedPosition);
		var cancelled=commands.Seek(100);processor.MixChannel(0,new float[2]);commands.Close();Assert.IsFalse(cancelled.Result);
		processor.MixChannel(1,new float[2]);Assert.IsFalse(commands.Seek(0).Result);
		Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>commands.Seek(-1));
	}
	[TestMethod] public void MusicCommands_AreAppliedOnlyAtStereoBoundary()
	{
		var core=new AudioMusicPcmRenderer(2);var commands=new AudioMusicPcmCommands(2);var processor=new AudioMusicPcmProcessor(core,4,commands);
		var owner=new AudioMusicRequestOwner();var request=commands.TryQueue(owner,new short[]{16384,-16384},0,1);
		Assert.IsFalse(request.Admission.IsCompleted);var left=new float[2];processor.MixChannel(0,left);
		Assert.IsTrue(request.Admission.Result);Assert.AreEqual(.5f,left[0]);
		var next=commands.TryQueue(owner,new short[]{8192,-8192},2,1);var right=new float[2];processor.MixChannel(1,right);
		Assert.IsFalse(next.Admission.IsCompleted);Assert.AreEqual(-.5f,right[0]);
		left=new float[2];right=new float[2];processor.MixChannel(0,left);processor.MixChannel(1,right);
		Assert.IsTrue(next.Admission.Result);Assert.AreEqual(.25f,left[0]);Assert.AreEqual(-.25f,right[0]);
	}
	[TestMethod] public void MusicCommands_BoundQueueAndRejectCancelledOrLateRequests()
	{
		var core=new AudioMusicPcmRenderer(2);var commands=new AudioMusicPcmCommands(2);var owner=new AudioMusicRequestOwner();
		short[] pcm={1,1};var cancelled=commands.TryQueue(owner,pcm,2,1);var late=commands.TryQueue(new(),pcm,0,1);
		Assert.IsNull(commands.TryQueue(new(),pcm,3,1));owner.Cancel();core.Render(new float[2]);commands.Drain(core);
		Assert.IsFalse(cancelled.Admission.Result);Assert.IsFalse(late.Admission.Result);Assert.AreEqual(0,core.ActiveCount);
		Assert.IsNotNull(commands.TryQueue(new(),pcm,3,1));
	}
	[TestMethod] public void MusicCommands_CloseResolvesPendingAndPreservesAlreadyAdmittedResult()
	{
		var core=new AudioMusicPcmRenderer(2);var commands=new AudioMusicPcmCommands(2);var owner=new AudioMusicRequestOwner();short[] pcm={1,1};
		var admitted=commands.TryQueue(owner,pcm,10,1);commands.Drain(core);
		var pending=commands.TryQueue(owner,pcm,12,1);commands.Close();commands.Close();
		Assert.IsTrue(admitted.Admission.Result);Assert.IsFalse(pending.Admission.Result);
		Assert.IsNull(commands.TryQueue(owner,pcm,14,1));commands.Drain(core);Assert.AreEqual(1,core.ActiveCount);
	}}

