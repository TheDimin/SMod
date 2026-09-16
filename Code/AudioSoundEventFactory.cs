namespace Smod;

public static class AudioSoundEventFactory
{
 public static AudioSourceDefinition CreateSource(IReadOnlyList<SoundFile> sounds)
 {
  ArgumentNullException.ThrowIfNull(sounds);
  return new AudioSourceDefinition
  {
   Tracks=sounds.Where(sound=>sound is not null && sound.IsValid())
    .Select(sound=>new SoundEventTrack{Sound=sound}).ToList()
  };
 }
}
