using Editor;
public sealed partial class AudioEventTests
{
 [TestMethod] public void LongClipOnlyDrawsViewportTiles()
 {
  var range=AudioVisibleTileRange.ForViewport(138240,65000,66428,512);
  Assert.AreEqual(126,range.First);Assert.AreEqual(130,range.End);
  Assert.IsTrue(range.End-range.First<=4,"A 1428 pixel viewport must not allocate the entire song image.");
 }
 [TestMethod] public void WaveformTilesCoverEdgesAndOffscreenClips()
 {
  Assert.AreEqual(new AudioVisibleTileRange(0,1),AudioVisibleTileRange.ForViewport(100,-20,80,512));
  Assert.AreEqual(new AudioVisibleTileRange(1,2),AudioVisibleTileRange.ForViewport(1024,512,1024,512));
  Assert.AreEqual(new AudioVisibleTileRange(0,0),AudioVisibleTileRange.ForViewport(1024,1024,2048,512));
  Assert.AreEqual(new AudioVisibleTileRange(0,0),AudioVisibleTileRange.ForViewport(1024,-100,0,512));
 }
}
