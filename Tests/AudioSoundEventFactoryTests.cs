using Sandbox;

namespace Smod.Tests;

[TestClass]
public sealed class AudioSoundEventFactoryTests
{
	[TestMethod]
	public void EmbeddedMultiInstrumentDefaultsToRandomAndOmitsInvalidSounds()
	{
		var soundEvent = AudioSoundEventFactory.CreateEmbedded( new SoundFile[] { null, null } );

		Assert.AreEqual( SoundTrackSelectionMode.Random, soundEvent.SelectionMode );
		Assert.AreEqual( 0, soundEvent.Tracks.Count );
		Assert.IsNotNull( soundEvent.EmbeddedResource );
	}
}