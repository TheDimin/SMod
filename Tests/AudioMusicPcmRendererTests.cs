using Smod;
public sealed partial class AudioEventTests
{
	[TestMethod] public void MusicPcm_PreservesBackingAndStartsCompletePhraseInsideBlock()
	{
		var r = new AudioMusicPcmRenderer( 1 ); var owner = new AudioMusicRequestOwner();
		Assert.IsTrue( r.Schedule( owner, new short[] {16384,-16384,8192,-8192}, 1, 1 ) );
		var block = new float[] {.1f,.1f,.1f,.1f,.1f,.1f,.1f,.1f}; r.Render( block );
		CollectionAssert.AreEqual( new float[] {.1f,.1f,.6f,-.4f,.35f,-.15f,.1f,.1f}, block );
		Assert.AreEqual( 4L, r.Position ); Assert.AreEqual( 0, r.ActiveCount );
		Assert.IsTrue( r.Schedule( owner, new short[] {1,1}, 4, 1 ) );
	}
	[TestMethod] public void MusicPcm_PauseHoldsClockAndPhraseAcrossBlocks()
	{
		var r = new AudioMusicPcmRenderer( 1 ); r.Schedule( new(), new short[] {16384,16384,8192,8192}, 0, 1 );
		var first = new float[2]; r.Render( first ); r.Paused = true;
		var paused = new float[] {.2f,.2f}; r.Render( paused ); Assert.AreEqual( 1L,r.Position ); Assert.AreEqual(.2f,paused[0]);
		r.Paused=false;var last=new float[2];r.Render(last);Assert.AreEqual(.25f,last[0]);Assert.AreEqual(0,r.ActiveCount);
	}
	[TestMethod] public void MusicPcm_CancelsPendingOwnerAndReleasesOnlyActiveOwner()
	{
		var r = new AudioMusicPcmRenderer( 3, 2 ); var coffee = new AudioMusicRequestOwner(); var oven = new AudioMusicRequestOwner();
		short[] pcm = {16384,16384,16384,16384,16384,16384,16384,16384};
		r.Schedule(coffee,pcm,0,1);r.Schedule(coffee,pcm,10,1);r.Schedule(oven,pcm,0,1);
		r.Render(new float[2]);coffee.Cancel();var output=new float[6];r.Render(output);
		CollectionAssert.AreEqual(new float[]{1,1,.75f,.75f,.5f,.5f},output);Assert.AreEqual(0,r.ActiveCount);
		Assert.IsFalse(r.Schedule(coffee,pcm,r.Position,1));
	}
	[TestMethod] public void MusicPcm_RejectsLateAndOverCapacityRequests()
	{
		var r=new AudioMusicPcmRenderer(1);var owner=new AudioMusicRequestOwner();short[] pcm={1,1};
		r.Render(new float[4]);Assert.IsFalse(r.Schedule(owner,pcm,1,1));Assert.IsTrue(r.Schedule(owner,pcm,4,1));
		Assert.IsFalse(r.Schedule(owner,pcm,5,1));Assert.AreEqual(1,r.ActiveCount);
	}
}
