using Editor;
using System.Text.Json.Nodes;
public sealed partial class AudioEventTests
{
 [TestMethod] public void AuthoringPatchAddsChangesAndRemovesWithoutChangingSource()
 {
  var source=JsonNode.Parse("{\"Tracks\":[{\"Volume\":1}]}").AsObject();
  var result=AudioAuthoringPatch.Apply(source,JsonNode.Parse("[{\"path\":\"/Tracks/0/Volume\",\"value\":0.5},{\"op\":\"add\",\"path\":\"/Tracks/-\",\"value\":{\"Volume\":0.2}},{\"op\":\"remove\",\"path\":\"/Tracks/0\"}]").AsArray());
  Assert.AreEqual(1,source["Tracks"][0]["Volume"].GetValue<int>());Assert.AreEqual(.2,result["Tracks"][0]["Volume"].GetValue<double>(),.001);
 }
 [TestMethod] public void InvalidPatchDoesNotPartlyEditSource()
 {
  var source=JsonNode.Parse("{\"Volume\":1}").AsObject();
  Assert.ThrowsException<ArgumentException>(()=>AudioAuthoringPatch.Apply(source,JsonNode.Parse("[{\"path\":\"/Volume\",\"value\":0},{\"path\":\"/Unknown\",\"value\":1}]").AsArray()));
  Assert.AreEqual(1,source["Volume"].GetValue<int>());
 }

 [TestMethod] public void InspectorDoesNotCreateOptionalTrackObjectsDuringRead()
 {
  var track=new Smod.AudioTrack();
  Assert.IsFalse(Smod.Editor.AudioTimelineInspectorUx.ShowInGenericSheet(track,"Tone"));
  Assert.IsFalse(Smod.Editor.AudioTimelineInspectorUx.ShowInGenericSheet(track,"Overlay"));
  Assert.IsFalse(Smod.Editor.AudioTimelineInspectorUx.ShowInGenericSheet(new Smod.AudioEvent(),"MasterTrack"));
  Assert.IsNull(track.Tone);Assert.IsNull(track.Overlay);
 }
}
