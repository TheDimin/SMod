using System;
using System.Linq;
using Smod;
namespace Editor;

public sealed partial class AudioEventEditorWindow
{
 internal static AudioEventEditorWindow UseSingleWindow()
 {
  var windows=IAssetEditor.OpenSingleEditors.Values.Concat(IAssetEditor.OpenMultiAssetEditors.Values)
   .OfType<AudioEventEditorWindow>().Where(w=>w.IsValid).Distinct().ToList();
  if(lastWindow is not null && lastWindow.TryGetTarget(out var last) && last.IsValid && !windows.Contains(last))windows.Add(last);
  var selected=windows.OrderByDescending(w=>w._dirtyResources.Count).FirstOrDefault()??new AudioEventEditorWindow();
  foreach(var other in windows.Where(w=>!ReferenceEquals(w,selected)))
  {
   // Keep conflicting drafts open rather than silently choosing one version.
   var conflict=other._dirtyResources.Any(r=>selected._dirtyResources.Any(existing=>
    !ReferenceEquals(existing,r) && !string.IsNullOrEmpty(r.ResourcePath) && string.Equals(existing.ResourcePath,r.ResourcePath,StringComparison.OrdinalIgnoreCase) && !StatesEqual(existing.Serialize(),r.Serialize())));
   if(conflict){Log.Warning("Audio editor has conflicting unsaved drafts. Save or discard one draft before consolidating: "+other._asset?.Path);continue;}
   foreach(var pair in other._savedStates)
   {
    var resource = pair.Key;
    var canonical = selected._savedStates.Keys.FirstOrDefault(existing => ReferenceEquals(existing, resource)
     || (!string.IsNullOrEmpty(resource.ResourcePath) && existing.GetType() == resource.GetType()
      && string.Equals(existing.ResourcePath, resource.ResourcePath, StringComparison.OrdinalIgnoreCase)));
    if(canonical is null)
    {
     canonical = resource;
     selected._savedStates[canonical] = pair.Value.DeepClone().AsObject();
     selected._lastStates[canonical] = resource.Serialize();
    }
    if(other._dirtyResources.Contains(resource) && !selected._dirtyResources.Contains(canonical))
    {
     var before = selected._savedStates[canonical].DeepClone().AsObject();
     var after = resource.Serialize();
     if(!ReferenceEquals(canonical, resource)) canonical.Deserialize(after);
     selected._lastStates[canonical] = after;
     selected._dirtyResources.Add(canonical);
     selected.InsertUndo("Edits from previous audio window", canonical, before, after);
    }
   }
   other.ReleaseLiveAudition();other._dirtyResources.Clear();other._closingAfterPrompt=true;other.Close();
  }
  foreach(var pair in IAssetEditor.OpenSingleEditors.Where(p=>p.Value is AudioEventEditorWindow w && (!w.IsValid || ReferenceEquals(w,selected))).ToArray())
   IAssetEditor.OpenSingleEditors.Remove(pair.Key);
  IAssetEditor.OpenMultiAssetEditors["audevt"]=selected;lastWindow=new(selected);
  selected.UpdateWindowTitle();selected.Show();selected.Raise();return selected;
 }
}
