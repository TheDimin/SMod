namespace Smod;

/// <summary>A bank of phrases started by world facts on a compatible music bar.</summary>
public sealed class AudioOverlayRule
{
 [Property, Description("Only a direct parent music control can request these phrases. Local activity and trigger counters are ignored.")] public bool ParentControlled { get; set; }
 [Property] public string VoiceGroup { get; set; } = "lead";
 [Property] public double QuantizationBeats { get; set; }
 /// <summary>Shared-output scheduling safety margin after source preparation; milliseconds, not acoustic latency.</summary>
 [Property] public int SchedulingLeadMilliseconds { get; set; } = 60;
 [Property] public bool QueueWhilePlaying { get; set; } = true;
 [Property] public bool IgnoreTriggersDuringCooldown { get; set; }
 /// <summary>Shared output only. One disables chaining; larger values bound success-triggered phrases.</summary>
 [Property] public int MaximumChainPhrases { get; set; } = 1;
 /// <summary>Silence in this voice group after the last chained source tail, in beats.</summary>
 [Property] public float ChainRestBeats { get; set; } = 16;
 [Property] public bool Enabled { get; set; }
 [Property, InlineEditor] public ParameterConditionSet Conditions { get; set; } = new();
 [Property] public AudioParameter TriggerParameter { get; set; }
 [Property, InlineEditor] public ParameterConditionSet ActivityConditions { get; set; }
 [Property] public string BeatGridName { get; set; }
 [Property] public float BeatsPerMinute { get; set; } = 100;
 [Property] public float CooldownBeats { get; set; } = 16;
 [Property] public float Volume { get; set; } = .5f;
}

public sealed partial class AudioEventInstance
{
 sealed class OverlayState
 {
  public AudioParameterValue Last;
  public bool Pending;
  public string LastDecision;
  public int ChainCount, ChainCredits;
  public long ChainEnd, ChainRestUntil;
  public long Boundary;
  public AudioInstrument Previous;
 }
 sealed class OverlayVoice
 {
  public IAudioVoice Handle;
  public AudioVoiceRequest Request;
  public double Started;
  public float Position, NativeVolume, NativePitch;
 }
 readonly Dictionary<AudioTrack, OverlayState> overlayStates = new();
 readonly Random overlayRandom = new();
 readonly List<OverlayVoice> overlayVoices = new();
 readonly Dictionary<string,double> overlayReady = new();
 float overlayLastClock;
 double overlayClock;

 AudioBeatGrid overlayGrid;
 void ResetOverlays()
 {
  StopOverlay();overlayStates.Clear();overlayClock=0;overlayReady.Clear();overlayGrid=null;
  overlayLastClock=AudioSystem.TimelineScheduler.Now;
  foreach(var track in _description.Resource.Tracks.Where(x=>x.Overlay?.Enabled==true))
   overlayStates[track]=new(){Last=track.Overlay.TriggerParameter is {} p ? _parameters[p] : default};
 }
 void StopOverlay()
 {
  StopSharedOverlays();parentLayers.Clear();
  foreach(var voice in overlayVoices)voice.Handle.Stop(0);overlayVoices.Clear();
  foreach(var state in overlayStates.Values)state.Pending=false;
 }
 bool OverlayMatches(ParameterConditionSet conditions) => AudioTimelineControlEvaluator.Matches(conditions,_parameters);
 void UpdateOverlays()
 {
  if(sharedMusic is not null){UpdateSharedOverlays();return;}
  var now=AudioSystem.TimelineScheduler.Now;
  overlayClock+=Math.Max(0,now-overlayLastClock);overlayLastClock=now;
  if(_playbackState!=AudioPlaybackState.Playing)return;
  foreach(var active in overlayVoices.ToArray())
   if(overlayClock-active.Started>=active.Request.Instrument.TrimEnd-active.Request.Instrument.TrimStart || !active.Handle.IsPlaying)
   {active.Handle.Stop(0);overlayVoices.Remove(active);}
  var position=CurrentTimelinePosition;
  var grid=_description.Resource.BeatGridAt(position);
  foreach(var pair in overlayStates)
  {
   var rule=pair.Key.Overlay;var state=pair.Value;
   if(rule.TriggerParameter is {} p)
   {var value=_parameters[p];if(!value.Equals(state.Last) && (rule.QueueWhilePlaying || !overlayVoices.Any(x=>x.Request.Track.Overlay.VoiceGroup==rule.VoiceGroup)) && (!rule.IgnoreTriggersDuringCooldown || overlayClock>=overlayReady.GetValueOrDefault(rule.VoiceGroup)))state.Pending=true;state.Last=value;}
   if(!OverlayMatches(rule.Conditions))state.Pending=false;
  }
  if(grid is null)return;
  var barSeconds=grid.SecondsPerBeat*grid.BeatsPerBar;
  var bar=(long)Math.Floor((position-grid.Start.Seconds)/barSeconds+.00001);
  if(grid!=overlayGrid)
  {
   overlayGrid=grid;
   foreach(var pair in overlayStates)pair.Value.Boundary=(long)Math.Floor((position-grid.Start.Seconds)/(grid.SecondsPerBeat*(pair.Key.Overlay.QuantizationBeats>0 ? pair.Key.Overlay.QuantizationBeats : grid.BeatsPerBar)));
   return;
  }
  IReadOnlyList<AudioVoiceRequest> requests=null;
  foreach(var pair in overlayStates.OrderByDescending(x=>x.Value.Pending))
  {
   var track=pair.Key;var rule=track.Overlay;var state=pair.Value;
   var quantum=grid.SecondsPerBeat*(rule.QuantizationBeats>0 ? rule.QuantizationBeats : grid.BeatsPerBar);
   var boundary=(long)Math.Floor((position-grid.Start.Seconds)/quantum+.00001);
   if(boundary==state.Boundary)continue;
   state.Boundary=boundary;
   var late=position-grid.Start.Seconds-boundary*quantum;
   if(late>Math.Min(grid.SecondsPerBeat,quantum*.5) || overlayVoices.Any(x=>x.Request.Track.Overlay.VoiceGroup==rule.VoiceGroup) || overlayClock<overlayReady.GetValueOrDefault(rule.VoiceGroup))continue;
   if(!OverlayMatches(rule.Conditions) || (!string.IsNullOrEmpty(rule.BeatGridName) && rule.BeatGridName!=grid.Name) || Math.Abs(rule.BeatsPerMinute-grid.BeatsPerMinute)>.01)continue;
   if(!state.Pending && (rule.ActivityConditions?.Conditions is not {Count:>0} || !OverlayMatches(rule.ActivityConditions)))continue;
   requests ??= AudioEventEvaluator.BuildPlaybackPlan(_description.Resource,_parameters,SelectSoundTrack);
   var candidates=requests.Where(x=>x.Track==track && (x.Instrument.OverlayBars.Count==0 || x.Instrument.OverlayBars.Contains((int)bar+1)) && position+x.Instrument.TrimEnd-x.Instrument.TrimStart<=grid.End.Seconds+.001).ToList();
   if(candidates.Count>1)candidates.RemoveAll(x=>x.Instrument==state.Previous);
   if(candidates.Count==0)continue;
   var request=candidates[overlayRandom.Next(candidates.Count)];
   var handle=AudioSystem.Backend.Play(request.Sound,_attributes3D,!_hasAttributes3D);
   handle.Paused=true;
   overlayVoices.Add(new(){Handle=handle,Request=request,Started=overlayClock-late,Position=(float)(position-late),NativeVolume=handle.Volume,NativePitch=handle.Pitch});
   handle.Time=request.SoundTrack.Offset+request.Instrument.TrimStart+(float)late;
   ApplyOverlayGain();handle.Paused=false;
   state.Pending=false;state.Previous=request.Instrument;
   overlayReady[rule.VoiceGroup]=overlayClock+Math.Max(rule.CooldownBeats*grid.SecondsPerBeat,request.Instrument.TrimEnd-request.Instrument.TrimStart);

  }
 }
 void ApplyOverlayGain()
 {
  foreach(var voice in overlayVoices)ApplyOverlayGain(voice);
 }
 void ApplyOverlayGain(OverlayVoice voice)
 {
  var request=voice.Request;var instrument=request.Instrument;
  if(voice.Handle is IAudioToneVoice tone)tone.SetTone(_description.Resource.MasterTrack.Tone,ReferenceEquals(request.Track,_description.Resource.MasterTrack)?null:request.Track.Tone);
  var elapsed=overlayClock-voice.Started;var remaining=instrument.TrimEnd-instrument.TrimStart-elapsed;
  var fade=Math.Clamp(elapsed/Math.Max(.005,instrument.FadeInDuration),0,1)*Math.Clamp(remaining/Math.Max(.005,instrument.FadeOutDuration),0,1);
  voice.Handle.Volume=_auditionMutedTracks.Contains(request.Track) || _auditionMutedTracks.Contains(_description.Resource.MasterTrack) ? 0 : voice.NativeVolume*request.Volume*request.Track.Overlay.Volume*_volume*(float)fade*HandoffGain(request.Track);
  voice.Handle.Pitch=voice.NativePitch*request.Pitch*_pitch;
  voice.Handle.Position=_attributes3D.Position;voice.Handle.ListenLocal=!_hasAttributes3D;
 }
}

public static partial class AudioEventValidator
{
 static void ValidateOverlays(AudioEvent e,string path,IReadOnlyDictionary<string,AudioParameter> parameters,AudioEventValidationResult result)
 {
  foreach(var track in (e.Tracks ?? new()).Prepend(e.MasterTrack))
  {
   if(track?.Instruments?.Any(clip=>clip?.OverlayBodySeconds>0)==true && (!e.SharedMusicOutput || track.Overlay?.Enabled!=true))
    result.Error(path+"/Overlay/"+track.Name,"A separate phrase body requires a shared-output overlay track.");
   foreach(var clip in track?.Instruments ?? new())
   {
    if(clip?.AllowReleaseTailAcrossLoop==true && (!e.SharedMusicOutput || track.Overlay?.Enabled!=true || clip.OverlayBodySeconds<=0 || clip.CompoundHits is {Count:>0}))
     result.Error(path,"A loop-crossing tail requires a shared ordinary phrase with a positive body duration.");
    if(clip?.CompoundHits is not {Count:>0} hits)continue;
    if(!e.SharedMusicOutput || track.Overlay?.Enabled!=true || clip.IsSubEvent || clip.OverlayBodySeconds!=0)
     result.Error(path,"Compound hits require a shared-output sound overlay with no separate body duration.");
    if(hits.Count>3 || hits.Any(hit=>hit is null || !hit.IsValid))result.Error(path,"Compound instruments require one to three valid source ranges and beat offsets.");
    else if(result.IsValid && e.Source(clip)?.Tracks?.Any(source=>source.Length>0 && hits.Any(hit=>hit.SourceOffset+hit.Length>source.Length))==true)
     result.Error(path,"Compound hit exceeds its Sound Event source range.");
   }
   if(track?.Overlay is not {Enabled:true} rule)continue;
   var at=path+"/Overlay/"+track.Name;
   if(track.Instruments?.Any(instrument=>instrument?.IsSubEvent==true)==true)result.Error(at,"Put phrase banks inside the child event; overlay source slots require sound instruments.");
   if(string.IsNullOrWhiteSpace(rule.VoiceGroup) || !double.IsFinite(rule.QuantizationBeats) || rule.QuantizationBeats<0 || rule.QuantizationBeats>32 || rule.QuantizationBeats>0 && rule.QuantizationBeats<.125)result.Error(at,"Overlay group is required and beat quantization must be between 0 and 32.");
   if(rule.MaximumChainPhrases<1 || rule.MaximumChainPhrases>8 || !float.IsFinite(rule.ChainRestBeats) || rule.ChainRestBeats<1)
    result.Error(at,"Phrase chains require a limit of 1 to 8 and a finite rest of at least one beat.");
   if(rule.MaximumChainPhrases>1 && (!e.SharedMusicOutput || rule.TriggerParameter is null || rule.ActivityConditions?.Conditions is {Count:>0} || track.Instruments.Any(i=>i.CompoundHits is {Count:>0})))
    result.Error(at,"Phrase chains require a shared-output trigger-only overlay with ordinary sound phrases.");
   if(rule.SchedulingLeadMilliseconds<12 || rule.SchedulingLeadMilliseconds>1000 || !e.SharedMusicOutput && rule.SchedulingLeadMilliseconds!=60)
    result.Error(at,"Scheduling lead requires shared output and 12 to 1000 milliseconds; ordinary output uses the default 60.");
   ValidateTimelineConditions(rule.Conditions,at,parameters,result);
   if(rule.ActivityConditions is not null)ValidateTimelineConditions(rule.ActivityConditions,at,parameters,result);
   if(rule.TriggerParameter is null && rule.ActivityConditions?.Conditions is not {Count:>0})result.Error(at,"An overlay needs a world trigger or activity conditions.");
   if(rule.TriggerParameter is {} p && !parameters.Values.Contains(p))result.Error(at,"Overlay trigger must be a declared parameter.");
   if(!float.IsFinite(rule.BeatsPerMinute) || rule.BeatsPerMinute<=0 || !float.IsFinite(rule.CooldownBeats) || rule.CooldownBeats<.25f || !float.IsFinite(rule.Volume) || rule.Volume<0)result.Error(at,"Overlay tempo, cooldown, and volume must be finite and in range.");
   if(!string.IsNullOrEmpty(rule.BeatGridName) && !(e.BeatGrids ?? new()).Any(x=>x?.Name==rule.BeatGridName))result.Error(at,"Overlay grid name must refer to an existing grid.");
   if(e.BeatGrids is not {Count:>0})result.Error(at,"Overlays require a music beat grid.");
   foreach(var clip in track.Instruments ?? new())
    if(clip is not null && (clip.CompoundHits is not {Count:>0} && clip.TrimEnd<=clip.TrimStart || clip.OverlayBars is null || clip.OverlayBars.Any(x=>x<1)))result.Error(at,"Overlay clips need a finite trim range and positive destination bar numbers.");
  }
 }
}
