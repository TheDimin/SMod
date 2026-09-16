using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Smod;
namespace Editor.Mcp;
public static partial class AudioSessionMeterTools
{
 public sealed record ChannelSummary(int Listener,int Channel,long Windows,double MeasuredSeconds,double PeakDbfs,double EnergyRmsDbfs,long ClippedFrames,long InvalidFrames,double? WorstMonoLossDb);
 public sealed record InstrumentActivity(string Instance,string Resource,string Link,double FirstSeen,double LastSeen,double MinimumGain,double MaximumGain,JsonElement Voice);
 public sealed record WindowContext(JsonElement Measurement,int Showing,bool Truncated,InstrumentActivity[] Instruments);
 public sealed record ReportResult(string SessionId,string Path,bool Complete,ChannelSummary[] Channels,WindowContext[] LoudestWindows,WindowContext[] QuietestActiveStereoWindows,WindowContext[] MonoCancellationWindows,string Interpretation);
 sealed class Totals
 {
  internal long Windows,Frames,Clipped,Invalid;internal double Peak,Energy;internal double? Mono;
 }
 /// <summary>Summarize a stopped session: per-listener/channel sample peak, energy RMS, clipping, quiet active stereo windows and mono cancellation. Returns up to limit windows of each kind (1-20, default 5). Includes observed instruments and gain ranges within each window (max 64). fromSeconds/toSeconds select complete windows; toSeconds=0 means session end. Gain snapshots show candidates, not proven isolated contributions. Channel -1 is front stereo. Does not sum listeners or label RMS as LUFS.</summary>
 [McpTool.ReadOnly("audio_session_report")]
 public static ReportResult Report(string sessionId,int limit=5,double fromSeconds=0,double toSeconds=0)
 {
  if(!double.IsFinite(fromSeconds)||!double.IsFinite(toSeconds)||fromSeconds<0||toSeconds<0||toSeconds>0&&toSeconds<=fromSeconds)throw new ArgumentException("Use a finite time range; toSeconds=0 means session end.");
  if(limit<1||limit>20)throw new ArgumentOutOfRangeException(nameof(limit));
  // Reuse the same identifier and stopped-session validation as the paged reader.
  _=Read(sessionId,0,1);
  var path=System.IO.Path.Combine(Folder,sessionId+".jsonl");
  var totals=new Dictionary<(int,int),Totals>();
  var loud=new List<JsonElement>();var quiet=new List<JsonElement>();var mono=new List<JsonElement>();var complete=false;
  static double Value(JsonElement row,string field)=>row.GetProperty("levels").GetProperty(field).GetDouble();
  void Keep(List<JsonElement> rows,JsonElement row,string field,bool descending)
  {
   rows.Add(row.Clone());rows.Sort((a,b)=>descending?Value(b,field).CompareTo(Value(a,field)):Value(a,field).CompareTo(Value(b,field)));
   if(rows.Count>limit)rows.RemoveAt(rows.Count-1);
  }
  foreach(var line in File.ReadLines(path))
  {
   using var doc=JsonDocument.Parse(line);var row=doc.RootElement;var type=row.GetProperty("type").GetString();
   if(type=="footer")complete=true;
   if(type!="levels")continue;
   var channel=row.TryGetProperty("channel",out var c)?c.GetInt32():-1;var key=(row.GetProperty("listener").GetInt32(),channel);
   if(!totals.TryGetValue(key,out var t))totals[key]=t=new();
   var levels=row.GetProperty("levels");var frames=levels.GetProperty("frames").GetInt64();
   var seconds=row.GetProperty("seconds").GetDouble();if(seconds-frames/44100d<fromSeconds || toSeconds>0&&seconds>toSeconds)continue;
   t.Windows++;t.Frames+=frames;t.Peak=Math.Max(t.Peak,levels.GetProperty("samplePeak").GetDouble());
   var rms=levels.GetProperty("rms").GetDouble();t.Energy+=rms*rms*frames;
   t.Clipped+=levels.GetProperty("clippedFrames").GetInt64();t.Invalid+=levels.GetProperty("invalidFrames").GetInt64();
   if(channel>=0)Keep(loud,row,"peakDbfs",true);
   if(channel==-1)
   {
    if(rms>1e-6)Keep(quiet,row,"rmsDbfs",false);
    if(levels.TryGetProperty("monoLossDb",out var loss)&&loss.ValueKind==JsonValueKind.Number)
    {var value=loss.GetDouble();t.Mono=t.Mono.HasValue?Math.Min(t.Mono.Value,value):value;if(value< -6)Keep(mono,row,"monoLossDb",false);}
   }
  }
  return new(sessionId,path,complete,totals.Where(p=>p.Value.Windows>0).Select(p=>new ChannelSummary(p.Key.Item1,p.Key.Item2,p.Value.Windows,p.Value.Frames/44100d,AudioLevelStatistics.Decibels(p.Value.Peak),AudioLevelStatistics.Decibels(Math.Sqrt(p.Value.Energy/Math.Max(1,p.Value.Frames))),p.Value.Clipped,p.Value.Invalid,p.Value.Mono)).ToArray(),Context(path,loud),Context(path,quiet),Context(path,mono),
   "Digital pre-limiter output estimate, not speaker dBSPL, true peak, or LUFS. Channel -1 is front stereo; 0-7 are individual engine channels. Listener contributions remain separate. Correlate window times with voice snapshots; a listed instrument is not proof it caused a peak. Gaps and the final partial window are excluded from energy averages.");
 }
 static WindowContext[] Context(string path,List<JsonElement> windows)
 {
  var maps=windows.Select(_=>new Dictionary<string,InstrumentActivity>()).ToArray();var truncated=new bool[windows.Count];
  foreach(var line in File.ReadLines(path))
  {
   using var doc=JsonDocument.Parse(line);var row=doc.RootElement;
   if(row.GetProperty("type").GetString()!="voices")continue;
   var seconds=row.GetProperty("seconds").GetDouble();
   for(var i=0;i<windows.Count;i++)
   {
    var end=windows[i].GetProperty("seconds").GetDouble();var duration=windows[i].GetProperty("levels").GetProperty("frames").GetInt64()/44100d;
    if(seconds<end-duration-.1 || seconds>end+.1)continue;
    foreach(var instance in row.GetProperty("instances").EnumerateArray())
    foreach(var voice in instance.GetProperty("voices").EnumerateArray())
    {
     var id=instance.GetProperty("id").GetString();var resource=instance.GetProperty("resource").GetString();
     var instrument=voice.GetProperty("instrumentId").GetString();
     var key=id+"/"+instrument+"/"+voice.GetProperty("start")+"/"+voice.GetProperty("source");
     var gain=voice.GetProperty("gain").GetDouble();
     if(maps[i].TryGetValue(key,out var previous))maps[i][key]=previous with{LastSeen=seconds,MinimumGain=Math.Min(previous.MinimumGain,gain),MaximumGain=Math.Max(previous.MaximumGain,gain),Voice=voice.Clone()};
     else if(maps[i].Count<64)maps[i][key]=new(id,resource,resource?.EndsWith(".audevt",StringComparison.OrdinalIgnoreCase)==true && Guid.TryParse(instrument,out var guid) && guid!=Guid.Empty ? "smod:"+resource+"#"+guid.ToString("N") : null,seconds,seconds,gain,gain,voice.Clone());
     else truncated[i]=true;
    }
   }
  }
  return windows.Select((window,i)=>new WindowContext(window,maps[i].Count,truncated[i],maps[i].Values.ToArray())).ToArray();
 }

}
