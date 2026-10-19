using Sandbox;
using Smod;

public sealed partial class AudioEventTests
{
	[TestMethod]
	public void OptionalPlaybackRejectsInvalidGraphWithoutThrowing()
	{
		Assert.IsFalse( AudioSystem.TryPlay( new AudioEvent() ) );
		Assert.AreEqual( 0, _backend.Voices.Count );
	}

	[TestMethod]
	public void OptionalPlaybackContainsConfigurationFailure()
	{
		var audioEvent = CreateEvent();
		Assert.IsFalse( AudioSystem.TryPlay( audioEvent, _ => throw new InvalidOperationException( "Audio configuration failed" ) ) );
		Assert.AreEqual( 0, _backend.Voices.Count );
	}

#if SMOD_NATIVE_TEST_HOST
	[TestMethod]
	public void SoundPointContainsInvalidGraphOnPlayAndCleanup()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		try
		{
			var point = scene.CreateObject().AddComponent<Smod.SoundPointComponent>( false );
			point.AudioEvent = new AudioEvent();
			point.Play();
			Assert.AreEqual( AudioPlaybackState.Stopped, point.PlaybackState );
			point.Stop();
			point.Destroy();
		}
		finally { scene.Destroy(); }
	}
#endif
}
