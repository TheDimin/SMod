using System.Threading;
using System.Threading.Tasks;
using Sandbox.Audio;
namespace Smod;

/// <summary>Opt-in client diagnostic. Only runs on a temporary component created by the audio MCP probe.</summary>
[Hide]
public sealed class AudioClientPlaybackProbe : Component
{
 [Property] public bool DirectPlayback { get; set; }
 [Property] public bool Preload { get; set; }
 [Property] public string SourcePaths { get; set; } = "";
 readonly CancellationTokenSource cancellation = new();
 sealed class Tap : AudioProcessor
 {
  int buffers;
  public int Buffers => Interlocked.CompareExchange(ref buffers,0,0);
  protected override void ProcessSingleChannel(AudioChannel channel, Span<float> input)
  {
   bool nonzero=false;
   foreach(var sample in input)nonzero |= float.IsFinite(sample) && Math.Abs(sample)>0.000001f;
   if(nonzero)Interlocked.Increment(ref buffers);
   input.Clear();
  }
 }
 protected override void OnStart()
 {
  if(Application.IsEditor || Application.IsHeadless || string.IsNullOrWhiteSpace(SourcePaths))return;
  _=Run();
 }
 protected override void OnDestroy() => cancellation.Cancel();
 async Task Run()
 {
  try
  {
   Log.Info("SMOD_CLIENT_PROBE begin");
   var paths=SourcePaths.Split(';',StringSplitOptions.RemoveEmptyEntries).Take(16).ToArray();
   foreach(var path in paths)
   {
    cancellation.Token.ThrowIfCancellationRequested();
    var sound=SoundFile.Load(path);
    if(sound is null){Log.Warning($"SMOD_CLIENT_PROBE missing {path}");continue;}
    bool loaded=sound.IsLoaded,ready=sound.IsValidForPlayback;
    if(Preload && !await sound.LoadAsync())throw new InvalidOperationException("Probe preload failed: "+path);
    var source=new AudioSourceDefinition();source.Tracks.Add(new(){Sound=sound});
    var graph=new AudioEvent{UI=true};graph.Sources.Add(source);
    graph.MasterTrack.Instruments.Add(new(){SourceId=source.Id});
    AudioEventValidator.Validate(graph).AssertValid();
    var tap=new Tap();var mixer=Mixer.Master.AddChild();
    var instance=AudioSystem.GetEvent(graph).CreateInstance();
    SoundHandle direct=null;
    try
    {
     mixer.Name="Smod client playback probe";mixer.Volume=0;mixer.AddProcessor(tap);
     instance.PlaybackBackend=new SboxAudioPlaybackBackend(()=>mixer);
     if(DirectPlayback)
     {direct=Sound.PlayFile(sound,0);direct.TargetMixer=mixer;direct.ListenLocal=true;direct.SpacialBlend=0;direct.ReverbEnabled=false;direct.Volume=1;}
     else instance.Start();
     await Task.Delay(1000,cancellation.Token);
     Log.Info($"SMOD_CLIENT_PROBE source={path} direct={DirectPlayback} preload={Preload} loadedBefore={loaded} readyBefore={ready} buffers={tap.Buffers} passed={tap.Buffers>0}");
    }
    finally{direct?.Stop();instance.Stop(AudioStopMode.Immediate);instance.Release();mixer.ClearProcessors();mixer.Destroy();}
   }
   Log.Info("SMOD_CLIENT_PROBE complete");
  }
  catch(OperationCanceledException) { }
  catch(Exception error){Log.Warning($"SMOD_CLIENT_PROBE failed: {error}");}
 }
}

