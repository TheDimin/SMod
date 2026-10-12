using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sandbox.Audio;
namespace Smod;

/// <summary>Short, isolated parent transition check. Its meters silence only its own outputs.</summary>
public static class AudioMusicParentNativeCheck
{
 sealed class Meter : AudioProcessor<Meter.State>
 {
  public sealed class State : ListenerState { }
  long frames;
  public float Peak;
  public long Frames=>Interlocked.Read(ref frames);
  protected override void ProcessSingleChannel(AudioChannel channel,Span<float> input)
  {
   foreach(var sample in input)Peak=Math.Max(Peak,Math.Abs(sample));
   if(channel.Get()==1)Interlocked.Add(ref frames,input.Length);
   input.Clear();
  }
 }
 sealed record Output(Meter Meter,SboxMusicOutput OutputNode,AudioMusicRuntimeSession Runtime);
 public sealed record Result(bool InitialPlaying,bool LoadingObserved,bool OldRetainedWhileLoading,bool ReplacementPaused,
  bool Changed,bool NewClockAdvanced,int Sessions,long PeakResidentBytes,bool BothHadSignal,
  bool MixersRemoved,long ResidentAfterStop,string Error,bool Passed);
 public static async Task<Result> Run(AudioEvent first,AudioEvent second,bool songEnd=false)
 {
  var phase=first.Parameters.First(p=>p.Name=="WorldState");
  var instrument=new AudioInstrument{SubEventQuantization=TransitionQuantization.NextBeat,SubEventTransitionBeats=1,
   SubEvents=new(){new(){Event=first,Conditions=new(){new(){Parameter=phase,Minimum=0,Maximum=0}}},
    new(){Event=second,Conditions=new(){new(){Parameter=phase,Minimum=1,Maximum=1}}}}};
  var parent=new AudioEvent{UI=true,Parameters=new(){phase},MasterTrack=new(){Name="Parent",Instruments=new(){instrument}}};
  AudioParameter available=null;
  if(songEnd)
  {
   // Fixture-only eligibility fact: make the first selection deterministic without changing song world state.
   available=new AudioParameter{Name="TestChoiceAvailable",Minimum=0,Maximum=1};parent.Parameters.Add(available);
   instrument.SubEvents[0].Conditions.Clear();
   instrument.SubEvents[1].Conditions=new(){new(){Parameter=available,Minimum=1,Maximum=1}};
   instrument.RotateSubEventsAtEnd=true;
  }
  var outputs=new List<Output>();var ids=new HashSet<Guid>();
  var instance=AudioSystem.GetEvent(parent).CreateInstance();
  instance.SharedMusicSessionFactory=()=>
  {
   if(outputs.Count>=2)throw new InvalidOperationException("Unexpected third song session.");
   var meter=new Meter();var node=new SboxMusicOutput(meter);
   var runtime=new AudioMusicRuntimeSession(node,maximumBytes:AudioMusicEventPlayback.DefaultPreparationBudgetBytes);
   outputs.Add(new(meter,node,runtime));return runtime;
  };
  bool initial=false,loading=false,retained=false,paused=false,changed=false,advanced=false;long resident=0;string error=null;
  void Observe()
  {
   foreach(var output in outputs)if(output.OutputNode.MixerId is Guid id)ids.Add(id);
   resident=Math.Max(resident,outputs.Sum(o=>o.Runtime.ResidentBytes));
  }
  try
  {
   instance.Start();var clock=System.Diagnostics.Stopwatch.StartNew();
   while(outputs.Count==0 || !outputs[0].Runtime.IsRunning)
   {
    if(clock.Elapsed.TotalSeconds>30)throw new InvalidOperationException("Initial song load timed out.");
    _=instance.PlaybackState;Observe();await Task.Delay(10);
   }
   await Task.Delay(120);Observe();initial=outputs[0].Runtime.Position>0;
   var old=instance.InstrumentPlaybackStates.Single().ChildInstanceId;
   if(songEnd)
   {
    instance.SetParameterByName(available.Name,1);
    instance.TimelinePosition=first.TimelineDuration-Math.Min(2.4f,first.TimelineDuration*.25f);
    clock.Restart();
    while(outputs.Count<2)
    {
     _=instance.InstrumentPlaybackStates;Observe();
     if(clock.Elapsed.TotalSeconds>3)throw new InvalidOperationException("Song-end preparation did not start after the near-end seek.");
     await Task.Delay(10);
    }
   }
   else instance.SetParameterByName("WorldState",1);
   loading=outputs.Count==2 && !outputs[1].Runtime.IsRunning;
   retained=instance.InstrumentPlaybackStates.Single().ChildInstanceId==old && outputs[0].Runtime.IsRunning;
   paused=outputs.Count==2 && outputs[1].Runtime.Paused;
   clock.Restart();
   while(instance.InstrumentPlaybackStates.Single().ChildInstanceId==old)
   {
    paused &= outputs[1].Runtime.Paused && outputs[1].Runtime.Position==0;
    Observe();
    if(clock.Elapsed.TotalSeconds>30)throw new InvalidOperationException("Prepared phase change timed out.");
    await Task.Delay(10);
   }
   changed=outputs.Count==2 && outputs[0].Runtime.IsStopped && outputs[1].Runtime.IsRunning;
   await Task.Delay(150);_=instance.PlaybackState;Observe();advanced=outputs[1].Runtime.Position>0;
  }
  catch(Exception ex){error=ex.Message;}
  finally
  {
   Observe();instance.Stop(AudioStopMode.Immediate);instance.Release();
   foreach(var output in outputs)output.Runtime.Dispose();
  }
  var remaining=outputs.Sum(o=>o.Runtime.ResidentBytes);
  var removed=ids.Count==2 && !Mixer.Default.GetChildren().Any(m=>ids.Contains(m.Id));
  var signal=outputs.Count==2 && outputs.All(o=>o.Meter.Peak>.0001f);
  return new(initial,loading,retained,paused,changed,advanced,outputs.Count,resident,signal,removed,remaining,error,
   initial&&loading&&retained&&paused&&changed&&advanced&&signal&&removed&&remaining==0&&error is null);
 }
}
