namespace Smod;

/// <summary>Bounded immutable PCM reuse between music sessions. Eviction drops only cache ownership.</summary>
internal sealed class AudioPreparedMusicCache<TKey> where TKey:notnull
{
 readonly long limit;
 readonly Dictionary<TKey,LinkedListNode<(TKey Key,short[] Pcm)>> entries=new();
 readonly LinkedList<(TKey Key,short[] Pcm)> order=new();
 internal long Bytes {get;private set;}
 internal int Count=>entries.Count;
 internal long Hits {get;private set;}
 internal AudioPreparedMusicCache(long limit){if(limit<0)throw new ArgumentOutOfRangeException(nameof(limit));this.limit=limit;}
 internal bool TryGet(TKey key,out short[] pcm)
 {
  if(!entries.TryGetValue(key,out var node)){pcm=null;return false;}
  order.Remove(node);order.AddLast(node);Hits++;pcm=node.Value.Pcm;return true;
 }
 internal void Add(TKey key,short[] pcm)
 {
  if(entries.ContainsKey(key)||pcm.LongLength*2>limit)return;
  while(Bytes+pcm.LongLength*2>limit)
  {
   var first=order.First;entries.Remove(first.Value.Key);Bytes-=first.Value.Pcm.LongLength*2;order.RemoveFirst();
  }
  var node=order.AddLast((key,pcm));entries.Add(key,node);Bytes+=pcm.LongLength*2;
 }
 internal void Clear(){entries.Clear();order.Clear();Bytes=0;}
}

/// <summary>Music sample reuse statistics. All access is on the owning game/editor thread.</summary>
public static class AudioMusicPreparationCache
{
 internal static readonly AudioPreparedMusicCache<AudioMusicSourceBank.Key> Ranges=new(256L*1024*1024);
 // Editor installs asset-change invalidation before allowing persistent reuse.
 public static bool EditorInvalidationReady {get;set;}
 internal static bool Enabled=>!Application.IsEditor || EditorInvalidationReady;
 public static long ResidentBytes=>Ranges.Bytes;
 public static long CacheHits=>Ranges.Hits;
 public static int RangeCount=>Ranges.Count;
 public static int Generation {get;private set;}
 public static long NativeDecodes {get;internal set;}
 public static void Invalidate(){Generation++;Ranges.Clear();}
}
