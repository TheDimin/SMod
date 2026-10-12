using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sandbox.Audio;

namespace Smod;

/// <summary>Capture only one temporary native output in memory. The final tap silences it.</summary>
public static partial class AudioMusicOutputNativeCheck
{
 sealed class Tap : AudioProcessor<Tap.CaptureState>
 {
  public sealed class CaptureState : ListenerState { }
  readonly float[][] channels={new float[44100*8],new float[44100*8]};
  readonly long[] counts=new long[2];
  CaptureState listener;
  public bool Invalid {get;private set;}
  protected override void ProcessSingleChannel(AudioChannel channel,Span<float> input)
  {
   listener??=CurrentState;
   var c=channel.Get();
   if(c>1){input.Clear();return;}
   if(!ReferenceEquals(listener,CurrentState) || c<0){Invalid=true;input.Clear();return;}
   var at=counts[c];
   if(at+input.Length>channels[c].Length)Invalid=true;
   else input.CopyTo(channels[c].AsSpan((int)at,input.Length));
   Interlocked.Add(ref counts[c],input.Length);input.Clear();
  }
  public long Frames=>Interlocked.Read(ref counts[1]);
  public float[] ReadAfterDetach()
  {
   if(Invalid || counts[0]!=counts[1] || counts[0]>channels[0].Length)throw new InvalidOperationException($"Invalid stereo capture: invalid={Invalid}, left={counts[0]}, right={counts[1]}, capacity={channels[0].Length}.");
   var result=new float[(int)counts[0]*2];
   for(var i=0;i<counts[0];i++){result[i*2]=channels[0][i];result[i*2+1]=channels[1][i];}
   return result;
  }
 }

 public sealed record EventResult(bool Running,int SharedParts,int VisibleParts,int PreparedRanges,long ResidentWhilePlaying,bool ActionExpected,bool ActionPlaying,bool ActionOnBeat,bool PauseHeld,bool SeekApplied,bool Faded,
  double Peak,bool MixerRemoved,long ResidentAfterStop,string Error,bool Passed,double? ActionScheduledDelayMs,double? ActionObservedDelayMs,string ActionReason);
 public static async Task<EventResult> RunEvent(AudioEvent resource,float worldState=0,bool activeCoffee=false,float actionDelaySeconds=.1f,float intensity=0,float measureSeconds=0)
 {
  if(!float.IsFinite(actionDelaySeconds)||actionDelaySeconds<0||actionDelaySeconds>.5f)throw new ArgumentOutOfRangeException(nameof(actionDelaySeconds));
  if(!float.IsFinite(intensity)||intensity<0||intensity>1||!float.IsFinite(measureSeconds)||measureSeconds<0||measureSeconds>10)throw new ArgumentOutOfRangeException("Use intensity 0-1 and measureSeconds 0-10.");
  var tap=new Tap();var output=new SboxMusicOutput(tap);
  var runtime=new AudioMusicRuntimeSession(output,maximumBytes:AudioMusicEventPlayback.DefaultPreparationBudgetBytes);
  var instance=AudioSystem.GetEvent(resource).CreateInstance();instance.SharedMusicSessionFactory=()=>runtime;
  if(resource.Parameters.Any(p=>p.Name=="WorldState"))instance.SetParameterByName("WorldState",worldState);
  if(activeCoffee)instance.SetParameterByName("ActiveCoffeeCount",1);
  if(resource.Parameters.Any(p=>p.Name=="Intensity"))instance.SetParameterByName("Intensity",intensity);
  double? scheduledDelay=null,observedDelay=null;string actionReason=null;
  var values=resource.Parameters.ToDictionary(p=>p,p=>p.Type==AudioParameterType.Labeled?new AudioParameterValue(0,p.DefaultLabel,true):new AudioParameterValue(p.Name=="WorldState"?worldState:p.Name=="Intensity"?intensity:p.DefaultValue,null,false));
  var actionTrack=resource.Tracks.FirstOrDefault(t=>t.Overlay?.Enabled==true && t.Overlay.TriggerParameter?.Name=="InteractionCount"
   && AudioTimelineControlEvaluator.Matches(t.Overlay.Conditions,values));
  bool expected=actionTrack is not null;
  bool running=false,held=false,sought=false,faded=false,action=false,onBeat=false;int parts=0,visible=0,ranges=0;long resident=0;Guid? id=null;float[] captured;
  try
  {
   instance.Start();var timeout=System.Diagnostics.Stopwatch.StartNew();
   while(instance.PlaybackState==AudioPlaybackState.Starting && timeout.Elapsed.TotalSeconds<30)await Task.Delay(10);
   running=instance.PlaybackState==AudioPlaybackState.Playing;id=output.MixerId;
   if(!running)throw new InvalidOperationException(instance.SharedMusicError??"Shared event did not start within 30 seconds.");
   await Task.Delay((int)Math.Round(actionDelaySeconds*1000));parts=instance.InstrumentPlaybackStates.Count;visible=instance.CaptureLiveSnapshot()?.Voices.Count(v=>v.State=="Playing"||v.State=="Silent")??0;
   if(expected)
   {
    var requestPosition=runtime.Position;timeout.Restart();instance.SetParameterByName("InteractionCount",1);
    while(timeout.Elapsed.TotalSeconds<3)
    {
     var states=instance.InstrumentPlaybackStates;
     action=states.Any(v=>ReferenceEquals(v.Track,actionTrack) && v.IsPlaying);
     if(action)break;
     await Task.Delay(10);
    }
    var row=instance.CaptureLiveSnapshot()?.Voices.FirstOrDefault(v=>v.Track==actionTrack.Name && v.State is "Playing" or "Silent");
    if(row is not null){scheduledDelay=(row.Start-requestPosition/44100d)*1000;observedDelay=timeout.Elapsed.TotalMilliseconds;actionReason=row.Reason;}
    var beats=row is null?-1:row.Start/resource.BeatGrids[0].SecondsPerBeat;
    var quantum=actionTrack.Overlay.QuantizationBeats>0?actionTrack.Overlay.QuantizationBeats:resource.BeatGrids[0].BeatsPerBar;
    onBeat=beats>=0 && Math.Abs(beats/quantum-Math.Round(beats/quantum))<=1d/(44100*resource.BeatGrids[0].SecondsPerBeat*quantum);
    // Include the complete short response in the in-memory level measurement.
    timeout.Restart();
    while(row is not null && instance.TimelinePosition<row.End && timeout.Elapsed.TotalSeconds<2)await Task.Delay(10);
   }
   parts=instance.InstrumentPlaybackStates.Count;visible=instance.CaptureLiveSnapshot()?.Voices.Count(v=>v.State=="Playing"||v.State=="Silent")??0;
   if(measureSeconds>0)await Task.Delay((int)Math.Round(measureSeconds*1000));
   ranges=runtime.PreparedRangeCount;resident=runtime.ResidentBytes;
   instance.Paused=true;await Task.Delay(60);var paused=instance.TimelinePosition;await Task.Delay(80);held=instance.TimelinePosition==paused;
   instance.TimelinePosition=1;await Task.Delay(60);sought=Math.Abs(instance.TimelinePosition-1)<.001;
   instance.Paused=false;await Task.Delay(120);instance.Stop(AudioStopMode.AllowFadeout);
   timeout.Restart();while(instance.PlaybackState==AudioPlaybackState.Stopping && timeout.Elapsed.TotalSeconds<2)await Task.Delay(10);
   faded=instance.PlaybackState==AudioPlaybackState.Stopped;await Task.Delay(50);
  }
  finally {if(instance.PlaybackState!=AudioPlaybackState.Stopped)instance.Stop(AudioStopMode.Immediate);runtime.Dispose();instance.Release();}
  captured=tap.ReadAfterDetach();var peak=captured.Select(v=>Math.Abs((double)v)).DefaultIfEmpty().Max();
  var removed=id.HasValue&&!Mixer.Default.GetChildren().Any(m=>m.Id==id.Value);
  return new(running,parts,visible,ranges,resident,expected,action,onBeat,held,sought,faded,peak,removed,runtime.ResidentBytes,instance.SharedMusicError,
   running&&parts>0&&visible>0&&visible<=parts&&(!expected||action&&onBeat)&&held&&sought&&faded&&peak>.0001&&removed&&runtime.ResidentBytes==0&&instance.SharedMusicError is null,scheduledDelay,observedDelay,actionReason);
 }
 public sealed record FadeResult(int RequestedFrames,long CapturedFadeFrames,double PeakBeforeFade,double FinalSample,
  bool MixerRemoved,long ResidentAfterStop,bool Passed);
 public static async Task<FadeResult> RunFade(AudioEvent resource,AudioBeatGrid grid,bool stopCarrier=false)
 {
  var tap=new Tap();var output=new SboxMusicOutput(tap);
  var runtime=new AudioMusicRuntimeSession(output,maximumBytes:AudioMusicEventPlayback.DefaultPreparationBudgetBytes);
  using var owner=new AudioMusicEventPlayback(resource,()=>runtime);
  var parameters=resource.Parameters.ToDictionary(p=>p,p=>p.Type==AudioParameterType.Labeled
   ?new AudioParameterValue(0,p.DefaultLabel,true):new AudioParameterValue(p.DefaultValue,null,false));
  Guid? id=null;long before=0;float[] captured;
  const int requested=22050;
  try
  {
   if(!await owner.StartAsync(grid,parameters))throw new InvalidOperationException("Shared fade source did not start.");
   id=output.MixerId;
   await Task.Delay(200);before=tap.Frames;
   var stopping=owner.StopAsync(.5f);
   if(stopCarrier)output.StopCarrierForCheck();
   await stopping;await Task.Delay(50);captured=tap.ReadAfterDetach();
  }
  finally {runtime.Dispose();}
  var elapsed=captured.Length/2-before;
  var peak=captured.Take((int)Math.Min(before*2,captured.Length)).Select(v=>Math.Abs((double)v)).DefaultIfEmpty().Max();
  var tail=captured.TakeLast(2).Select(v=>Math.Abs((double)v)).DefaultIfEmpty().Max();
  var removed=id.HasValue&&!Mixer.Default.GetChildren().Any(m=>m.Id==id.Value);
  return new(requested,elapsed,peak,tail,removed,runtime.ResidentBytes,
   (stopCarrier?elapsed<requested:elapsed>=requested&&elapsed<=requested+8192&&tail<.000001)&&peak>.0001&&removed&&runtime.ResidentBytes==0);
 }
 public sealed record PhraseResult(long ScheduledSample,long CaptureSample,int Frames,double MaximumSampleError,double ExpectedPeak,bool SamplesMatch);
 public sealed record Result(bool Started,bool ChannelSequenceValid,bool ClockMatchesCapture,PhraseResult[] Phrases,
  long CapturedFrames,long ClockFrames,long PausedFrames,bool MixerRemoved,long ResidentAfterStop,
  int RequestedStallMs,double MeasuredStallMs,bool StallCrossedFirstBoundary,int RequestedUpdateRate,double MaximumUpdateIntervalMs,bool Passed);
 public static async Task<Result> Run(AudioEvent resource,AudioBeatGrid grid,bool stallMainThread=false,int updateRate=0)
 {
  if(updateRate is not (0 or 30 or 60 or 120))throw new ArgumentOutOfRangeException(nameof(updateRate));
  var updateDelay=updateRate==0?5:Math.Max(1,(int)Math.Round(1000d/updateRate));
  double maximumUpdateInterval=0;
  var invocationThread=Thread.CurrentThread.ManagedThreadId;
  double stallMs=0;bool crossed=false;
  var parameters=resource.Parameters.ToDictionary(p=>p,p=>p.Type==AudioParameterType.Labeled
   ?new AudioParameterValue(0,p.DefaultLabel,true):new AudioParameterValue(p.DefaultValue,null,false));
  var part=new AudioMusicEventBacking(resource,grid,parameters).Parts.First();
  var phrase=new AudioInstrument {TrimStart=part.Instrument.TrimStart,TrimEnd=part.Instrument.TrimStart+(float)grid.SecondsPerBeat};
  var tap=new Tap();var output=new SboxMusicOutput(tap);
  var runtime=new AudioMusicRuntimeSession(output,maximumBytes:AudioMusicEventPlayback.DefaultPreparationBudgetBytes);
  using var owner=new AudioMusicEventPlayback(resource,()=>runtime);
  owner.UpdateMix(new AudioMusicMixState(0,resource.Tracks.Prepend(resource.MasterTrack).ToDictionary(t=>t,t=>0f)));
  var process=AudioSystem.GetEvent(new AudioEvent{UI=true,SustainUntilStopped=true}).CreateInstance();
  var scheduled=new System.Collections.Generic.List<long>();
  short[] pcm=null;float[] captured=null;bool started=false,valid=false;long clock=0,pausedFrames=0;Guid? mixerId=null;
  try
  {
   process.Start();
   started=await owner.StartAsync(grid,parameters);mixerId=output.MixerId;
   if(!started)throw new InvalidOperationException("Shared owner did not start.");
   pcm=await runtime.PreparePhraseAsync(phrase,part.Source,null,null);
   if(pcm is null)throw new InvalidOperationException("Phrase preparation failed.");
   for(var i=0;i<2;i++)
   {
    var request=await owner.PrepareAndScheduleAsync(process,phrase,part.Source,null,null,1,.1f);
    if(request is null)throw new InvalidOperationException("Phrase request rejected.");
    await Task.WhenAny(request.Admission,Task.Delay(2000));
    if(!request.Admission.IsCompletedSuccessfully || !request.Admission.Result)throw new InvalidOperationException("Phrase admission failed.");
    scheduled.Add(request.Sample);
    if(stallMainThread && i==0)
    {
     var approach=System.Diagnostics.Stopwatch.StartNew();
     while(owner.Position<request.Sample-2205 && approach.Elapsed.TotalSeconds<3)await Task.Delay(1);
     if(Thread.CurrentThread.ManagedThreadId!=invocationThread)throw new InvalidOperationException("Stall check lost the invoking editor thread.");
     var before=owner.Position;var stall=System.Diagnostics.Stopwatch.StartNew();
     Thread.Sleep(200);stallMs=stall.Elapsed.TotalMilliseconds;
     crossed=before<request.Sample && owner.Position>request.Sample;
    }
    var watch=System.Diagnostics.Stopwatch.StartNew();
    while(owner.Position<request.Sample+pcm.Length/2+1024 && watch.Elapsed.TotalSeconds<3)
    {
     var interval=System.Diagnostics.Stopwatch.StartNew();
     await Task.Delay(updateDelay);
     maximumUpdateInterval=Math.Max(maximumUpdateInterval,interval.Elapsed.TotalMilliseconds);
    }
    if(owner.Position<request.Sample+pcm.Length/2+1024)throw new InvalidOperationException("Shared output clock stalled.");
   }
   // Detach before reading; the tap never writes files or captures other mixers.
   output.Dispose();await Task.Delay(50);
   clock=runtime.Position;pausedFrames=runtime.PausedFrames;valid=runtime.ValidChannelSequence;
   captured=tap.ReadAfterDetach();
  }
  finally
  {
   process.Stop(AudioStopMode.Immediate);process.Release();runtime.Dispose();await owner.StopAsync();
  }
  var frames=captured.Length/2;var aligned=frames==clock+pausedFrames;
  var phrases=scheduled.Select(sample=>
  {
   var start=sample+pausedFrames;double error=0;
   var peak=pcm.Max(value=>Math.Abs(value/32768d*.1));
   if(start<0 || start+pcm.Length/2>frames)error=double.PositiveInfinity;
   else for(var i=0;i<pcm.Length;i++)error=Math.Max(error,Math.Abs(captured[(int)start*2+i]-pcm[i]/32768f*.1f));
   return new PhraseResult(sample,start,pcm.Length/2,error,peak,error<=.000001&&peak>.0001);
  }).ToArray();
  var removed=mixerId.HasValue&&!Mixer.Default.GetChildren().Any(m=>m.Id==mixerId.Value);
  return new(started,valid,aligned,phrases,frames,clock,pausedFrames,removed,runtime.ResidentBytes,
   stallMainThread?200:0,stallMs,crossed,updateRate,maximumUpdateInterval,
   started&&valid&&aligned&&phrases.Length==2&&phrases.All(p=>p.SamplesMatch)&&removed&&runtime.ResidentBytes==0&&(!stallMainThread||crossed));
 }
}
