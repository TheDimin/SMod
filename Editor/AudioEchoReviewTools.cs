using System;
using System.Threading.Tasks;
using Smod;
namespace Editor.Mcp;
public static class AudioEchoReviewTools
{
 sealed class Tap:Sandbox.Audio.AudioProcessor
 {
  internal float Peak;
  protected override void ProcessSingleChannel(Sandbox.Audio.AudioChannel channel,Span<float> input)
  {foreach(var value in input)Peak=Math.Max(Peak,Math.Abs(value));}
 }
 /// <summary>Check shared stem release echo with an existing source in a temporary muted mixer. Records only peak statistics after closing source input and after tail expiry. Writes no audio files. Returns cleanup status; does not alter the game or authored assets.</summary>
 [McpTool("check_audio_music_release_echo")]
 public static async Task<object> CheckShared(string sourcePath,float sourceOffset=0)
 {
  var sound=Sandbox.SoundFile.Load(sourcePath);if(sound is null||!await sound.LoadAsync()||!float.IsFinite(sourceOffset)||sourceOffset<0||sourceOffset+1>sound.Duration)throw new ArgumentException("Use one second of a loaded source.");
  using var bank=new AudioMusicSourceBank(64*1024*1024);
  var pcm=await bank.PrepareAsync(sound,sourceOffset,1) ?? throw new InvalidOperationException("Source exceeds the 64 MiB check budget.");
  var backing=new AudioMusicPcmBacking(new AudioMusicPcmBacking.Stem[]{new(pcm,1,Echo:new(){ReleaseOnly=true,DelaySeconds=.16f,WetGain=.5f,Feedback=.45f,TailSeconds=1.2f})});
  var renderer=new AudioMusicPcmRenderer(1,backing:backing);var processor=new AudioMusicPcmProcessor(renderer);
  var mixer=Sandbox.Audio.Mixer.Default.AddChild();mixer.Volume=0;var tap=new Tap();
  Sandbox.SoundHandle carrier=null;float tail=0,ended=0;
  try
  {
   mixer.AddProcessor(processor);mixer.AddProcessor(tap);
   var clock=Sandbox.SoundFile.FromPcm("smod/runtime/release_echo_check.vsnd",new byte[4096],new Sandbox.SoundFile.PcmOptions{Channels=2,Rate=44100,Bits=16,Loop=true});
   carrier=Sandbox.Sound.PlayFile(clock,1);carrier.TargetMixer=mixer;carrier.ListenLocal=true;carrier.SpacialBlend=0;carrier.ReverbEnabled=false;
   await Task.Delay(500);backing.SetGains(new[]{0f},new[]{true});await Task.Delay(100);tap.Peak=0;
   await Task.Delay(350);tail=tap.Peak;await Task.Delay(1000);tap.Peak=0;await Task.Delay(100);ended=tap.Peak;
  }
  finally{carrier?.Stop();backing.Cancel();mixer.ClearProcessors();mixer.Destroy();}
  return new{Passed=tail>1e-6f&&ended<1e-7f,TailPeak=tail,AfterTailPeak=ended,SilenceThreshold=1e-7f,Cleaned=true};
 }
 /// <summary>Check a muted native echo route with an existing sound. Stops the source after 0.6 seconds, measures its echo, and checks route cleanup. Writes no files or player settings. sourceOffset must leave at least one second of source.</summary>
 [McpTool("check_audio_echo")]
 public static async Task<object> Check(string sourcePath,float sourceOffset=0)
 {
  var sound=Sandbox.SoundFile.Load(sourcePath);if(sound is null||!await sound.LoadAsync()||!float.IsFinite(sourceOffset)||sourceOffset<0||sourceOffset+1>sound.Duration)throw new ArgumentException("Use an existing source with at least one second after the offset.");
  var handle=Sandbox.Sound.PlayFile(sound,0);handle.ListenLocal=true;handle.SpacialBlend=0;handle.Time=sourceOffset;
  var voice=new SboxAudioVoice(handle);var tap=new Tap();bool tail=false,clean=false;float peak=0;
  try
  {
   voice.SetEcho(new(){DelaySeconds=.22f,WetGain=.28f,Feedback=.45f,TailSeconds=1.4f});
   var mixer=voice.EffectMixerForCheck;mixer.Volume=0;mixer.AddProcessor(tap);handle.Volume=1;
   await Task.Delay(600);voice.Stop(0);await Task.Delay(100);tap.Peak=0;await Task.Delay(400);
   peak=tap.Peak;tail=peak>1e-6f;
   await Task.Delay(1300);clean=voice.EffectMixerForCheck is null;
  }
  finally{voice.CancelEcho();voice.Stop(0);}
  return new{Passed=tail&&clean,TailAfterSourceStop=tail,NativeTailPeak=peak,RouteDisposed=clean};
 }
}
