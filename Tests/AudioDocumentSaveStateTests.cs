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
}
