using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Smod;
namespace Editor;
public sealed partial class AudioEventEditorWindow
{
 private JsonObject ReadSavedAudioState(GameResource resource)
 {
  var asset=ResolveAsset(resource);
  if(asset is null)throw new IOException("Cannot locate audio resource: "+resource.ResourcePath);
  GameResource copy=resource switch
  {
   AudioEvent => new AudioEvent(),
   SoundEventResource => new SoundEventResource(),
   AudioParameter => new AudioParameter(),
   _ => throw new InvalidOperationException("Unsupported audio resource: "+resource.ResourcePath)
  };
  copy.LoadFromJson(File.ReadAllText(asset.AbsolutePath));
  return copy.Serialize();
 }
 private void ReconcileSavedAudio()
 {
  foreach(var resource in _dirtyResources.ToArray())
  {
   try
   {
    var current=resource.Serialize();var disk=ReadSavedAudioState(resource);
    if(!AudioDocumentSaveState.Matches(current,disk))continue;
    _savedStates[resource]=disk;_lastStates[resource]=current;_dirtyResources.Remove(resource);
   }
   catch(Exception error){Log.Warning("Cannot check saved audio: "+error.Message);}
  }
  UpdateWindowTitle();
 }
 private void EnsureAudioSaveIsCurrent(GameResource resource)
 {
  var disk=ReadSavedAudioState(resource);
  if(!_savedStates.TryGetValue(resource,out var saved)
   || AudioDocumentSaveState.Conflicts(saved,resource.Serialize(),disk))
   throw new IOException("Audio file changed outside this editor. Save stopped to protect the newer file: "+resource.ResourcePath);
 }
 private void DiscardAudioDrafts()
 {
  // Restore the current disk version, never an old snapshot from when the window opened.
  var states=_dirtyResources.ToDictionary(r=>r,ReadSavedAudioState);
  foreach(var pair in states)
  {
   pair.Key.Deserialize(pair.Value);_savedStates[pair.Key]=pair.Value;
   _lastStates[pair.Key]=pair.Value;_dirtyResources.Remove(pair.Key);
  }
 }
}
