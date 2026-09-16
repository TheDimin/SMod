using System;
using System.Linq;

namespace Editor;

public sealed partial class AudioEventEditorWindow
{
 internal void AddGetPath(ContextMenu menu, object value, double? time = null)
 {
  menu.AddOption("Get Path", "link", () =>
  {
   var target = AudioElementTarget.List(_event).Single(t => ReferenceEquals(t.Value, value));
   var link = new AudioElementPath("element", _asset.RelativePath, target.Pointer, target.Kind,
    AudioElementPath.RevisionOf(_event.Serialize()), target.Id, time ?? target.Time);
   EditorUtility.Clipboard.Copy(link.Encode());
  });
 }
 internal static void AddLibraryGetPath(ContextMenu menu, string source, string resource)
 {
  menu.AddOption("Get Path", "link", () => EditorUtility.Clipboard.Copy(
   new AudioElementPath(source, resource, "", "LibraryEntry", "", Guid.Empty, 0).Encode()));
 }
 internal object ResolveElementPath(AudioElementPath link, bool focus)
 {
  var state = _event.Serialize();
  var stateRevision = AudioElementPath.RevisionOf(state);
  var target = link.Resolve(AudioElementTarget.List(_event), stateRevision);
  var time = target.Pointer.Length == 0 || target.Value is Smod.AudioTrack ? link.Time : target.Time;
  if (focus)
  {
   Show(); Raise(); Focus(); SelectObject(target.Value);
   _workspace.FocusElement(target.Value, time);
  }
  return new { ResourcePath = _asset.RelativePath, target.Pointer, target.Kind, target.Id,
   Time = time, Focused = focus, StateRevision = stateRevision,
   Revision = AuthoringRevision(), Dirty = _dirtyResources.Contains(_event),
   Description = target.Value is Smod.AudioInstrument instrument ? AudioTimelineView.InstrumentDescription(instrument,_event) : null,
   Element = AudioElementPath.Read(state, target.Pointer) };
 }
}
