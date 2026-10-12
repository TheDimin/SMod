using Smod;
using Sandbox.Audio;
namespace Editor;

/// <summary>Editor audition is a sibling of game/music buses. Global master still applies.</summary>
internal static class AudioEditorPreviewOutput
{
 static Mixer mixer;
 internal static Mixer GetMixer()
 {
  if(mixer is not null && Mixer.FindMixerByGuid(mixer.Id)==mixer)return mixer;
  mixer=Mixer.Master.AddChild();mixer.Name="Smod Editor Preview";
  mixer.Volume=1; mixer.Spatializing=0; mixer.DistanceAttenuation=0;
  mixer.Occlusion=0; mixer.Reverb=0; mixer.AirAbsorption=0;
  return mixer;
 }
 internal static void Configure(AudioEventInstance instance)
 {
  instance.PlaybackBackend=new SboxAudioPlaybackBackend(GetMixer);
  instance.SharedMusicSessionFactory=()=>new AudioMusicRuntimeSession(
   new SboxMusicOutput(outputParent:GetMixer),maximumBytes:AudioMusicEventPlayback.DefaultPreparationBudgetBytes);
 }
}
