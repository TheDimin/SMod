using System;
using System.Linq;
using System.Threading.Tasks;
using Sandbox.Audio;
namespace Smod;

public static partial class AudioMusicOutputNativeCheck
{
 static (SoundEventResource Source,AudioParameter Trigger,AudioInstrument Clip) TailFixture(AudioEvent resource)
 {
  var source=resource.Tracks.First(t=>t.Overlay?.VoiceGroup=="drum-fills").Instruments[0].Sound;
  var grid=resource.BeatGrids.First();var beat=(float)grid.SecondsPerBeat;
  resource.Tracks.RemoveAll(t=>t.Overlay?.Enabled==true);
  foreach(var track in resource.Tracks)track.Volume=0;
  var trigger=new AudioParameter{Name="TailCheck",Type=AudioParameterType.Discrete,Minimum=0,Maximum=10};resource.Parameters.Add(trigger);
  var clip=new AudioInstrument{Sound=source,TrimStart=24,TrimEnd=24+beat*3,OverlayBodySeconds=beat};
  var phraseTrack=new AudioTrack{Name="Tail check",Volume=.1f,Instruments=new(){clip},
   Overlay=new(){Enabled=true,TriggerParameter=trigger,BeatGridName=grid.Name,BeatsPerMinute=grid.BeatsPerMinute,QuantizationBeats=1,CooldownBeats=1,Volume=1}};
  resource.Tracks.Add(phraseTrack);resource.SharedMusicOutput=true;AudioEventValidator.Validate(resource).AssertValid();
  return (source,trigger,clip);
 }
 public sealed record TailLifecycleResult(bool TwoVoices,bool PauseHeld,bool MuteBoth,bool ResumeAdvanced,bool SeekCleared,bool SilentAfterSeek,bool MixerRemoved,long ResidentAfterStop,bool Passed);
 public static async Task<TailLifecycleResult> RunTailLifecycle(AudioEvent resource)
 {
  var (source,trigger,clip)=TailFixture(resource);
  var tap=new Tap();var output=new SboxMusicOutput(tap);var runtime=new AudioMusicRuntimeSession(output,maximumBytes:AudioMusicEventPlayback.DefaultPreparationBudgetBytes);
  var instance=AudioSystem.GetEvent(resource).CreateInstance();instance.SharedMusicSessionFactory=()=>runtime;
  Guid? mixer=null;bool two=false,held=false,muted=false,resumed=false,cleared=false;float[] captured=null;
  AudioLiveVoice[] Rows()=>instance.CaptureLiveSnapshot()?.Voices.Where(v=>v.InstrumentId==clip.Id && v.State!="Inactive").ToArray()??Array.Empty<AudioLiveVoice>();
  async Task WaitUntil(Func<bool> condition)
  {
   var watch=System.Diagnostics.Stopwatch.StartNew();
   while(!condition())
   {
    if(watch.Elapsed.TotalSeconds>5)throw new TimeoutException("Tail lifecycle check timed out.");
    _=instance.InstrumentPlaybackStates;await Task.Delay(5);
   }
  }
  try
  {
   instance.Start();await WaitUntil(()=>instance.PlaybackState==AudioPlaybackState.Playing);mixer=output.MixerId;
   await runtime.PreparePhraseAsync(clip,source.Tracks[0],null,null);
   instance.SetParameterByName(trigger.Name,1);await WaitUntil(()=>Rows().Any(v=>v.IsReleaseTail));
   instance.SetParameterByName(trigger.Name,2);await WaitUntil(()=>Rows().Length==2 && Rows().All(v=>v.State=="Playing"));
   two=true;instance.Paused=true;await Task.Delay(50);var position=runtime.Position;
   await Task.Delay(100);held=position==runtime.Position && Rows().Length==2 && Rows().All(v=>v.State=="Paused");
   var track=resource.Tracks.Single(t=>t.Instruments.Contains(clip));instance.SetTrackMutedForAudition(track,true);
   muted=Rows().Length==2 && Rows().All(v=>v.Gain==0);
   instance.Paused=false;await Task.Delay(100);resumed=runtime.Position>position;
   instance.Paused=true;instance.TimelinePosition=0;await WaitUntil(()=>runtime.Position==0);
   cleared=Rows().Length==0;instance.SetTrackMutedForAudition(track,false);instance.Paused=false;
   await Task.Delay(100);_=instance.InstrumentPlaybackStates;
   output.Dispose();await Task.Delay(50);captured=tap.ReadAfterDetach();
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();runtime.Dispose();}
  var silent=captured.TakeLast(512).All(v=>Math.Abs(v)<.000001);
  var removed=mixer.HasValue&&!Mixer.Default.GetChildren().Any(m=>m.Id==mixer);
  return new(two,held,muted,resumed,cleared,silent,removed,runtime.ResidentBytes,two&&held&&muted&&resumed&&cleared&&silent&&removed&&runtime.ResidentBytes==0);
 }
 public sealed record TailResult(bool TwoRows,bool TailVisible,bool DistinctStarts,int MaximumVoices,
  int OverlapFrames,double MaximumSampleError,double ExpectedPeak,bool MixerRemoved,long ResidentAfterStop,bool Passed);
 public static async Task<TailResult> RunTail(AudioEvent resource)
 {
  var (source,trigger,clip)=TailFixture(resource);
  var tap=new Tap();var output=new SboxMusicOutput(tap);var runtime=new AudioMusicRuntimeSession(output,maximumBytes:AudioMusicEventPlayback.DefaultPreparationBudgetBytes);
  var instance=AudioSystem.GetEvent(resource).CreateInstance();instance.SharedMusicSessionFactory=()=>runtime;
  Guid? mixer=null;float[] captured=null;short[] pcm=null;AudioLiveVoice[] overlap=null;int maximum=0;long[] starts=null;
  async Task WaitUntil(Func<bool> condition)
  {
   var clock=System.Diagnostics.Stopwatch.StartNew();
   while(!condition())
   {
    if(clock.Elapsed.TotalSeconds>5)throw new TimeoutException("Tail check did not reach its next state.");
    _=instance.InstrumentPlaybackStates;
    maximum=Math.Max(maximum,instance.CaptureLiveSnapshot()?.Voices.Count(v=>v.InstrumentId==clip.Id)??0);
    await Task.Delay(5);
   }
  }
  AudioLiveVoice[] Rows()=>instance.CaptureLiveSnapshot()?.Voices.Where(v=>v.InstrumentId==clip.Id && v.State is "Playing" or "Silent" or "Scheduled").ToArray()??Array.Empty<AudioLiveVoice>();
  try
  {
   instance.Start();await WaitUntil(()=>instance.PlaybackState==AudioPlaybackState.Playing);mixer=output.MixerId;
   pcm=await runtime.PreparePhraseAsync(clip,source.Tracks[0],null,null);
   instance.SetParameterByName(trigger.Name,1);
   await WaitUntil(()=>Rows().Any(v=>v.IsReleaseTail));
   instance.SetParameterByName(trigger.Name,2);
   await WaitUntil(()=>Rows().Length==2 && Rows().All(v=>v.State=="Playing"));
   overlap=Rows();starts=overlap.Select(v=>(long)Math.Round(v.Start*44100d)).Order().ToArray();
   await WaitUntil(()=>runtime.Position>=starts[1]+pcm.Length/2+1024);
   output.Dispose();await Task.Delay(50);captured=tap.ReadAfterDetach();
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();runtime.Dispose();}
  double error=0,peak=0;
  for(var frame=0;frame<captured.Length/2;frame++)for(var channel=0;channel<2;channel++)
  {
   double expected=0;
   foreach(var start in starts){var local=frame-start;if(local>=0 && local<pcm.Length/2)expected+=pcm[local*2+channel]/32768d*.1;}
   peak=Math.Max(peak,Math.Abs(expected));error=Math.Max(error,Math.Abs(captured[frame*2+channel]-expected));
  }
  var overlapFrames=(int)Math.Max(0,starts[0]+pcm.Length/2-starts[1]);
  var removed=mixer.HasValue&&!Mixer.Default.GetChildren().Any(m=>m.Id==mixer);
  return new(overlap.Length==2,overlap.Any(v=>v.IsReleaseTail),starts[0]!=starts[1],maximum,overlapFrames,error,peak,removed,runtime.ResidentBytes,
   overlap.Length==2&&overlap.Any(v=>v.IsReleaseTail)&&maximum==2&&overlapFrames>0&&error<.000001&&peak>.0001&&removed&&runtime.ResidentBytes==0);
 }
}
