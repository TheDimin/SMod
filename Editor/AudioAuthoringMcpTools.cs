using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Smod;
namespace Editor.Mcp;

[McpToolset("audio_authoring", "Inspect, edit with undo, save and test Audio Event graphs through the native editor.")]
public static class AudioAuthoringMcpTools
{
 /// <summary>Resolve a smod: link copied by Get Path in an Audio Event context menu. Returns the exact current element and JSON pointer, including unsaved editor state. focus selects and frames it without playback or saving. Short path-plus-GUID links survive element edits and moves within the event. Missing or duplicate IDs fail instead of guessing. Library links return their address only; no import, download or external file read.</summary>
 [McpTool("resolve_audio_path")]
 public static object ResolvePath(string link, bool focus = true)
 {
  var address = AudioElementPath.Parse(link);
  if (address.Source != "element") return new { address.Source, address.Resource, Focused = false };
  return AudioEventEditorWindow.AuthoringWindow(address.Resource).ResolveElementPath(address, focus);
 }

 /// <summary>Open an event and return full editable state, disk hash, validation, dirty flag and revision token. Reads current editor values, including unsaved changes. Does not reload over them.</summary>
 [McpTool("inspect_audio_authoring")]
 public static object Inspect(string resourcePath)=>AudioEventEditorWindow.AuthoringWindow(resourcePath).AuthoringInspect();

 /// <summary>Apply 1-128 structured JSON pointer operations to one event through native editor undo. editsJson is an array of {op:replace|add|remove,path:/Tracks/0/Volume,value:0.7}. Add uses an array index or /-. Inspect first for property paths and expectedRevision. previewOnly validates and returns before/after without mutation. Does not save files. No audio binary edits.</summary>
 [McpTool("edit_audio_authoring")]
 public static object Edit(string resourcePath,string expectedRevision,string editsJson,string title="Edit Audio Event",bool previewOnly=false)
  =>AudioEventEditorWindow.AuthoringWindow(resourcePath).AuthoringEdit(expectedRevision,JsonNode.Parse(editsJson)?.AsArray() ?? throw new ArgumentException("Edits array required."),title,previewOnly);

 /// <summary>Undo or redo an edit in the event's native editor. Returns the new revision and state; does not save files.</summary>
 [McpTool("undo_audio_authoring")]
 public static object Undo(string resourcePath,bool redo=false)=>AudioEventEditorWindow.AuthoringWindow(resourcePath).AuthoringUndo(redo);

 /// <summary>Save only the selected Audio Event with its inspected revision, then verify disk read-back. Does not save referenced child-event drafts. Use when other open audio resources have unrelated user edits; inspect_audio_authoring returns their paths.</summary>
 [McpTool("save_audio_event_verified")]
 public static object SaveEvent(string resourcePath,string expectedRevision)
  =>AudioEventEditorWindow.AuthoringWindow(resourcePath).AuthoringSave(expectedRevision);

 private static List<AudioEvent> Graph(string root)
 {
  var result=new List<AudioEvent>();var seen=new HashSet<AudioEvent>();
  void Visit(AudioEvent e,int depth)
  {
   if(e is null || !seen.Add(e))return;
   if(depth>16 || result.Count>=128)throw new ArgumentException("Graph exceeds inspection limit.");
   foreach(var choice in e.Tracks.Prepend(e.MasterTrack).Where(t=>t is not null).SelectMany(t=>t.Instruments).Where(i=>i is not null).SelectMany(i=>i.SubEvents ?? new())){Visit(choice.Event,depth+1);Visit(choice.ExitEvent,depth+1);}
   result.Add(e);
  }
  Visit(AudioEventEditorWindow.AuthoringWindow(root).Event,0);return result;
 }
 /// <summary>Return every child event, editable state, revision, dirty state and graph validation. Dependencies come first. Shared children appear once. Does not decode audio or reload unsaved values.</summary>
 [McpTool("inspect_audio_graph")]
 public static object InspectGraph(string resourcePath)
 {
  var events=Graph(resourcePath);
  return new{Root=resourcePath,Validation=AudioEventValidator.Validate(events.Last()),Events=events.Select(e=>AudioEventEditorWindow.AuthoringWindow(e.ResourcePath).AuthoringInspect()).ToArray()};
 }
 /// <summary>Validate the entire graph, preflight all event revisions, save children then parent, and read back each file. revisionsJson maps every event path from inspect_audio_graph to its revision. Reports verified and failed paths. Multi-file disk writes are sequential, not atomic. Does not save unrelated sound/parameter edits or audio binaries.</summary>
 [McpTool("save_audio_graph_verified")]
 public static object SaveGraph(string resourcePath,string revisionsJson)
 {
  var events=Graph(resourcePath);var revisions=JsonNode.Parse(revisionsJson).AsObject();
  AudioEventValidator.Validate(events.Last()).AssertValid();
  foreach(var e in events)
  {
   var w=AudioEventEditorWindow.AuthoringWindow(e.ResourcePath);
   if(revisions[e.ResourcePath]?.GetValue<string>()!=w.AuthoringRevision())throw new InvalidOperationException("Revision conflict or missing token: "+e.ResourcePath);
  }
  var verified=new List<object>();
  foreach(var e in events)
  {
   var w=AudioEventEditorWindow.AuthoringWindow(e.ResourcePath);
   try{verified.Add(w.AuthoringSave(revisions[e.ResourcePath].GetValue<string>()));}
   catch(Exception ex){return new{Success=false,Verified=verified,FailedPath=w.Event.ResourcePath,Error=ex.Message};}
  }
  return new{Success=true,Verified=verified};
 }
 /// <summary>Run a bounded muted scenario on a temporary instance. stepsJson is an array with parameters:{Intensity:0.8,WorldState:2}, seekSeconds (parent), seekToSongEndLeadSeconds (active child only; finite 0 to child grid duration), paused, waitSeconds, expectSongUnchanged or expectSongChanged (relative to preceding step). Numeric parameters are applied as one batch; labels are applied individually first. Parameters precede seek: put seek in an earlier step when testing new triggers. Total wait at most 20 seconds, at most 32 steps. Returns snapshots, SongEndSeek positions, and explicit checks; always stops/releases the temporary instance. Does not change active gameplay or save resources. Timing proof is not listening proof.</summary>
 [McpTool("check_audio_scenario")]
 public static async Task<object> Scenario(string resourcePath,string stepsJson)
 {
  var steps=JsonNode.Parse(stepsJson).AsArray();
  if(steps.Count is <1 or >32 || steps.Sum(s=>s["waitSeconds"]?.GetValue<float>() ?? 0)>20 || steps.Any(s=>(s["waitSeconds"]?.GetValue<float>() ?? 0)<0))throw new ArgumentException("Scenario must contain 1-32 steps and 0-20 seconds of total wait.");
  var resource=AudioEventEditorWindow.AuthoringWindow(resourcePath).Event;AudioEventValidator.Validate(resource).AssertValid();
  var instance=AudioSystem.GetEvent(resource).CreateInstance();instance.Volume=0;
  var reports=new List<object>();var passed=true;string previous=null;
  try
  {
   var started=false;
   foreach(var step in steps)
   {
    if(step["parameters"] is JsonObject parameters)
    {
     var numeric=new Dictionary<string,float>();
     foreach(var pair in parameters)
     {
      if(pair.Value is JsonValue value && value.TryGetValue<string>(out var label))instance.SetParameterByNameWithLabel(pair.Key,label);
      else numeric.Add(pair.Key,pair.Value.GetValue<float>());
     }
     instance.SetParametersByName(numeric);
    }
    if(!started)
    {
     // Initial world facts must participate in the first song selection.
     instance.Start();started=true;
     previous=instance.InstrumentPlaybackStates.FirstOrDefault().ChildEvent;
    }
    if(step["paused"] is {} pause)instance.Paused=pause.GetValue<bool>();
    if(step["seekSeconds"] is {} seek)instance.TimelinePosition=seek.GetValue<float>();
    object songEndSeek=null;
    if(step["seekToSongEndLeadSeconds"] is {} lead)
    {
     var active=instance.InstrumentPlaybackStates.FirstOrDefault();
     var child=AudioLiveDiagnostics.CaptureActive().FirstOrDefault(x=>x.Id==active.ChildInstanceId) ?? throw new ArgumentException("End seek requires an active song child.");
     var leadSeconds=lead.GetValue<float>();
     if(!float.IsFinite(leadSeconds) || leadSeconds<0 || leadSeconds>child.GridEnd-child.GridStart)
      throw new ArgumentException("Song-end lead must be finite and within the active child grid duration.");
     var childInstance=AudioLiveDiagnostics.FindForAudition(child.Id) ?? throw new ArgumentException("The scenario child is no longer active.");
     var parentBefore=instance.TimelinePosition;
     var requested=(float)child.GridEnd-leadSeconds;
     childInstance.TimelinePosition=requested;
     songEndSeek=new{Child=child.Id,Before=child.Position,Requested=requested,ParentBefore=parentBefore,ParentAfter=instance.TimelinePosition};
    }
    var wait=step["waitSeconds"]?.GetValue<float>() ?? 0;
    var clock=System.Diagnostics.Stopwatch.StartNew();
    while(clock.Elapsed.TotalSeconds<wait)
    {
     // The tool also runs outside play mode, where no game update drives preparation.
     _=instance.InstrumentPlaybackStates;
     await Task.Delay(Math.Max(1,Math.Min(10,(int)((wait-clock.Elapsed.TotalSeconds)*1000))));
    }
    var state=instance.InstrumentPlaybackStates.FirstOrDefault();var song=state.ChildEvent;
    var ok=(step["expectSongUnchanged"]?.GetValue<bool>()!=true || song==previous) && (step["expectSongChanged"]?.GetValue<bool>()!=true || song!=previous);
    if(step["expectState"] is {} expected)ok &= instance.PlaybackState.ToString()==expected.GetValue<string>();
    if(step["expectPaused"] is {} expectedPause)ok &=instance.Paused==expectedPause.GetValue<bool>();
    passed &=ok;
    reports.Add(new{Passed=ok,Song=song,Instance=instance.LiveId,SongEndSeek=songEndSeek,Snapshots=AudioLiveDiagnostics.CaptureActive().Where(x=>x.Id==instance.LiveId || x.ParentInstanceId==instance.LiveId).ToArray()});previous=song;
   }
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
  var cleaned=!AudioLiveDiagnostics.CaptureActive().Any(x=>x.Id==instance.LiveId || x.ParentInstanceId==instance.LiveId);
  return new{Passed=passed&&cleaned,Cleaned=cleaned,Steps=reports};
 }
}
