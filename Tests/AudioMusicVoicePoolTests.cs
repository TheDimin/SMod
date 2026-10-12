using Smod;
public sealed partial class AudioEventTests
{
	[TestMethod] public void MusicVoicePool_EnforcesCapacityAndRejectsStaleCompletion()
	{
		var pool = new AudioMusicVoicePool( 1 ); var owner = new AudioMusicRequestOwner();
		var old = pool.TryReserve( owner ); Assert.IsNotNull( old );
		Assert.IsNull( pool.TryReserve( owner ) ); Assert.IsTrue( pool.Complete( old ) );
		var replacement = pool.TryReserve( owner ); Assert.IsNotNull( replacement );
		Assert.IsFalse( pool.Complete( old ) ); Assert.IsNull( pool.TryReserve( owner ) );
		Assert.IsTrue( pool.Complete( replacement ) );
	}
	[TestMethod] public void MusicVoicePool_CancellationIsPerProcessAndRetainsSlotUntilRendererCompletes()
	{
		var pool = new AudioMusicVoicePool( 2 ); var coffee = new AudioMusicRequestOwner(); var oven = new AudioMusicRequestOwner();
		var a = pool.TryReserve( coffee ); var b = pool.TryReserve( oven ); coffee.Cancel();
		Assert.IsTrue( a.IsCancelled ); Assert.IsFalse( b.IsCancelled );
		Assert.IsNull( pool.TryReserve( coffee ) ); Assert.IsNull( pool.TryReserve( oven ) );
		Assert.IsTrue( pool.Complete( a ) ); Assert.IsNotNull( pool.TryReserve( oven ) );
	}
	[TestMethod] public void MusicVoicePool_RejectsForeignLeaseAndCancelledOwnerCannotRestart()
	{
		var first = new AudioMusicVoicePool( 1 ); var second = new AudioMusicVoicePool( 1 );
		var oldOwner = new AudioMusicRequestOwner(); var old = first.TryReserve( oldOwner );
		Assert.IsFalse( second.Complete( old ) ); oldOwner.Cancel(); first.Complete( old );
		Assert.IsNull( first.TryReserve( oldOwner ) );
		Assert.IsNotNull( first.TryReserve( new AudioMusicRequestOwner() ) );
	}
}
