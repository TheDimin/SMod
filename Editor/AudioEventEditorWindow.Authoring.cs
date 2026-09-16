using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Smod;
namespace Editor;

public sealed partial class AudioEventEditorWindow
{
 internal static AudioEventEditorWindow AuthoringWindow(string path)
 {
  UseSingleWindow();
  var asset=AssetSystem.FindByPath(path);
  if(asset is null || !Path.GetFullPath(asset.AbsolutePath).StartsWith(Path.GetFullPath(Project.Current.GetAssetsPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || !IAssetEditor.OpenInEditor(asset,out var editor) || editor is not AudioEventEditorWindow window)throw new ArgumentException("An existing project .audevt asset is required.");
  return window;
 }
 internal static string Fingerprint(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
 internal string AuthoringRevision()=>Fingerprint(_event.Serialize().ToJsonString()+"\n"+File.ReadAllText(_asset.AbsolutePath));
 private bool DiskMatchesEditor()
 {
  var disk=new AudioEvent();disk.LoadFromJson(File.ReadAllText(_asset.AbsolutePath));
  return JsonNode.DeepEquals(_event.Serialize(),disk.Serialize());
 }
 internal object AuthoringInspect()=>new {Path=_asset.RelativePath,Revision=AuthoringRevision(),Dirty=_dirtyResources.Contains(_event),
  DiskHash=Fingerprint(File.ReadAllText(_asset.AbsolutePath)),DiskMatchesEditor=DiskMatchesEditor(),OtherUnsavedResources=_dirtyResources.Where(r=>r!=_event).Select(r=>r.ResourcePath).ToArray(),State=_event.Serialize(),Validation=AudioEventValidator.Validate(_event)};
 internal object AuthoringEdit(string revision,JsonArray edits,string title,bool preview)
 {
  if(revision!=AuthoringRevision())throw new InvalidOperationException("Revision conflict. Inspect the event again before editing.");
  var before=_event.Serialize();var after=AudioAuthoringPatch.Apply(before,edits);
  var candidate=new AudioEvent();candidate.Deserialize(after);
  if(RefreshMissingParameterDependencies(candidate,after))
  {
   // Compilation can trigger reload callbacks. Never apply against a changed draft.
   if(revision!=AuthoringRevision())throw new InvalidOperationException("Audio dependencies were refreshed, but the event changed. Inspect it again before editing.");
   candidate=new AudioEvent();candidate.Deserialize(after);
  }
  var ids=AudioElementTarget.List(candidate).Where(t=>t.Pointer.Length>0).Select(t=>t.Id).ToArray();
  if(ids.Any(id=>id==Guid.Empty) || ids.Distinct().Count()!=ids.Length)throw new ArgumentException("Every audio element requires a unique permanent ID.");
  void CheckCycle(AudioEvent e,HashSet<AudioEvent> ancestors)
  {
   if(ReferenceEquals(e,_event))e=candidate;
   if(e is null)return;
   if(ancestors.Count>=16 || !ancestors.Add(e))throw new ArgumentException("The proposed edit creates a sub-event cycle or excessive depth.");
   foreach(var child in e.Tracks.Prepend(e.MasterTrack).Where(t=>t is not null).SelectMany(t=>t.Instruments ?? new()).Where(i=>i is not null).SelectMany(i=>i.SubEvents ?? new())){CheckCycle(child.Event,ancestors);CheckCycle(child.ExitEvent,ancestors);}
   ancestors.Remove(e);
  }
  CheckCycle(candidate,new());
  var validation=AudioEventValidator.Validate(candidate);
  if(!validation.IsValid)return new{Applied=false,Validation=validation};
  if(preview)return new{Applied=false,Preview=true,Before=before,After=after,Validation=validation};
  // A library reload can add serialized defaults to an otherwise clean resource.
  // Refresh the baseline only when the complete pre-edit state equals current disk.
  var diskBeforeEdit=ReadSavedAudioState(_event);
  if(AudioDocumentSaveState.Matches(before,diskBeforeEdit)) _savedStates[_event]=diskBeforeEdit;
  ExecuteResourceAction(title,_event,()=>_event.Deserialize(after));
  return AuthoringInspect();
 }
 // Recover only the known unloaded-parameter case. Invalid authored parameters
 // still fail validation; dirty parameter drafts are never compiled over.
 private bool RefreshMissingParameterDependencies(AudioEvent candidate,JsonObject state)
 {
  var missing=(candidate.Parameters ?? new()).Select((parameter,index)=>(parameter,index))
   .Where(item=>item.parameter is null || string.IsNullOrWhiteSpace(item.parameter.Name)).ToArray();
  if(missing.Length==0)return false;
  if(_dirtyResources.Any(resource=>resource is AudioParameter))
   throw new InvalidOperationException("An audio parameter dependency is not ready. A parameter has unsaved changes; save or resolve that draft before refreshing dependencies.");
  var assets=new List<Asset>();
  var root=Path.GetFullPath(Project.Current.GetAssetsPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
  foreach(var item in missing)
  {
   if(state["Parameters"] is not JsonArray references || item.index>=references.Count
    || references[item.index] is not JsonValue value || !value.TryGetValue<string>(out var path)
    || !path.EndsWith(".audparam",StringComparison.OrdinalIgnoreCase))return false;
   var asset=AssetSystem.FindByPath(path);
   if(asset is null || !Path.GetFullPath(asset.AbsolutePath).StartsWith(root,StringComparison.OrdinalIgnoreCase))return false;
   var disk=JsonNode.Parse(File.ReadAllText(asset.AbsolutePath));
   var name=disk?["Name"]?.GetValue<string>();
   if(string.IsNullOrWhiteSpace(name) || name!=name.Trim())return false;
   assets.Add(asset);
  }
  foreach(var asset in assets.Distinct())
  {
   if(!asset.Compile(false))throw new InvalidOperationException("Audio parameter compilation failed: "+asset.RelativePath);
  }
  return true;
 }
 internal object AuthoringUndo(bool redo)
 {
  var changed=redo ? Redo() : Undo();
  return new{Changed=changed,Event=AuthoringInspect()};
 }
 internal object AuthoringSave(string revision)
 {
  if(revision!=AuthoringRevision())throw new InvalidOperationException("Revision conflict. Inspect the event again before saving.");
  AudioEventValidator.Validate(_event).AssertValid();
  EnsureAudioSaveIsCurrent(_event);
  _saving=true;
  try
  {
   if(!_asset.SaveToDisk(_event))throw new IOException("Failed to save "+_asset.RelativePath);
   var roundtrip=new AudioEvent();roundtrip.LoadFromJson(File.ReadAllText(_asset.AbsolutePath));
   if(!JsonNode.DeepEquals(_event.Serialize(),roundtrip.Serialize()))throw new IOException("Saved event differs from editor state: "+_asset.RelativePath);
   _savedStates[_event]=_event.Serialize();_lastStates[_event]=_event.Serialize();_dirtyResources.Remove(_event);UpdateWindowTitle();
   return AuthoringInspect();
  }
  finally{_saving=false;}
 }
}
