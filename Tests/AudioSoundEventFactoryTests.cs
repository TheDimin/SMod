using Sandbox;

namespace Smod.Tests;

[TestClass]
public sealed class AudioSoundEventFactoryTests
{
 [TestMethod]
 public void CreateSource_DefaultsToRandomAndOmitsInvalidSounds()
 {
  var source=AudioSoundEventFactory.CreateSource(new SoundFile[] { null, null });

  Assert.AreEqual(SoundTrackSelectionMode.Random,source.SelectionMode);
  Assert.AreEqual(0,source.Tracks.Count);
  Assert.AreNotEqual(Guid.Empty,source.Id);
 }
}
