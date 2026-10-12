using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Smod;

public sealed partial class AudioEvent
{
 public override int ResourceVersion => 1;

 /// <summary>Old assets get repeatable IDs before loading. The next normal save stores them.
 /// IDs are scoped to a resource. New objects use random IDs; edits and undo keep saved IDs.</summary>
 [JsonUpgrader(typeof(AudioEvent), 1)]
 public static void UpgradeElementIds(JsonObject state)
 {
  void Add(JsonObject node, string pointer)
  {
   if (node is null) return;
   if (!Guid.TryParse(node["Id"]?.ToString(), out var id) || id == Guid.Empty)
    node["Id"] = LegacyElementId(pointer).ToString();
  }
  void Automations(JsonArray values, string pointer)
  {
   if (values is null) return;
   for (var i = 0; i < values.Count; i++)
   {
    if (values[i] is not JsonObject value) continue;
    var p = pointer + "/" + i; Add(value, p);
    if (value["Points"] is not JsonArray points) continue;
    for (var j = 0; j < points.Count; j++) Add(points[j] as JsonObject, p + "/Points/" + j);
   }
  }
  void Track(JsonObject value, string pointer)
  {
   if (value is null) return;
   Add(value, pointer); Automations(value["Automations"] as JsonArray, pointer + "/Automations");
   if (value["Instruments"] is not JsonArray instruments) return;
   for (var i = 0; i < instruments.Count; i++)
   {
    if (instruments[i] is not JsonObject instrument) continue;
    var p = pointer + "/Instruments/" + i; Add(instrument, p);
    Automations(instrument["Automations"] as JsonArray, p + "/Automations");
   }
  }
  Track(state["MasterTrack"] as JsonObject, "/MasterTrack");
  if (state["Tracks"] is JsonArray tracks)
   for (var i = 0; i < tracks.Count; i++) Track(tracks[i] as JsonObject, "/Tracks/" + i);
  foreach (var name in new[] { "DestinationMarkers", "TransitionMarkers", "TransitionRegions", "LoopRegions" })
   if (state[name] is JsonArray controls)
    for (var i = 0; i < controls.Count; i++) Add(controls[i] as JsonObject, "/" + name + "/" + i);
 }
 internal static Guid LegacyElementId(string pointer)
  => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("Smod.AudioEvent.Element.v1:" + pointer)).AsSpan(0, 16));
}
