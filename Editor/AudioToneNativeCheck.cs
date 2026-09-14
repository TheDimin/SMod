namespace Smod;
/// <summary>Editor-only access to the production native voice effect route.</summary>
public sealed class AudioToneNativeCheck
{
 readonly SboxAudioVoice voice;
 public AudioToneNativeCheck(Sandbox.SoundHandle handle) => voice = new(handle);
 public void Set(float gainDb, float frequencyHz) => voice.SetTone(null, new AudioTone { BassGainDb = gainDb, BassFrequencyHz = frequencyHz });
}
