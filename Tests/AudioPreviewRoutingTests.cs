using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void PreviewBackendIsInheritedByChildrenWithoutChangingGameBackend()
 {
  var previewBackend=new FakeAudioBackend();
  var parent=AudioSystem.GetEvent(Nested(CreateEvent())).CreateInstance();
  parent.PlaybackBackend=previewBackend;
  var game=AudioSystem.GetEvent(CreateEvent()).CreateInstance();
  try
  {
   parent.Start();game.Start();
   Assert.AreEqual(1,previewBackend.Voices.Count);
   Assert.AreEqual(1,_backend.Voices.Count);
   parent.Stop(AudioStopMode.Immediate);
   Assert.IsTrue(_backend.Voices[0].IsPlaying);
  }
  finally{parent.Release();game.Stop(AudioStopMode.Immediate);game.Release();}
 }
}
