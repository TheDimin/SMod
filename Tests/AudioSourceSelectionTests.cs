using Smod;

public sealed partial class AudioEventTests
{
 [TestMethod]
 public void SourceHistory_HandlesRemovedAndDuplicateVariations()
 {
  var source = new object();
  var first = new SoundEventTrack();
  var second = new SoundEventTrack();
  Assert.AreSame(first, AudioSourceSelectionHistory.Select(source, SoundTrackSelectionMode.RandomExclusive, new[]{first}));
  Assert.AreSame(second, AudioSourceSelectionHistory.Select(source, SoundTrackSelectionMode.RandomExclusive, new[]{first, first, second}));
  Assert.AreSame(first, AudioSourceSelectionHistory.Select(source, SoundTrackSelectionMode.RandomExclusive, new[]{first, first}));
  Assert.AreSame(first, AudioSourceSelectionHistory.Select(source, SoundTrackSelectionMode.RandomExclusive, new[]{first, first}));
 }

 [TestMethod]
 public void SourceHistory_WarmExclusiveSelectionDoesNotAllocateCandidateArrays()
 {
  var source = new object();
  var choices = new[]{new SoundEventTrack(), new SoundEventTrack()};
  for(var i=0;i<100;i++) AudioSourceSelectionHistory.Select(source, SoundTrackSelectionMode.RandomExclusive, choices);
  var before=GC.GetAllocatedBytesForCurrentThread();
  for(var i=0;i<2000;i++) AudioSourceSelectionHistory.Select(source, SoundTrackSelectionMode.RandomExclusive, choices);
  Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread()-before);
 }
}
