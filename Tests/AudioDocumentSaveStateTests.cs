using System.Text.Json.Nodes;
public sealed partial class AudioEventTests
{
 [TestMethod] public void AudioDocumentSave_ProtectsNewerDiskAndRecognizesSavedAgentEdits()
 {
  var original=JsonNode.Parse("{\"Volume\":1,\"Name\":\"Lead\"}").AsObject();
  var draft=JsonNode.Parse("{\"Volume\":0.5,\"Name\":\"Lead\"}").AsObject();
  var newer=JsonNode.Parse("{\"Volume\":0.2,\"Name\":\"Lead\"}").AsObject();
  Assert.IsFalse(Editor.AudioDocumentSaveState.Conflicts(original,draft,original));
  Assert.IsFalse(Editor.AudioDocumentSaveState.Conflicts(original,draft,draft));
  Assert.IsTrue(Editor.AudioDocumentSaveState.Conflicts(original,draft,newer));
  Assert.IsTrue(Editor.AudioDocumentSaveState.Conflicts(original,original,newer));
  Assert.IsTrue(Editor.AudioDocumentSaveState.Matches(original,JsonNode.Parse("{\"Name\":\"Lead\",\"Volume\":1}").AsObject()));
 }

 [TestMethod] public void AudioDocumentSave_IgnoresOnlyTransientSoundReferenceIdentity()
 {
  JsonObject State(string id,string path)=>JsonNode.Parse("{\"Sources\":[{\"Id\":\"authored-source\",\"Tracks\":[{\"Sound\":{\"Id\":\""+id+"\",\"Path\":\""+path+"\"}}]}]}").AsObject();
  var original=State("native-a","audio/menu.vsnd");
  var refreshed=State("native-b","audio/menu.vsnd");
  Assert.IsTrue(Editor.AudioDocumentSaveState.Matches(original,refreshed));
  var saved=original.DeepClone().AsObject();saved["Sources"][0]["Tracks"][0]["Sound"]="audio/menu.vsnd";
  Assert.IsTrue(Editor.AudioDocumentSaveState.Matches(original,saved));
  Assert.IsFalse(Editor.AudioDocumentSaveState.Matches(original,State("native-a","audio/other.vsnd")));
  refreshed["Sources"][0]["Id"]="changed-source";
  Assert.IsFalse(Editor.AudioDocumentSaveState.Matches(original,refreshed));
  Assert.AreEqual("native-a",original["Sources"][0]["Tracks"][0]["Sound"]["Id"].GetValue<string>());
 }
}
