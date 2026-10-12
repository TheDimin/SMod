using Editor;
using Smod;
using System;
using System.Linq;
using System.Text.Json.Nodes;

public sealed partial class AudioEventTests
{
 static AudioElementPath ClipLink(AudioInstrument clip) => AudioElementPath.Parse(
  new AudioElementPath("element", "audio/test.audevt", "/unused", "AudioInstrument", "old", clip.Id, 99).Encode());
 [TestMethod] public void ShortElementLinkContainsOnlyEscapedFileAndGuid()
 {
  var id = Guid.NewGuid();
  var link = new AudioElementPath("element", "audio/caf\u00e9 & keys.audevt", "/Tracks/1", "AudioTrack", "old", id, 12);
  var text = link.Encode(); var parsed = AudioElementPath.Parse(text);
  Assert.IsFalse(text.Contains("revision")); Assert.IsFalse(text.Contains("Tracks"));
  Assert.IsTrue(text.EndsWith("#" + id.ToString("N")));
  Assert.AreEqual(link.Resource, parsed.Resource); Assert.AreEqual(id, parsed.Id);
 }
 [TestMethod] public void ClipLinkSurvivesNameTimingGainAndTrackChanges()
 {
  var clip = new AudioInstrument { Name = "Piano" }; var other = new AudioInstrument { Name = "Piano" };
  var e = new AudioEvent { Tracks = new() { new() { Instruments = new() { clip, other } }, new() } };
  var link = ClipLink(clip);
  clip.Name = "New name"; clip.StartTime = 40; e.Tracks[0].Volume = .3f;
  e.Tracks[0].Instruments.Remove(clip); e.Tracks[1].Instruments.Add(clip); e.Tracks.Reverse();
  var target = link.Resolve(AudioElementTarget.List(e), "different revision");
  Assert.AreSame(clip, target.Value); Assert.AreEqual("/Tracks/0/Instruments/0", target.Pointer);
 }
 [TestMethod] public void GuidLinkRejectsDeletedAndDuplicateTargetsWithoutIndexFallback()
 {
  var clip = new AudioInstrument(); var e = new AudioEvent { Tracks = new() { new() { Instruments = new() { clip } } } };
  var link = ClipLink(clip); e.Tracks[0].Instruments.Add(new() { Id = clip.Id });
  Assert.ThrowsException<InvalidOperationException>(() => link.Resolve(AudioElementTarget.List(e), ""));
  e.Tracks[0].Instruments.Clear(); e.Tracks[0].Instruments.Add(new());
  Assert.ThrowsException<InvalidOperationException>(() => link.Resolve(AudioElementTarget.List(e), ""));
 }
 [TestMethod] public void NewEditableElementsHaveDistinctIds()
 {
  var e = new AudioEvent { Tracks = new() { new() { Instruments = new() { new() { Automations = new() { new() { Points = new() { new(), new() } } } }, new() } } }, LoopRegions = new() { new() } };
  var elements = AudioElementTarget.List(e).Where(t => t.Pointer != "").ToArray();
  Assert.IsTrue(elements.All(t => t.Id != Guid.Empty));
  Assert.AreEqual(elements.Length, elements.Select(t => t.Id).Distinct().Count());
 }
 [TestMethod] public void LegacyMigrationIsRepeatablePreservesExistingIdsAndSurvivesReorder()
 {
  var state = JsonNode.Parse("""{"MasterTrack":{},"Tracks":[{"Instruments":[{"Name":"A","Automations":[{"Points":[{"Input":0}]}]},{"Name":"A"}]}],"LoopRegions":[{}]}""").AsObject();
  var reload = (JsonObject)state.DeepClone(); AudioEvent.UpgradeElementIds(state); AudioEvent.UpgradeElementIds(reload);
  Assert.IsTrue(JsonNode.DeepEquals(state, reload));
  var id = state["Tracks"][0]["Instruments"][0]["Id"].ToString();
  var instruments = state["Tracks"][0]["Instruments"].AsArray(); var moved = instruments[0]; instruments.RemoveAt(0); instruments.Add(moved);
  moved["Name"] = "Changed"; AudioEvent.UpgradeElementIds(state);
  Assert.AreEqual(id, instruments[1]["Id"].ToString());
  var saved = JsonNode.Parse(state.ToJsonString()).AsObject(); AudioEvent.UpgradeElementIds(saved);
  Assert.IsTrue(JsonNode.DeepEquals(state, saved));
 }
 [TestMethod] public void AutomationPointGuidResolvesAfterPointSort()
 {
  var point = new AudioAutomationPoint(); var automation = new AudioAutomation { Points = new() { point, new() } };
  var e = new AudioEvent { MasterTrack = new() { Automations = new() { automation } } };
  var link = AudioElementPath.Parse(new AudioElementPath("element", "test.audevt", "", "", "", point.Id, 0).Encode());
  automation.Points.Reverse(); point.Output = .5f;
  Assert.AreEqual("/MasterTrack/Automations/0/Points/1", link.Resolve(AudioElementTarget.List(e), "").Pointer);
 }
 [TestMethod] public void RootTimelineShortLinkRetainsTime()
 {
  var link = AudioElementPath.Parse(new AudioElementPath("element", "test.audevt", "", "AudioEvent", "", Guid.Empty, 42.5).Encode());
  var e = new AudioEvent(); Assert.AreSame(e, link.Resolve(AudioElementTarget.List(e), "new").Value); Assert.AreEqual(42.5, link.Time);
 }
 [TestMethod] public void InvalidShortLinksAreRejected()
 {
  foreach (var value in new[] { "smod:../test.audevt", "smod:E:/test.audevt", "smod:test.audevt#bad", "smod:test.audevt#t=NaN", "smod:test.audevt#t=-1", "smod:test.audevt#a#b", "smod:test.wav", "smod:%2e%2e/test.audevt" })
   Assert.ThrowsException<ArgumentException>(() => AudioElementPath.Parse(value));
 }
 [TestMethod] public void OldLongControlLinksRemainValid()
 {
  var marker = new TransitionMarker(); var e = new AudioEvent { TransitionMarkers = new() { marker } };
  var text = "smod://element/v1?resource=test.audevt&path=%2FTransitionMarkers%2F0&kind=TransitionMarker&revision=old&id=" + marker.Id + "&time=0";
  e.TransitionMarkers.Insert(0, new());
  Assert.AreEqual("/TransitionMarkers/1", AudioElementPath.Parse(text).Resolve(AudioElementTarget.List(e), "new").Pointer);
 }
 [TestMethod] public void LibraryShortLinksRoundTripWithoutFileAccess()
 {
  foreach (var source in new[] { "asset", "external", "package" })
  {
   var address = new AudioElementPath(source, "E:/source/music & keys.wav", "", "LibraryEntry", "", Guid.Empty, 0);
   var parsed = AudioElementPath.Parse(address.Encode());
   Assert.AreEqual(address.Resource, parsed.Resource); Assert.AreEqual(source, parsed.Source);
  }
 }
 [TestMethod] public void McpCopyGetsNewIdsAndDoesNotChangeOriginalIds()
 {
  var trackId=Guid.NewGuid(); var clipId=Guid.NewGuid(); var pointId=Guid.NewGuid();
  var source=new JsonObject { ["Tracks"]=new JsonArray(new JsonObject { ["Id"]=trackId.ToString(),["Instruments"]=new JsonArray(new JsonObject { ["Id"]=clipId.ToString(),["Automations"]=new JsonArray(new JsonObject { ["Id"]=Guid.NewGuid().ToString(),["Points"]=new JsonArray(new JsonObject { ["Id"]=pointId.ToString() }) }) }) }) };
  var edits=new JsonArray(new JsonObject { ["op"]="add",["path"]="/Tracks/-",["value"]=source["Tracks"][0].DeepClone() });
  var result=AudioAuthoringPatch.Apply(source,edits);
  Assert.AreEqual(trackId.ToString(),result["Tracks"][0]["Id"].ToString());
  Assert.AreNotEqual(trackId.ToString(),result["Tracks"][1]["Id"].ToString());
  Assert.AreNotEqual(clipId.ToString(),result["Tracks"][1]["Instruments"][0]["Id"].ToString());
  Assert.AreNotEqual(pointId.ToString(),result["Tracks"][1]["Instruments"][0]["Automations"][0]["Points"][0]["Id"].ToString());
  Assert.AreEqual(1,source["Tracks"].AsArray().Count);
 }
 [TestMethod] public void McpCannotEditPermanentIdDirectly()
 {
  var source=JsonNode.Parse("""{"Tracks":[{"Id":"00000000-0000-0000-0000-000000000001"}]}""").AsObject();
  Assert.ThrowsException<ArgumentException>(()=>AudioAuthoringPatch.Apply(source,new JsonArray(new JsonObject { ["path"]="/Tracks/0/Id",["value"]=Guid.NewGuid().ToString() })));
 }

}
