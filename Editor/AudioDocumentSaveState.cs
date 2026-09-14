using System.Text.Json.Nodes;
namespace Editor;
internal static class AudioDocumentSaveState
{
 internal static bool Matches(JsonObject a,JsonObject b)=>JsonNode.DeepEquals(a,b);
 internal static bool Conflicts(JsonObject saved,JsonObject current,JsonObject disk)
  => !Matches(current,disk) && !Matches(saved,disk);
}
