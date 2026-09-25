using System.Text.Json.Nodes;
namespace Editor;
internal static class AudioDocumentSaveState
{
 // Native SoundFile references can receive a new in-memory Id on load. The asset path,
 // not that transient Id, is the authored identity. Graph element IDs remain significant.
 internal static JsonObject Canonical(JsonObject state)
 {
  if(state is null)return null;
  var copy=state.DeepClone().AsObject();
  if(copy["Sources"] is JsonArray sources)
   foreach(var source in sources)
    if(source?["Tracks"] is JsonArray tracks)
     foreach(var track in tracks)
      if(track?["Sound"] is JsonObject sound && sound.Count==2 && sound.ContainsKey("Id")
       && sound["Path"] is JsonValue value && value.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path))
       track["Sound"]=path;
  return copy;
 }
 internal static bool Matches(JsonObject a,JsonObject b)=>JsonNode.DeepEquals(Canonical(a),Canonical(b));
 internal static bool Conflicts(JsonObject saved,JsonObject current,JsonObject disk)
  => !Matches(current,disk) && !Matches(saved,disk);
}
