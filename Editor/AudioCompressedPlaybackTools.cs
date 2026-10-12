using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Audio;

namespace Editor.Mcp;

public static class AudioCompressedPlaybackTools
{
 sealed class SignalTap : AudioProcessor
 {
  int nonzeroBuffers;
  public int NonzeroBuffers => Volatile.Read(ref nonzeroBuffers);
  protected override void ProcessSingleChannel(AudioChannel channel, Span<float> input)
  {
   bool audible = false;
   foreach(var sample in input) audible |= float.IsFinite(sample) && Math.Abs(sample) > 0.000001f;
   if(audible) Interlocked.Increment(ref nonzeroBuffers);
   input.Clear();
  }
 }

 /// <summary>Measure native sound playback before silencing its isolated output. Does not decode to a managed PCM cache, change assets, or change game volume. preload waits for native LoadAsync first. A pass proves nonzero samples in this run only, not cold-start or reload reliability. Run before waveform/decode tools and check read_console for MP3 warnings.</summary>
 [McpTool("check_audio_compressed_playback")]
 public static async Task<object> Check(string sourcePath, bool preload = false, float seconds = 2)
 {
  if(!float.IsFinite(seconds) || seconds < .1f || seconds > 5) throw new ArgumentException("Use 0.1 to 5 seconds.");
  var sound = SoundFile.Load(sourcePath) ?? throw new ArgumentException("Sound source not found.");
  bool loadedBefore = sound.IsLoaded;
  bool readyBefore = sound.IsValidForPlayback;
  if(preload && !await sound.LoadAsync()) throw new InvalidOperationException("Native sound load failed.");
  var tap = new SignalTap();
  var mixer = Mixer.Master.AddChild();
  SoundHandle handle = null;
  try
  {
   mixer.Name = "Smod Compressed Playback Check";
   mixer.Volume = 0;
   mixer.AddProcessor(tap);
   handle = Sound.PlayFile(sound, 0);
   handle.TargetMixer = mixer;
   handle.ListenLocal = true;
   handle.SpacialBlend = 0;
   handle.ReverbEnabled = false;
   handle.Volume = 1;
   await Task.Delay(TimeSpan.FromSeconds(seconds));
   return new { Source = sourcePath, Preload = preload, LoadedBefore = loadedBefore, ReadyBefore = readyBefore,
    ReadyAfter = sound.IsValidForPlayback, NonzeroBuffers = tap.NonzeroBuffers,
    Passed = tap.NonzeroBuffers > 0, ColdStartVerified = false };
  }
  finally
  {
   handle?.Stop();
   mixer.ClearProcessors();
   mixer.Destroy();
  }
 }

 /// <summary>Stress the normal Smod event path using one existing source, with an initial seek and optional pause/resume. Runs up to 100 short trials, isolated and silent. Counts trials without output samples. Does not clear native caches or claim published-client proof. Read the console after the check.</summary>
 [McpTool("check_audio_compressed_event_stress")]
 public static async Task<object> Stress(string sourcePath, int trials = 30, bool pauseResume = false, float seekSeconds = 0)
 {
  if(trials < 1 || trials > 100 || !float.IsFinite(seekSeconds) || seekSeconds < 0 || seekSeconds > .1f)
   throw new ArgumentException("Use 1 to 100 trials and seek from 0 to 0.1 seconds.");
  var sound = SoundFile.Load(sourcePath) ?? throw new ArgumentException("Sound source not found.");
  var source = new Smod.AudioSourceDefinition();
  source.Tracks.Add(new(){Sound = sound});
  var graph = new Smod.AudioEvent{UI = true};
  graph.Sources.Add(source);
  graph.MasterTrack.Instruments.Add(new(){SourceId = source.Id, TrimStart = seekSeconds});
  Smod.AudioEventValidator.Validate(graph).AssertValid();
  var tap = new SignalTap();
  var mixer = Mixer.Master.AddChild();
  var failed = new System.Collections.Generic.List<int>();
  try
  {
   mixer.Name = "Smod Compressed Event Stress";
   mixer.Volume = 0;
   mixer.AddProcessor(tap);
   for(int trial = 0; trial < trials; trial++)
   {
    var instance = Smod.AudioSystem.GetEvent(graph).CreateInstance();
    instance.PlaybackBackend = new Smod.SboxAudioPlaybackBackend(()=>mixer);
    int before = tap.NonzeroBuffers;
    try
    {
     instance.Start();
     if(pauseResume) { instance.Paused = true; await Task.Delay(10); instance.Paused = false; }
     await Task.Delay(180);
     if(tap.NonzeroBuffers == before) failed.Add(trial);
    }
    finally { instance.Stop(Smod.AudioStopMode.Immediate); instance.Release(); }
    await Task.Delay(20);
   }
   return new { Source = sourcePath, Trials = trials, PauseResume = pauseResume, SeekSeconds = seekSeconds,
    SilentTrials = failed, NonzeroBuffers = tap.NonzeroBuffers, Passed = failed.Count == 0, ColdStartVerified = false };
  }
  finally { mixer.ClearProcessors(); mixer.Destroy(); }
 }

 /// <summary>Create one temporary networked audio diagnostic in the running host scene. New clients silently test up to 16 semicolon-separated source paths and write SMOD_CLIENT_PROBE results to their client log. Does not save the scene. Call remove_audio_client_probe after the test.</summary>
 [McpTool("start_audio_client_probe")]
 public static object ClientProbe(string sourcePaths, bool preload = false, bool directPlayback = false)
 {
  if(!Game.IsPlaying || !Networking.IsHost)throw new InvalidOperationException("Start an editor test host first.");
  if(string.IsNullOrWhiteSpace(sourcePaths) || sourcePaths.Length>4096)throw new ArgumentException("Provide sound paths, up to 4096 characters.");
  RemoveClientProbe();
  using var scope=Game.ActiveScene.Push();
  var go=new GameObject(false,"Smod temporary client audio probe");
  var probe=go.Components.Create<Smod.AudioClientPlaybackProbe>();
  probe.SourcePaths=sourcePaths;probe.Preload=preload;probe.DirectPlayback=directPlayback;
  go.Enabled=true;go.NetworkSpawn();
  return new{Id=go.Id,Temporary=true};
 }
 /// <summary>Remove temporary Smod client audio probes from the playing scene. Does not modify an editor scene or other objects.</summary>
 [McpTool("remove_audio_client_probe")]
 public static object RemoveClientProbe()
 {
  int removed=0;
  if(Game.IsPlaying && Networking.IsHost)
   foreach(var probe in Game.ActiveScene.GetAllComponents<Smod.AudioClientPlaybackProbe>().ToArray())
   {probe.GameObject.Destroy();removed++;}
  return new{Removed=removed};
 }

 /// <summary>Check that Smod native voice seeks still reposition, rewind, and resume an existing source. Silent temporary output; source must last at least half a second. Writes no audio or settings.</summary>
 [McpTool("check_audio_native_seek")]
 public static async Task<object> NativeSeek(string sourcePath)
 {
  var source=SoundFile.Load(sourcePath)??throw new ArgumentException("Sound not found.");
  if(!await source.LoadAsync() || source.Duration<.5f)throw new ArgumentException("Use a sound longer than half a second.");
  var tap=new SignalTap();var mixer=Mixer.Master.AddChild();SoundHandle handle=null;
  try
  {
   mixer.Volume=0;mixer.AddProcessor(tap);
   handle=Sound.PlayFile(source,0);handle.TargetMixer=mixer;handle.ListenLocal=true;handle.SpacialBlend=0;handle.Volume=1;
   var voice=new Smod.SboxAudioVoice(handle);
   await Task.Delay(180);voice.Paused=true;
   var progressed=voice.Time;
   voice.Time=.3f;var sought=voice.Time;
   voice.Time=0;var rewound=voice.Time;
   int before=tap.NonzeroBuffers;voice.Paused=false;await Task.Delay(100);
   var resumed=voice.Time;
   return new{Passed=progressed>.05f && Math.Abs(sought-.3f)<.002f && rewound==0 && resumed>.02f && resumed<.25f && tap.NonzeroBuffers>before,
    Progressed=progressed,Sought=sought,Rewound=rewound,Resumed=resumed};
  }
  finally{handle?.Stop();mixer.ClearProcessors();mixer.Destroy();}
 }
}
