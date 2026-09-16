using Smod;

public sealed partial class AudioEventTests
{
 [TestMethod]
 public void PreparedSources_ResolveLocalSharedIdentityWithoutFallback()
 {
  var source=new AudioSourceDefinition{Tracks=CreateSoundEvent().Tracks};
  var inputs=new Dictionary<string,AudioParameter>();
  var table=new AudioPreparedSources(new[]{source},inputs);
  var slot=table.ResolveIndex(source.Id);
  Assert.AreSame(source,table[slot]);
  Assert.AreEqual(slot,table.ResolveIndex(source.Id));
  Assert.ThrowsExactly<InvalidOperationException>(()=>table.ResolveIndex(Guid.NewGuid()));
  var other=new AudioPreparedSources(Array.Empty<AudioSourceDefinition>(),inputs);
  Assert.ThrowsExactly<InvalidOperationException>(()=>other.ResolveIndex(source.Id));
  Assert.ThrowsExactly<InvalidOperationException>(()=>table.ResolveIndex(Guid.Empty));
 }
 [TestMethod]
 public void PreparedSources_RejectDuplicateIdsAndInvalidSourceData()
 {
  var source=new AudioSourceDefinition{Tracks=CreateSoundEvent().Tracks};
  var inputs=new Dictionary<string,AudioParameter>();
  Assert.ThrowsExactly<InvalidOperationException>(()=>new AudioPreparedSources(new[]{source,source},inputs));
  source.Id=Guid.Empty;
  Assert.ThrowsExactly<InvalidOperationException>(()=>new AudioPreparedSources(new[]{source},inputs));
  source.Id=Guid.NewGuid();source.Volume=new(-1f);
  Assert.ThrowsExactly<InvalidOperationException>(()=>new AudioPreparedSources(new[]{source},inputs));
 }
 [TestMethod]
 public void PreparedSources_CachedSlotReadsDoNotAllocate()
 {
  var source=new AudioSourceDefinition{Tracks=CreateSoundEvent().Tracks};
  var table=new AudioPreparedSources(new[]{source},new Dictionary<string,AudioParameter>());
  var slot=table.ResolveIndex(source.Id);AudioSourceDefinition current=null;
  for(var i=0;i<100;i++)current=table[slot];
  var before=GC.GetAllocatedBytesForCurrentThread();
  for(var i=0;i<2000;i++)current=table[slot];
  var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
  Assert.AreSame(source,current);Assert.AreEqual(0L,allocated);
 }
}
