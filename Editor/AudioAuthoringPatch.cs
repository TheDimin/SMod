using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Nodes;
namespace Editor;

/// <summary>Bounded JSON-pointer edits on a detached serialized event, before editor mutation.</summary>
internal static class AudioAuthoringPatch
{
 internal static JsonObject Apply(JsonObject source,JsonArray edits)
 {
  if(edits.Count is <1 or >128)throw new ArgumentException("Provide 1 to 128 edits.");
  var result=(JsonObject)source.DeepClone();
  foreach(var edit in edits)
  {
   var path=edit?["path"]?.GetValue<string>() ?? "";
   if(!path.StartsWith('/') || path=="/")throw new ArgumentException("Use a property JSON pointer, such as /Tracks/0/Volume.");
   var parts=path[1..].Split('/');
   if(parts.Any(part=>Decode(part)=="Id"))throw new ArgumentException("Element IDs are permanent and cannot be edited.");
   JsonNode parent=result;
   for(var i=0;i<parts.Length-1;i++)parent=Child(parent,Decode(parts[i]));
   var key=Decode(parts[^1]);var op=edit["op"]?.GetValue<string>() ?? "replace";
   var value=edit["value"]?.DeepClone();
   if(parent is JsonObject obj)
   {
    if(!obj.ContainsKey(key))throw new ArgumentException("Unknown property: "+path);
    if(op=="remove")obj.Remove(key);
    else if(op=="replace")obj[key]=value;
    else throw new ArgumentException("Object properties support replace or remove.");
   }
   else if(parent is JsonArray array)
   {
    var index=key=="-" ? array.Count : int.Parse(key);
    if(op=="add")
    {
     // A copied authoring subtree is a new element, not a second address for the original.
     if(parts.Length>=2 && new[]{"Tracks","Instruments","Automations","Points","DestinationMarkers","TransitionMarkers","TransitionRegions","LoopRegions"}.Contains(Decode(parts[^2])))
      RenewIds(value);
     array.Insert(index,value);
    }
    else if(op=="remove")array.RemoveAt(index);
    else if(op=="replace")array[index]=value;
    else throw new ArgumentException("Unknown edit operation.");
   }
   else throw new ArgumentException("Edit parent is not an object or array.");
  }
  return result;
 }
 private static void RenewIds(JsonNode node)
 {
  var replacements=new Dictionary<Guid,Guid>();
  void Walk(JsonNode value,bool rewriteReferences)
  {
   if(value is JsonObject obj)
   {
    foreach(var key in obj.Select(pair=>pair.Key).ToArray())
    {
     if(key==(rewriteReferences?"DestinationMarkerId":"Id") && Guid.TryParse(obj[key]?.ToString(),out var old))
     {
      if(rewriteReferences){if(replacements.TryGetValue(old,out var mapped))obj[key]=mapped.ToString();}
      else {var id=Guid.NewGuid();replacements[old]=id;obj[key]=id.ToString();}
     }
     else Walk(obj[key],rewriteReferences);
    }
   }
   else if(value is JsonArray array)foreach(var child in array)Walk(child,rewriteReferences);
  }
  Walk(node,false);Walk(node,true);
 }
 private static string Decode(string key)=>key.Replace("~1","/").Replace("~0","~");
 private static JsonNode Child(JsonNode node,string key)=>node is JsonArray a ? a[int.Parse(key)] : node[key] ?? throw new ArgumentException("Missing edit path: "+key);
}
