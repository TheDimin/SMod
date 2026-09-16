using System;
using System.Linq;
using System.Threading.Tasks;
using Smod;
namespace Editor.Mcp;

public static class AudioSourceAuditionReviewTools
{
 /// <summary>Check source gain and pitch refresh on a temporary muted native voice. Uses an existing imported sound, writes no assets, and leaves user previews unchanged. Reports control values and source-clock continuity, not audible loudness.</summary>
 [McpTool("check_audio_source_audition")]
 public static async Task<object> Check(string sourcePath)
 {
  var sound=Sandbox.SoundFile.Load(sourcePath) ?? throw new ArgumentException("Source not found.");
  if(!await sound.LoadAsync() || sound.Duration<2)throw new ArgumentException("Use an existing sound at least two seconds long.");
  var source=new AudioSourceDefinition();source.Tracks.Add(new(){Sound=sound,Length=2});
  var graph=new AudioEvent{UI=true};graph.Sources.Add(source);
  graph.MasterTrack.Instruments.Add(new(){SourceId=source.Id,TrimEnd=2});
  AudioEventValidator.Validate(graph).AssertValid();
  var instance=AudioSystem.GetEvent(graph).CreateInstance();instance.Volume=0;
  AudioLiveVoice Snapshot()=>AudioLiveDiagnostics.CaptureActive().Single(x=>x.Id==instance.LiveId).Voices.Single();
  object result;
  try
  {
   instance.Start();
   var clock=System.Diagnostics.Stopwatch.StartNew();
   while(clock.Elapsed.TotalSeconds<1 && Snapshot().SourcePosition<.1f)
   { _=instance.PlaybackState;await Task.Delay(10); }
   instance.Paused=true;var before=Snapshot();
   source.Volume=new(.25f);source.Pitch=new(.8f);
   instance.RefreshAuthoredPropertiesForAudition();var changed=Snapshot();
   source.Volume=new(1f);source.Pitch=new(1f);
   instance.RefreshAuthoredPropertiesForAudition();var restored=Snapshot();
   var progressed=before.SourcePosition>=.1f;
   var applied=Math.Abs(changed.NativeGain-.25f)<.0001f && Math.Abs(changed.Pitch-.8f)<.0001f;
   var recovered=Math.Abs(restored.NativeGain-1)<.0001f && Math.Abs(restored.Pitch-1)<.0001f;
   var held=Math.Abs(changed.SourcePosition-before.SourcePosition)<.02f && Math.Abs(restored.SourcePosition-before.SourcePosition)<.02f;
   result=new{Passed=progressed&&applied&&recovered&&held,SourceProgressed=progressed,ChangedValuesApplied=applied,RestoredValuesApplied=recovered,PausedClockHeld=held,Before=before,Changed=changed,Restored=restored};
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
  return new{Result=result,Cleaned=!AudioLiveDiagnostics.CaptureActive().Any(x=>x.Id==instance.LiveId)};
 }
}
