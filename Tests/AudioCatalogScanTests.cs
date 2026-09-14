using Editor;
public sealed partial class AudioEventTests
{
 [TestMethod] public async Task CatalogWindowsSharePendingAndCompletedScan()
 {
  var cache=new AudioCatalogScanCache<string[]>();var pending=new TaskCompletionSource<string[]>();var scans=0;
  Task<string[]> Scan(){scans++;return pending.Task;}
  var first=cache.Get(Scan);var second=cache.Get(Scan,true);
  Assert.AreSame(first,second);Assert.AreEqual(1,scans);
  pending.SetResult(["song.wav"]);await first;
  Assert.AreSame(first,cache.Get(Scan));Assert.AreEqual(1,scans);
  var refreshed=cache.Get(()=>{scans++;return Task.FromResult(new[]{"new.wav"});},true);
  CollectionAssert.AreEqual(new[]{"new.wav"},await refreshed);Assert.AreEqual(2,scans);
 }
 [TestMethod] public async Task CatalogRetriesFailureWithoutPoisoningOtherWindows()
 {
  var cache=new AudioCatalogScanCache<int>();
  try{await cache.Get(()=>Task.FromException<int>(new InvalidOperationException()));}catch(InvalidOperationException){}
  Assert.AreEqual(7,await cache.Get(()=>Task.FromResult(7)));
 }
}
