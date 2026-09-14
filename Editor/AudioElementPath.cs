using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Smod;

namespace Editor;

/// <summary>A read-only, versioned address. Copying an address never changes resource IDs or saves files.</summary>
internal sealed record AudioElementPath(string Source, string Resource, string Pointer, string Kind, string Revision, Guid Id, double Time)
{
 internal string Encode()
 {
  var resource = string.Join("/", Resource.Split('/').Select(Escape));
  if (Source != "element") return $"smod:{Source}:{resource}";
  if (Id != Guid.Empty) return $"smod:{resource}#{Id:N}";
  if (Pointer.Length != 0) throw new InvalidOperationException("The element needs a permanent ID before copying its link.");
  return "smod:" + resource + (Time == 0 ? "" : "#t=" + Time.ToString("R", CultureInfo.InvariantCulture));
 }
 private static string Escape(string value) => Uri.EscapeDataString(value ?? "");
 internal static AudioElementPath Parse(string text)
 {
  if (text is not null && text.Trim().StartsWith("smod:", StringComparison.Ordinal) && !text.Trim().StartsWith("smod://", StringComparison.Ordinal))
   return ParseShort(text.Trim());
  if (text is null || text.Length > 16384 || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "smod" || uri.AbsolutePath != "/v1" || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
   throw new ArgumentException("Invalid Smod link. Copy Get Path again.");
  if (uri.Host is not ("element" or "asset" or "external" or "package")) throw new ArgumentException("Unknown Smod link type.");
  var fields = new Dictionary<string,string>(StringComparer.Ordinal);
  foreach (var part in uri.Query.TrimStart('?').Split('&'))
  {
   var pair = part.Split('=', 2);
   if (pair.Length != 2 || !fields.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1]))) throw new ArgumentException("Invalid or duplicate link field.");
  }
  string Get(string key) => fields.TryGetValue(key, out var value) ? value : throw new ArgumentException("Missing link field: " + key);
  if (fields.Count != 6) throw new ArgumentException("Unknown link fields.");
  var result = new AudioElementPath(uri.Host, Get("resource"), Get("path"), Get("kind"), Get("revision"), Guid.Parse(Get("id")), double.Parse(Get("time"), CultureInfo.InvariantCulture));
  if (string.IsNullOrWhiteSpace(result.Resource) || result.Resource.Any(char.IsControl) || !double.IsFinite(result.Time) || result.Time < 0 || result.Time > float.MaxValue) throw new ArgumentException("Invalid resource or timeline time.");
  if (result.Source is "element" or "asset")
  {
   if (result.Resource.StartsWith('/') || result.Resource.Contains('\\') || result.Resource.Contains(':') || result.Resource.Split('/').Any(x => x is ".." or "." or "")) throw new ArgumentException("A project-relative resource path is required.");
  }
  if (result.Source == "element" && !result.Resource.EndsWith(".audevt", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("An Audio Event link is required.");
  return result;
 }
 private static AudioElementPath ParseShort(string text)
 {
  if (text.Length > 16384) throw new ArgumentException("Smod link is too long.");
  var body = text[5..];
  foreach (var source in new[] { "asset", "external", "package" })
   if (body.StartsWith(source + ":", StringComparison.Ordinal))
   {
    var address = Uri.UnescapeDataString(body[(source.Length + 1)..]);
    if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsControl)) throw new ArgumentException("Invalid library address.");
    return new(source, address, "", "LibraryEntry", "", Guid.Empty, 0);
   }
  var parts = body.Split('#');
  if (parts.Length > 2) throw new ArgumentException("Invalid element fragment.");
  var resource = Uri.UnescapeDataString(parts[0]);
  if (!resource.EndsWith(".audevt", StringComparison.OrdinalIgnoreCase) || resource.StartsWith('/') || resource.Contains('\\') || resource.Contains(':') || resource.Any(char.IsControl) || resource.Split('/').Any(x => x is ".." or "." or ""))
   throw new ArgumentException("A project-relative Audio Event path is required.");
  Guid id = Guid.Empty; double time = 0;
  if (parts.Length == 2)
  {
   if (parts[1].StartsWith("t=", StringComparison.Ordinal))
   {
    if (!double.TryParse(parts[1][2..], NumberStyles.Float, CultureInfo.InvariantCulture, out time) || !double.IsFinite(time) || time < 0 || time > float.MaxValue) throw new ArgumentException("Invalid timeline position.");
   }
   else if (!Guid.TryParse(parts[1], out id) || id == Guid.Empty) throw new ArgumentException("Invalid element GUID.");
  }
  return new("element", resource, "", id == Guid.Empty ? "AudioEvent" : "", "", id, time);
 }
 internal static string RevisionOf(JsonObject state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToJsonString())));
 internal static JsonNode Read(JsonObject state, string pointer)
 {
  JsonNode node = state;
  foreach (var part in pointer.Split('/').Skip(1))
   node = node is JsonArray array ? array[int.Parse(part, CultureInfo.InvariantCulture)] : node[part.Replace("~1", "/").Replace("~0", "~")];
  return node?.DeepClone();
 }
 internal AudioElementTarget Resolve(IReadOnlyList<AudioElementTarget> targets, string revision)
 {
  if (Source != "element") throw new ArgumentException("This link identifies a library entry, not a timeline element.");
  var matches = Id != Guid.Empty
   ? targets.Where(t => (Kind.Length == 0 || t.Kind == Kind) && t.Id == Id).ToArray()
   : targets.Where(t => t.Kind == Kind && t.Pointer == Pointer).ToArray();
  if (matches.Length != 1) throw new InvalidOperationException("The element is missing or ambiguous. Copy Get Path again.");
  if (Id == Guid.Empty && Pointer.Length != 0 && Revision != revision) throw new InvalidOperationException("Stale element link: the event changed. Copy Get Path again; no target was selected.");
  return matches[0];
 }
}

internal sealed record AudioElementTarget(object Value, string Pointer, string Kind, Guid Id, double Time)
{
 internal static List<AudioElementTarget> List(AudioEvent audioEvent)
 {
  var result = new List<AudioElementTarget>();
  void Add(object value, string path, double time = 0, Guid id = default)
  {
   if (value is null) return;
   id = value switch { AudioTrack track => track.Id, AudioInstrument instrument => instrument.Id,
    AudioAutomation automation => automation.Id, AudioAutomationPoint point => point.Id, _ => id };
   result.Add(new(value, path, value.GetType().Name, id, time));
  }
  void Automations(IEnumerable<AudioAutomation> values, string path, double time)
  {
   var index = 0;
   foreach (var value in values ?? Enumerable.Empty<AudioAutomation>())
   {
    var p = path + "/" + index++; Add(value, p, time);
    if (value?.Points is null) continue;
    for (var j = 0; j < value.Points.Count; j++) Add(value.Points[j], p + "/Points/" + j, time);
   }
  }
  void Track(AudioTrack track, string path)
  {
   Add(track, path); if (track is null) return;
   Automations(track.Automations, path + "/Automations", 0);
   if (track.Instruments is null) return;
   for (var i = 0; i < track.Instruments.Count; i++)
   {
    var instrument = track.Instruments[i]; var p = path + "/Instruments/" + i;
    if (instrument is null) continue;
    Add(instrument, p, instrument.StartTime);
    Automations(instrument.Automations, p + "/Automations", instrument.StartTime);
   }
  }
  Add(audioEvent, ""); Track(audioEvent.MasterTrack, "/MasterTrack");
  for (var i = 0; i < audioEvent.Tracks.Count; i++) Track(audioEvent.Tracks[i], "/Tracks/" + i);
  for (var i = 0; i < audioEvent.DestinationMarkers.Count; i++) { var x = audioEvent.DestinationMarkers[i]; Add(x, "/DestinationMarkers/" + i, x.Position.Seconds, x.Id); }
  for (var i = 0; i < audioEvent.TransitionMarkers.Count; i++) { var x = audioEvent.TransitionMarkers[i]; Add(x, "/TransitionMarkers/" + i, x.Position.Seconds, x.Id); }
  for (var i = 0; i < audioEvent.TransitionRegions.Count; i++) { var x = audioEvent.TransitionRegions[i]; Add(x, "/TransitionRegions/" + i, x.Start.Seconds, x.Id); }
  for (var i = 0; i < audioEvent.LoopRegions.Count; i++) { var x = audioEvent.LoopRegions[i]; Add(x, "/LoopRegions/" + i, x.Start.Seconds, x.Id); }
  return result;
 }
}
