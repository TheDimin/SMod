using System.Threading.Tasks;
namespace Smod;
/// <summary>Editor-only check of the production runtime owner; its output stays muted.</summary>
public static class AudioMusicRuntimeNativeCheck
{
 public sealed record ProcessResult(bool Started,bool SameBoundary,bool BothAdmitted,bool CancelledOwner,bool OtherOwnerActive,bool OnePhraseAfterBoundary,bool ClockContinued,bool MixerRemoved,long ResidentAfterStop,long FirstSample,long SecondSample,bool SeekHeld,bool StartupPositionVerified);
 /// <summary>Two retained process events submit existing source ranges to one muted song owner.</summary>
 public static async Task<ProcessResult> RunProcesses(AudioEvent resource,AudioBeatGrid grid)
 {
  var parameters=resource.Parameters.ToDictionary(p=>p,p=>p.Type==AudioParameterType.Labeled
   ?new AudioParameterValue(0,p.DefaultLabel,true):new AudioParameterValue(p.DefaultValue,null,false));
  var binding=new AudioMusicEventBacking(resource,grid,parameters);
  var part=binding.Parts.First();
  var range=AudioMusicSourcePreparation.ResolveRange(part.Instrument,part.Source);
  var phrase=new AudioInstrument{TrimStart=part.Instrument.TrimStart,TrimEnd=part.Instrument.TrimStart+(float)(grid.SecondsPerBeat*2)};
  if(range.Length<grid.SecondsPerBeat*2)throw new InvalidOperationException("The source must contain at least two beats.");
  var output=new SboxMusicOutput();var runtime=new AudioMusicRuntimeSession(output,maximumBytes:AudioMusicEventPlayback.DefaultPreparationBudgetBytes);
  using var owner=new AudioMusicEventPlayback(resource,()=>runtime){Volume=0,Paused=true};
  var processResource=new AudioEvent{UI=true,SustainUntilStopped=true};
  var first=AudioSystem.GetEvent(processResource).CreateInstance();var second=AudioSystem.GetEvent(processResource).CreateInstance();
  Guid? mixerId=null;bool started=false,same=false,admitted=false,cancelled=false,other=false,one=false,continued=false;
  long aSample=-1,bSample=-1;bool seekHeld=false,startupPositionVerified=false;
  try
  {
   first.Start();second.Start();
   started=await owner.StartAsync(grid,parameters,22050);mixerId=output.MixerId;
   startupPositionVerified=owner.Position==22050;
   owner.Paused=true;
   await Task.Delay(100);var pausedAt=owner.Position;await Task.Delay(100);
   if(owner.Position!=pausedAt)throw new InvalidOperationException("The shared clock did not pause.");
   var seek=owner.Seek(44100);
   await Task.WhenAny(seek,Task.Delay(2000));
   seekHeld=seek.IsCompletedSuccessfully&&seek.Result&&owner.Position==44100;
   if(!seekHeld)throw new InvalidOperationException("The paused shared seek did not publish its target sample.");
   var a=await owner.PrepareAndScheduleAsync(first,phrase,part.Source,null,null,1,.1f);
   var b=await owner.PrepareAndScheduleAsync(second,phrase,part.Source,null,null,1,.1f);
   if(a is null || b is null)throw new InvalidOperationException("The shared session rejected a process phrase.");
   aSample=a.Sample;bSample=b.Sample;same=a.Sample==b.Sample;
   var wait=System.Diagnostics.Stopwatch.StartNew();
   while((!a.Admission.IsCompleted || !b.Admission.IsCompleted)&&wait.Elapsed.TotalSeconds<2)await Task.Delay(10);
   admitted=a.Admission.IsCompletedSuccessfully&&a.Admission.Result&&b.Admission.IsCompletedSuccessfully&&b.Admission.Result;
   var firstOwner=first.MusicRequestOwner;var secondOwner=second.MusicRequestOwner;
   first.Stop(AudioStopMode.Immediate);cancelled=firstOwner.IsCancelled;other=!secondOwner.IsCancelled;
   owner.Paused=false;wait.Restart();
   while(owner.Position<b.Sample+1024&&wait.Elapsed.TotalSeconds<3)await Task.Delay(5);
   continued=owner.Position>=b.Sample+1024;one=continued&&runtime.ActivePhraseVoices==1;
  }
  finally
  {
   first.Stop(AudioStopMode.Immediate);first.Release();second.Stop(AudioStopMode.Immediate);second.Release();
   await owner.StopAsync();
  }
  var removed=mixerId.HasValue&&!Sandbox.Audio.Mixer.Default.GetChildren().Any(x=>x.Id==mixerId.Value);
  return new(started,same,admitted,cancelled,other,one,continued,removed,runtime.ResidentBytes,aSample,bSample,seekHeld,startupPositionVerified);
 }
 public sealed record EventResult(Result Playback,int Parts,long PreparedBytes,double PreparationMs,string[] Sources);
 public static async Task<EventResult> RunEvent(AudioEvent resource,AudioBeatGrid grid)
 {
  var parameters=resource.Parameters.ToDictionary(p=>p,p=>p.Type==AudioParameterType.Labeled
   ?new AudioParameterValue(0,p.DefaultLabel,true):new AudioParameterValue(p.DefaultValue,null,false));
  var binding=new AudioMusicEventBacking(resource,grid,parameters);
  var sources=binding.Parts.Select(part=>part.Source.Sound.ResourcePath).ToArray();
  var output=new SboxMusicOutput();var runtime=new AudioMusicRuntimeSession(output,maximumBytes:AudioMusicEventPlayback.DefaultPreparationBudgetBytes);
  using var owner=new AudioMusicEventPlayback(resource,()=>runtime){Volume=0};
  var timer=System.Diagnostics.Stopwatch.StartNew();
  var started=await owner.StartAsync(grid,parameters);var preparationMs=timer.Elapsed.TotalMilliseconds;
  var bytes=runtime.ResidentBytes;var id=output.MixerId;
  timer.Restart();while(owner.Position<4410 && timer.Elapsed.TotalSeconds<3)await Task.Delay(10);
  var advanced=owner.Position>=4410;
  owner.Paused=true;await Task.Delay(100);var at=owner.Position;await Task.Delay(100);
  var held=owner.Position==at;owner.Paused=false;await Task.Delay(100);var resumed=owner.Position>at;
  var carrierTime=output.CarrierTime;await owner.StopAsync();
  var removed=id.HasValue&&!Sandbox.Audio.Mixer.Default.GetChildren().Any(x=>x.Id==id.Value);
  return new(new(started,advanced,held,resumed,removed,runtime.ResidentBytes,Sandbox.Audio.Mixer.Default.Name,
   Sandbox.Audio.Mixer.Default.Mute,Sandbox.Audio.Mixer.Master.Mute,carrierTime),binding.Parts.Count,bytes,preparationMs,sources);
 }
 public sealed record Result(bool Started,bool ClockAdvancedWhileMuted,bool PauseHeld,bool Resumed,bool MixerRemoved,long ResidentAfterStop,string DefaultMixer,bool DefaultMuted,bool MasterMuted,float CarrierTime);
 public static async Task<Result> Run(AudioBeatGrid grid,AudioTrack master,AudioTrack track)
 {
  var output=new SboxMusicOutput();using var runtime=new AudioMusicRuntimeSession(output);
  runtime.Volume=0;
  var instrument=track.Instruments.Single();var source=instrument.Sound.Tracks.Single();
  var started=await runtime.StartAsync(grid,instrument,source,master.Tone,track.Tone,.1f);
  var id=output.MixerId;
  var timeout=System.Diagnostics.Stopwatch.StartNew();
  while(runtime.Position<4410 && timeout.Elapsed.TotalSeconds<3)await Task.Delay(10);
  var advanced=runtime.Position>=4410;
  runtime.Paused=true;await Task.Delay(100);var at=runtime.Position;await Task.Delay(100);
  var held=runtime.Position==at;
  runtime.Paused=false;await Task.Delay(100);var resumed=runtime.Position>at;
  var carrierTime=output.CarrierTime;
  await runtime.StopAsync();
  var removed=id.HasValue && !Sandbox.Audio.Mixer.Default.GetChildren().Any(x=>x.Id==id.Value);
  return new(started,advanced,held,resumed,removed,runtime.ResidentBytes,Sandbox.Audio.Mixer.Default.Name,Sandbox.Audio.Mixer.Default.Mute,Sandbox.Audio.Mixer.Master.Mute,carrierTime);
 }
}
