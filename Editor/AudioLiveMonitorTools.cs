using Smod;
namespace Editor.Mcp;

[McpToolset("audio_live", "Observe active audio instances without changing playback.")]
public static class AudioLiveMonitorTools
{
 /// <summary>Open the audio event editor with its live overlay attached. Does not change game playback. Next use capture_audio_instances or inspect the editor.</summary>
 [McpTool("open_audio_live_monitor")]
 public static object Open(string resourcePath="") {
  if(string.IsNullOrWhiteSpace(resourcePath))resourcePath=AudioLiveDiagnostics.CaptureActive().FirstOrDefault()?.Resource;
  if(string.IsNullOrWhiteSpace(resourcePath))throw new InvalidOperationException("No active event. Open an audio event asset in the editor first.");
  AudioEventEditorWindow.OpenLiveEvent(resourcePath);return new {Opened=true,Resource=resourcePath};
 }
 /// <summary>Read active event parameters, source files, gain, clocks and pending music choices. Does not drive playback. Use open_audio_live_monitor for the visual view.</summary>
 /// <param name="limit">Maximum instances returned. Default 32, maximum 128.</param>
 [McpTool.ReadOnly("capture_audio_instances")]
 public static AudioLiveCapture Capture(int limit=32)
 {
  var all=AudioLiveDiagnostics.CaptureActive();
  return new(){Total=all.Count,Instances=all.Take(Math.Clamp(limit,1,128)).ToArray()};
 }
 /// <summary>Filter live instances by exact ID or resource path, include descendants, and show beat/bar position, pending transition wait, handoff phase, parameter values, and per-voice gain factors. Read-only; does not advance playback.</summary>
 [McpTool.ReadOnly("inspect_audio_live")]
 public static object InspectLive(string instanceId="",string resourcePath="")
 {
  var all=AudioLiveDiagnostics.CaptureActive();
  Guid? id=string.IsNullOrWhiteSpace(instanceId) ? null : Guid.Parse(instanceId);
  var chosen=all.Where(x=>(id is null || x.Id==id) && (string.IsNullOrWhiteSpace(resourcePath)||x.Resource==resourcePath)).Select(x=>x.Id).ToHashSet();
  for(var pass=0;pass<16;pass++)foreach(var x in all)if(chosen.Contains(x.ParentInstanceId))chosen.Add(x.Id);
  return new{Total=all.Count,Instances=all.Where(x=>chosen.Contains(x.Id)).Select(x=>new{Snapshot=x,
   Beat=x.Bpm>0 ? (x.Position-x.GridStart)*x.Bpm/60 : 0,
   Bar=x.Bpm>0 ? 1+Math.Floor((x.Position-x.GridStart)*x.Bpm/60/Math.Max(1,x.BeatsPerBar)) : 0}).ToArray()};
 }

}
public sealed class AudioLiveCapture
{
 public int Total {get;set;}
 public IReadOnlyList<AudioLiveSnapshot> Instances {get;set;}
}
