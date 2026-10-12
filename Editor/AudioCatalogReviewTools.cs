using System.Diagnostics;
using System.Threading.Tasks;
namespace Editor.Mcp;
[McpToolset("audio_catalog", "Check shared external audio catalog scans.")]
public static class AudioCatalogReviewTools
{
 [McpTool("check_audio_catalog_cache")]
 public static async Task<object> Check()
 {
  var before=ExternalAudioSourceCatalog.ScanCount;
  var watch=Stopwatch.StartNew();
  var first=ExternalAudioSourceCatalog.ScanSharedAsync(true);
  var second=ExternalAudioSourceCatalog.ScanSharedAsync(true);
  var dispatchMs=watch.Elapsed.TotalMilliseconds;
  var files=await first;await second;
  var elapsedMs=watch.Elapsed.TotalMilliseconds;
  var scans=ExternalAudioSourceCatalog.ScanCount-before;
  watch.Restart();
  for(var i=0;i<100;i++)await ExternalAudioSourceCatalog.ScanSharedAsync();
  return new {Files=files.Length,SharedPending=ReferenceEquals(first,second),DispatchMs=dispatchMs,ScanElapsedMs=elapsedMs,
   ColdScans=scans,WarmRequests=100,WarmMs=watch.Elapsed.TotalMilliseconds,WarmScans=ExternalAudioSourceCatalog.ScanCount-before-scans};
 }
 [McpTool("check_audio_timeline_zoom")]
 public static async Task<object> CheckZoom(string resourcePath)
 {
  var asset=AssetSystem.FindByPath(resourcePath);
  if(asset is null || !IAssetEditor.OpenInEditor(asset,out var editor) || editor is not AudioEventEditorWindow window)throw new System.ArgumentException("Audio Event is required.");
  return await window.CheckTimelineZoomAsync();
 }
 [McpTool("check_audio_event_browser")]
 public static object CheckEventBrowser()=>AudioEventEditorWindow.CheckEventBrowser();
}
