namespace Smod;

internal static class AudioSoundEventFactory
{
	public static SoundEventResource CreateEmbedded( IReadOnlyList<SoundFile> sounds )
	{
		ArgumentNullException.ThrowIfNull( sounds );
		var soundEvent = new SoundEventResource
		{
			Tracks = sounds
				.Where( sound => sound is not null && sound.IsValid() )
				.Select( sound => new SoundEventTrack { Sound = sound } )
				.ToList()
		};
		soundEvent.EmbeddedResource = new Sandbox.Resources.EmbeddedResource { ResourceCompiler = "embed" };
		return soundEvent;
	}
}
