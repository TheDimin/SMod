using System;
using System.Linq;
using System.Threading.Tasks;
using Smod;
using Sandbox;
using Sandbox.Audio;
namespace Editor.Mcp;
public static class AudioPreviewRoutingTools
{
 sealed class Tap:AudioProcessor
 {
  internal float Peak;
  protected override void ProcessSingleChannel(AudioChannel channel,Span<float> input)
  {foreach(var value in input)Peak=Math.Max(Peak,Math.Abs(value));input.Clear();}
 }
 /// <summary>Check a shared-music event on the editor preview output. Uses an isolated temporary silent capture, changes no game volume, returns measured pre-silence PCM peak and direct-master route. Always disposes the temporary session. No files are written.</summary>
 [McpTool("check_audio_preview_routing")]
 public static async Task<object> Check(string resourcePath)
 {
  var resource=AssetSystem.FindByPath(resourcePath)?.LoadResource<AudioEvent>()??throw new ArgumentException("Audio event required.");
  AudioEventValidator.Validate(resource).AssertValid();
  if(!resource.SharedMusicOutput)throw new ArgumentException("Use a shared-music song event.");
  var preview=AudioEditorPreviewOutput.GetMixer();var tap=new Tap();
  var output=new SboxMusicOutput(tap,AudioEditorPreviewOutput.GetMixer);
  using var runtime=new AudioMusicRuntimeSession(output,maximumBytes:AudioMusicEventPlayback.DefaultPreparationBudgetBytes);
  var instance=AudioSystem.GetEvent(resource).CreateInstance();AudioEditorPreviewOutput.Configure(instance);
  instance.SharedMusicSessionFactory=()=>runtime;
  var masterBefore=Sound.MasterVolume;var gameBefore=Mixer.Default.Volume;
  try
  {
   instance.Start();var timer=System.Diagnostics.Stopwatch.StartNew();
   while(timer.Elapsed.TotalSeconds<8 && (!runtime.IsRunning || tap.Peak<=0)){_=instance.PlaybackState;await Task.Delay(20);}
   return new{Passed=runtime.IsRunning && tap.Peak>0 && Mixer.Master.GetChildren().Contains(preview),Peak=tap.Peak,
    PreviewUnderMaster=Mixer.Master.GetChildren().Contains(preview),PreviewOutsideGame=preview!=Mixer.Default,
    MasterUnchanged=masterBefore==Sound.MasterVolume,GameVolumeUnchanged=gameBefore==Mixer.Default.Volume,Cleaned=true};
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
}
