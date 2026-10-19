using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sandbox;
using Sandbox.Audio;
using Smod;
namespace Editor.Mcp;

[McpToolset("audio_session","Record level measurements and Smod instrument activity as JSONL; never write audio.")]
public static partial class AudioSessionMeterTools
{
 public sealed record StatusResult(bool Recording,string SessionId,string Path,double Seconds,long Windows,long VoiceSamples,int DroppedWindows,int UnsupportedChannelCallbacks,int PairingErrors,string Error,AudioLevelReading Latest,bool ProcessorAttached=false);
 public sealed record MeterRecord(string Type,double Seconds,int Listener,int Channel,AudioLevelReading Levels,float DownstreamGain);
 public sealed record VoiceRecord(string Type,double Seconds,int Total,int Showing,AudioLiveSnapshot[] Instances);
 public sealed record MarkerRecord(string Type,double Seconds,string Label);
 static Session current;
 static StatusResult last;
 sealed class Session
 {
  internal string Id,Path,Error;
  internal Mixer Mixer;
  internal AudioSessionMeterTap Tap;
  internal StreamWriter Writer;
  internal readonly Stopwatch Clock=Stopwatch.StartNew();
  internal long StartTicks=Stopwatch.GetTimestamp(),Windows,VoiceSamples;
  internal double NextVoices,NextFlush,Limit;
  internal AudioLevelReading Latest;
  internal bool Recording=true;
  internal long LastVoicesTick;
  internal double MaximumVoiceGap;
 }
 static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
 static string Folder=>System.IO.Path.GetFullPath(System.IO.Path.Combine(Project.Current.GetAssetsPath(),"../.validation/audio-sessions"));
 static StatusResult Describe(Session s)=>s is null?new(false,null,null,0,0,0,0,0,0,null,null):new(s.Recording,s.Id,s.Path,s.Clock.Elapsed.TotalSeconds,s.Windows,s.VoiceSamples,s.Tap.Dropped,s.Tap.UnsupportedChannels,s.Tap.PairingErrors,s.Error,s.Latest,!s.Tap.Removed&&s.Mixer.GetProcessors().Contains(s.Tap.NativeProcessor));

 /// <summary>Start a passive session recorder and return immediately. Records master per-listener/channel sample peak/RMS dBFS, clipping, mono loss, and Smod instrument snapshots every 100 ms to JSONL. intervalSeconds is 0.25-10; maxMinutes is 1-240 (default 120). Writes statistics only, never PCM. Captures all in-app master audio including previews; excludes Windows/device volume. Multiple listeners remain separate, not a claimed final sum. Use audio_session_status, audio_session_mark, audio_session_stop, then audio_session_report or audio_session_read.</summary>
 [McpTool("audio_session_start")]
 public static StatusResult Start(double intervalSeconds=1,int maxMinutes=120)
 {
  if(current is not null)throw new InvalidOperationException("A session is already recording. Stop it first.");
  if(!double.IsFinite(intervalSeconds)||intervalSeconds<.25||intervalSeconds>10||maxMinutes<1||maxMinutes>240)throw new ArgumentOutOfRangeException("Use intervalSeconds 0.25-10 and maxMinutes 1-240.");
  var mixer=Mixer.Master??throw new InvalidOperationException("The audio master mixer is unavailable.");
  var id=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N");
  Directory.CreateDirectory(Folder);
  var s=new Session{Id=id,Path=System.IO.Path.Combine(Folder,id+".jsonl"),Mixer=mixer,Tap=new(mixer,intervalSeconds),Limit=maxMinutes*60};
  try
  {
   s.Writer=new StreamWriter(new FileStream(s.Path,FileMode.CreateNew,FileAccess.Write,FileShare.Read));
   Write(s,new{type="header",schemaVersion=2,sessionId=id,startedUtc=DateTime.UtcNow,intervalSeconds,voiceIntervalSeconds=.1,sampleRate=44100,
    measurement="Master processor input per listener and channel; channel -1 is the front stereo pair, scaled by master mixer volume and Sound.MasterVolume; before engine hard limiting and Windows/device output.",
    limits="Not dBSPL, LUFS or true peak. Multiple listeners are not summed. Silence without callbacks is a gap. Voice snapshots can miss sounds shorter than 100 ms. Voice gains are control values, not isolated measured contributions. Last partial audio window is not emitted.",
    stopLimits="Duration, 512 MiB file, mixer replacement/removal, or explicit stop. Only this processor is detached.",masterMixer=mixer.Id,masterVolume=mixer.Volume,soundMasterVolume=Sound.MasterVolume});
   mixer.AddProcessor(s.Tap.NativeProcessor);current=s;return Describe(s);
  }
  catch{if(mixer.GetProcessors().Contains(s.Tap.NativeProcessor))mixer.RemoveProcessor(s.Tap.NativeProcessor);s.Writer?.Dispose();throw;}
 }
 /// <summary>Read recorder status and the latest completed audio window without changing capture or playback.</summary>
 [McpTool.ReadOnly("audio_session_status")]
 public static StatusResult Status()=>current is not null?Describe(current):last??Describe(null);
 /// <summary>Add a timestamped note such as 'busy service' or 'speaker test'. Does not change playback. Label at most 200 characters.</summary>
 [McpTool("audio_session_mark")]
 public static StatusResult Mark(string label)
 {
  if(current is null)throw new InvalidOperationException("Start audio_session_start first.");
  if(string.IsNullOrWhiteSpace(label)||label.Length>200)throw new ArgumentException("Use a label of 1-200 characters.");
  Write(current,new MarkerRecord("marker",current.Clock.Elapsed.TotalSeconds,label));return Describe(current);
 }
 /// <summary>Stop and flush the active measurement session. Detaches only its passive meter. Does not stop play, change volumes, or write audio. The JSONL path can be read with audio_session_read.</summary>
 [McpTool("audio_session_stop")]
 public static StatusResult Stop(){if(current is not null)Finish(current,null);return last??Describe(null);}
 /// <summary>Read recorded JSONL rows. Pass sessionId from start/stop; no arbitrary path. offset is zero-based; limit defaults to 20, max 200. Rows include level windows, voice snapshots, and markers. Use their Seconds values to correlate activity; multi-listener levels are separate.</summary>
 [McpTool.ReadOnly("audio_session_read")]
 public static object Read(string sessionId,int offset=0,int limit=20)
 {
  if(string.IsNullOrWhiteSpace(sessionId)||sessionId.Length>80||sessionId.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-'))throw new ArgumentException("Use the sessionId returned by start.");
  if(offset<0||limit<1||limit>200)throw new ArgumentOutOfRangeException("Use offset >= 0 and limit 1-200.");
  if(current?.Id==sessionId)throw new InvalidOperationException("Stop capture before reading the file; use audio_session_status while recording.");
  var path=System.IO.Path.Combine(Folder,sessionId+".jsonl");
  var rows=File.ReadLines(path).Skip(offset).Take(limit+1).ToArray();
  return new{SessionId=sessionId,Offset=offset,Showing=Math.Min(rows.Length,limit),HasMore=rows.Length>limit,Rows=rows.Take(limit).Select(r=>JsonSerializer.Deserialize<JsonElement>(r)).ToArray()};
 }
 static void Write(Session s,object row)=>s.Writer.WriteLine(JsonSerializer.Serialize(row,JsonOptions));
 [EditorEvent.Frame]
 static void Update()
 {
  var s=current;if(s is null)return;
  try
  {
   if(!ReferenceEquals(s.Mixer,Mixer.Master)||s.Tap.Removed||s.Mixer.GetProcessors().LastOrDefault()!=s.Tap.NativeProcessor){Finish(s,"Master mixer or final processor position changed.");return;}
   Drain(s);
   var now=s.Clock.Elapsed.TotalSeconds;
   if(now>=s.NextVoices)
   {
    if(s.LastVoicesTick>0)s.MaximumVoiceGap=Math.Max(s.MaximumVoiceGap,(Stopwatch.GetTimestamp()-s.LastVoicesTick)/(double)Stopwatch.Frequency);
    s.LastVoicesTick=Stopwatch.GetTimestamp();
    var all=AudioLiveDiagnostics.CaptureActive();
    Write(s,new VoiceRecord("voices",now,all.Count,Math.Min(all.Count,128),all.Take(128).Select(x=>x with{Voices=x.Voices.Where(v=>v.State!="Inactive"&&v.State!="Stopped").Take(256).ToArray()}).ToArray()));s.VoiceSamples++;
    s.NextVoices=now+.1;
   }
   if(now>=s.NextFlush){s.Writer.Flush();s.NextFlush=now+2;}
   if(now>=s.Limit||s.Writer.BaseStream.Position>=512L*1024*1024)Finish(s,"Capture duration or size limit reached.");
  }
  catch(Exception error){Finish(s,error.Message);}
 }
 static void Drain(Session s)
 {
  while(s.Tap.TryRead(out var window))
  {
   if(window.Channel==-1)s.Latest=window.Levels;s.Windows++;
   Write(s,new MeterRecord("levels",(window.EndTicks-s.StartTicks)/(double)Stopwatch.Frequency,window.Listener,window.Channel,window.Levels,window.DownstreamGain));
  }
 }
 static void Finish(Session s,string error)
 {
  current=null;s.Recording=false;s.Error=error;
  try
  {
   if(!s.Tap.Removed)s.Mixer.RemoveProcessor(s.Tap.NativeProcessor);
   Drain(s);s.Clock.Stop();
   Write(s,new{type="footer",seconds=s.Clock.Elapsed.TotalSeconds,s.Windows,s.VoiceSamples,error,s.Tap.Dropped,s.Tap.UnsupportedChannels,s.Tap.PairingErrors,s.MaximumVoiceGap});
  }
  catch(Exception failure){s.Error=error is null?failure.Message:error+"; "+failure.Message;}
  finally{try{s.Writer?.Dispose();}catch(Exception failure){s.Error=failure.Message;}s.Clock.Stop();last=Describe(s);}
 }
 [Event("app.exit")]
 static void Exit(){if(current is not null)Finish(current,"Editor exit.");}
}
