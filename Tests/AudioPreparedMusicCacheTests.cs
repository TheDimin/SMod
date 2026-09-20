using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void PreparedCacheReusesPcmAndEvictsLeastRecentlyUsedWithinBudget()
 {
  var cache=new AudioPreparedMusicCache<string>(16);
  var first=new short[4];var second=new short[4];var third=new short[4];
  cache.Add("a",first);cache.Add("b",second);
  Assert.IsTrue(cache.TryGet("a",out var hit));Assert.AreSame(first,hit);
  cache.Add("c",third);
  Assert.IsFalse(cache.TryGet("b",out _));Assert.IsTrue(cache.TryGet("a",out _));
  Assert.AreEqual(16L,cache.Bytes);Assert.AreEqual(2,cache.Count);
  cache.Add("large",new short[20]);Assert.AreEqual(16L,cache.Bytes);
  cache.Clear();Assert.AreEqual(0L,cache.Bytes);Assert.IsFalse(cache.TryGet("a",out _));
  Assert.AreEqual(4,first.Length,"Eviction must not mutate data owned by an active voice.");
 }
 [TestMethod] public void UnchangedMusicTargetsAllocateNothingAndTailChangesStillApply()
 {
  var backing=new AudioMusicPcmBacking(new short[2000],1);
  var gains=new[]{1f};var tails=new[]{true};
  for(int i=0;i<100;i++)backing.SetGains(gains,tails);
  var before=GC.GetAllocatedBytesForCurrentThread();
  for(int i=0;i<1000;i++)backing.SetGains(gains,tails);
  Assert.AreEqual(0L,GC.GetAllocatedBytesForCurrentThread()-before);
  Assert.ThrowsException<ArgumentException>(()=>backing.SetGains(gains,Array.Empty<bool>()));
  Assert.ThrowsException<ArgumentOutOfRangeException>(()=>backing.SetGains(new[]{float.NaN}));
 }
}
