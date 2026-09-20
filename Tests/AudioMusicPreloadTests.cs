using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void MusicPreloadFindsSharedSongsAndExitGraphsOnce()
 {
  var song=new AudioEvent{SharedMusicOutput=true};var exit=new AudioEvent{SharedMusicOutput=true};var root=Nested(song);
  root.MasterTrack.Instruments[0].SubEvents[0].ExitEvent=exit;
  root.MasterTrack.Instruments[0].SubEvents.Add(new(){Event=song});
  CollectionAssert.AreEquivalent(new[]{song,exit},AudioMusicPreload.SharedGraphs(root));
 }
 [TestMethod] public async Task CancelledMusicPreloadDoesNotStartDecoding()
 {
  var before=AudioMusicPreparationCache.NativeDecodes;
  await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>AudioMusicPreload.PrepareAsync(new AudioEvent(),new System.Threading.CancellationToken(true)));
  Assert.AreEqual(before,AudioMusicPreparationCache.NativeDecodes);
 }
}
