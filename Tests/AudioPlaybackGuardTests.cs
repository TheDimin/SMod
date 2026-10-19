using Smod;

[TestClass]
public sealed class AudioPlaybackGuardTests
{
	[TestMethod]
	public void DiagnosticFailureDoesNotEscapeOrPreventCleanup()
	{
		var guard = new AudioPlaybackGuard( _ => throw new InvalidOperationException( "Reporter failed" ) );
		Assert.IsFalse( guard.TryRun( () => throw new InvalidOperationException( "Audio failed" ) ) );
		var released = false;
		guard.Cleanup( () => released = true );
		Assert.IsTrue( released );
		Assert.IsTrue( guard.IsFaulted );
	}

	[TestMethod]
	public void FailedAudioDoesNotBlockGameplayAndReportsOnce()
	{
		var reports = 0;
		var calls = 0;
		var guard = new AudioPlaybackGuard( _ => reports++ );
		var progressed = 0;
		for ( var i = 0; i < 3; i++ )
		{
			Assert.IsFalse( guard.TryRun( () =>
			{
				calls++;
				throw new InvalidOperationException( "Audio source unavailable" );
			} ) );
			progressed++;
		}
		Assert.AreEqual( 3, progressed );
		Assert.AreEqual( 1, calls );
		Assert.AreEqual( 1, reports );
	}

	[TestMethod]
	public void CleanupContinuesAfterPlaybackAndStopFailures()
	{
		var reports = 0;
		var released = false;
		var guard = new AudioPlaybackGuard( _ => reports++ );
		guard.TryRun( () => throw new InvalidOperationException( "Playback failed" ) );
		guard.Cleanup( () => throw new InvalidOperationException( "Stop failed" ) );
		guard.Cleanup( () => released = true );
		Assert.IsTrue( released );
		Assert.AreEqual( 1, reports );
	}

	[TestMethod]
	public void SuccessfulAudioCanRunUntilItsOwnerStops()
	{
		var guard = new AudioPlaybackGuard( _ => Assert.Fail( "Unexpected audio failure" ) );
		var calls = 0;
		Assert.IsTrue( guard.TryRun( () => calls++ ) );
		Assert.IsTrue( guard.TryRun( () => calls++ ) );
		Assert.AreEqual( 2, calls );
		Assert.IsFalse( guard.IsFaulted );
	}
}
