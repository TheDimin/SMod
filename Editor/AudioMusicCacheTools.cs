using Smod;
namespace Editor.Mcp;
[McpToolset("audio_music_cache", "Inspect bounded prepared music memory and native decode counts.")]
public static class AudioMusicCacheTools
{
 public sealed record Status(bool Enabled,long Bytes,long Limit,int Ranges,long Hits,long Decodes,int Generation);
 /// <summary>Read shared prepared PCM bytes, cache hits and actual native decoder calls. Counters are cumulative; compare snapshots around a scenario. Cache bytes exclude active session and native decoder memory.</summary>
 [McpTool.ReadOnly("audio_music_cache_status")]
 public static Status Read()=>new(AudioMusicPreparationCache.Enabled,AudioMusicPreparationCache.ResidentBytes,256L*1024*1024,AudioMusicPreparationCache.RangeCount,AudioMusicPreparationCache.CacheHits,AudioMusicPreparationCache.NativeDecodes,AudioMusicPreparationCache.Generation);
 /// <summary>Clear only shared prepared music reuse for a cold managed-cache test. Active voices retain their PCM. Does not clear native codec or disk caches, change assets or stop music. Run check_audio_scenario and read audio_music_cache_status afterward.</summary>
 [McpTool("audio_music_cache_clear")]
 public static Status Clear(){AudioMusicPreparationCache.Invalidate();return Read();}
 /// <summary>Prepare all referenced shared-music graphs without playback, using the same preparation as the scene loading barrier. Existing cache is retained. Use audio_music_cache_clear first for a cold managed-cache check, then check_audio_scenario to verify no transition decodes. Native decode still runs on the owning thread.</summary>
 [McpTool("prepare_audio_music_graph")]
 public static async System.Threading.Tasks.Task<object> Prepare(string resourcePath)
 {
  var graph=AssetSystem.FindByPath(resourcePath)?.LoadResource<AudioEvent>()??throw new System.ArgumentException("Audio event required.");
  var result=await AudioMusicPreload.PrepareAsync(graph);
  return new{result.Graphs,result.Ranges,result.NativeDecodes,result.RetainedBytes};
 }
}
