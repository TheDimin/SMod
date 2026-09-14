using Smod;
public sealed partial class AudioEventTests
{
	[TestMethod] public void MusicPcmProcessor_MixesBothChannelsAndAdvancesOnlyOnce()
	{
		var core=new AudioMusicPcmRenderer(1);core.Schedule(new(),new short[]{16384,-16384,8192,-8192},1,1);
		var adapter=new AudioMusicPcmProcessor(core,4);var left=new float[]{.1f,.1f};var right=new float[]{.2f,.2f};
		adapter.MixChannel(0,left);adapter.MixChannel(1,right);
		CollectionAssert.AreEqual(new float[]{.1f,.6f},left);CollectionAssert.AreEqual(new float[]{.2f,-.3f},right);
		Assert.AreEqual(2L,core.Position);
		left=new float[2];right=new float[2];adapter.MixChannel(0,left);adapter.MixChannel(1,right);
		Assert.AreEqual(.25f,left[0]);Assert.AreEqual(-.25f,right[0]);Assert.AreEqual(4L,core.Position);
		var surround=new float[]{.7f};adapter.MixChannel(2,surround);Assert.AreEqual(.7f,surround[0]);Assert.AreEqual(4L,core.Position);
	}
	[TestMethod] public void MusicPcmProcessor_RejectsIncompleteStereoSequence()
	{
		var core=new AudioMusicPcmRenderer(1);var adapter=new AudioMusicPcmProcessor(core,2);
		adapter.MixChannel(1,new float[2]);Assert.IsTrue(adapter.InvalidChannelSequence);Assert.AreEqual(0L,core.Position);
	}
}
