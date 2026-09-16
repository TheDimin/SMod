namespace Smod;

/// <summary>Parent-owned activity control for one continuous track in a shared-clock child.
/// The source keeps its authored position. Only its gain changes.</summary>
public enum AudioMusicControlMode { SustainLayer, TriggerPhraseBank }
public enum AudioLayerReleaseCurve { LinearAmplitude, SmoothDecibels }

public sealed class AudioMusicLayerControl
{
 [Property] public AudioMusicControlMode Mode { get; set; }
 [Property] public Guid SubEventInstrumentId { get; set; }
 [Property, Description("Optional child resource scope. Set this when choices contain copied track IDs. This is an authored source reference, never a gameplay song selector.")] public AudioEvent ChildEvent { get; set; }
 [Property] public Guid ChildTrackId { get; set; }
 [Property] public AudioParameter TriggerParameter { get; set; }
 [Property, Range(1,64)] public int PhraseBeats { get; set; } = 8;
 [Property, Range(1,8), Description("Sustain Layer only. Trigger Phrase Bank uses the target bank's chain and release rules.")] public int MaximumPhrases { get; set; } = 3;
 [Property, Range(1,64)] public int RestBeats { get; set; } = 8;
 [Property, Range(0,4)] public float IdleGain { get; set; } = .25f;
 [Property, Range(0,4)] public float ActiveGain { get; set; } = 1f;
 [Property, Range(.01f,5)] public float AttackSeconds { get; set; } = .12f;
 [Property, Range(.01f,5)] public float ReleaseSeconds { get; set; } = .6f;
 [Property, Description("Smooth Decibels eases the audible level at both ends of the release. Applies to sustain controls only.")] public AudioLayerReleaseCurve ReleaseCurve {get;set;}
 internal AudioMusicLayerControl Copy() => new()
 {
  Mode=Mode, SubEventInstrumentId=SubEventInstrumentId, ChildTrackId=ChildTrackId,
  TriggerParameter=TriggerParameter, PhraseBeats=PhraseBeats, MaximumPhrases=MaximumPhrases,
  RestBeats=RestBeats, IdleGain=IdleGain, ActiveGain=ActiveGain,
  AttackSeconds=AttackSeconds, ReleaseSeconds=ReleaseSeconds, ReleaseCurve=ReleaseCurve
 };
 internal AudioLayerPlan Prepare(double secondsPerBeat) => new(
  PhraseBeats*secondsPerBeat,MaximumPhrases,RestBeats*secondsPerBeat,IdleGain,ActiveGain,AttackSeconds,ReleaseSeconds,ReleaseCurve);
}

/// <summary>Immutable prepared rule. No source copies or runtime clip composition.</summary>
internal sealed record AudioLayerPlan(double PhraseSeconds,int MaximumPhrases,double RestSeconds,
 float IdleGain,float ActiveGain,float AttackSeconds,float ReleaseSeconds,AudioLayerReleaseCurve ReleaseCurve=AudioLayerReleaseCurve.LinearAmplitude);

internal sealed class AudioLayerPlayback
{
 readonly AudioLayerPlan plan;
 double previous, end, restEnd;
 double releaseStart=double.NaN;float releaseGain;
 float lastCounter;
 bool extension;
 public int Phrases { get; private set; }
 public bool Active => Phrases>0;
 public float Gain { get; private set; }
 public double NextBoundary => Active ? end : restEnd;
 public AudioLayerPlayback(AudioLayerPlan plan,double clock,float counter)
 {this.plan=plan;previous=clock;lastCounter=counter;Gain=plan.IdleGain;}
 public void Update(double clock,float counter,bool enabled)
 {
  var seeked=clock<previous;
  var wasActive=Active;var priorEnd=end;
  var elapsed=Math.Max(0,clock-previous);previous=clock;
  // A seek starts a fresh phrase policy without replaying old action counters.
  if(seeked || !enabled && Active)
  {Phrases=0;extension=false;restEnd=clock+plan.RestSeconds;}
  if(Active && clock>=end)
  {
   if(extension && Phrases<plan.MaximumPhrases && clock<end+plan.PhraseSeconds)
   {Phrases++;end+=plan.PhraseSeconds;extension=false;}
   else {Phrases=0;extension=false;restEnd=end+plan.RestSeconds;}
  }
  if(counter>lastCounter && enabled)
  {
   if(Active && Phrases<plan.MaximumPhrases)extension=true;
   else if(!Active && clock>=restEnd)
   {
    Phrases=1;
    // Keep at least one complete phrase, ending on the existing song grid.
    end=(Math.Floor(clock/plan.PhraseSeconds)+2)*plan.PhraseSeconds;
   }
  }
  lastCounter=counter;
  var target=Active ? plan.ActiveGain : plan.IdleGain;
  if(plan.ReleaseCurve==AudioLayerReleaseCurve.SmoothDecibels && !Active)
  {
   if(seeked){Gain=plan.IdleGain;releaseStart=double.NaN;}
   else if(wasActive){releaseStart=enabled?priorEnd:clock;releaseGain=Gain;}
   if(!double.IsNaN(releaseStart))Gain=ReleaseGain(releaseGain,target,(clock-releaseStart)/plan.ReleaseSeconds);
  }
  else
  {
   releaseStart=double.NaN;
   var ramp=target>Gain?plan.AttackSeconds:plan.ReleaseSeconds;
   var delta=(float)(elapsed/ramp)*Math.Abs(plan.ActiveGain-plan.IdleGain);
   Gain=Gain<target?Math.Min(target,Gain+delta):Math.Max(target,Gain-delta);
  }
 }
 internal static float ReleaseGain(float from,float to,double progress)
 {
  if(from==to)return to;
  if(progress<=0)return from;if(progress>=1)return to;
  var eased=progress*progress*(3-2*progress);
  var a=Math.Max(from,.0001f);var b=Math.Max(to,.0001f);
  return (float)Math.Exp(Math.Log(a)+(Math.Log(b)-Math.Log(a))*eased);
 }
}

public sealed partial class AudioEventInstance
{
 sealed class ParentLayerState
 {
  internal AudioTrack Track;
  internal AudioInstrument Instrument;
  internal AudioEventInstance Child;
  internal AudioLayerPlayback Playback;
  internal float Counter;
 }
 readonly Dictionary<AudioInstrument,ParentLayerState> parentLayers=new();
 readonly Dictionary<Guid,float> parentLayerGains=new();
 readonly HashSet<Guid> blockedParentTails=new();
 /// <summary>Whether a direct active child has an enabled sustained role for this world fact. Does not start playback.</summary>
 public bool HasActiveMusicControl(string parameterName) => !_paused && parentLayers.Values.Any(state=>
  state.Instrument.LayerControl is {Mode:AudioMusicControlMode.SustainLayer} rule && rule.TriggerParameter?.Name==parameterName
  && rule.ActiveGain>rule.IdleGain && state.Child.sharedMusic is {IsRunning:true}
  && _voices.Any(v=>!v.Pending && v.Voice is SubEventVoice child && ReferenceEquals(child.Child,state.Child))
  && ParentControlEnabled(state.Track,state.Instrument) && state.Track.Volume>0 && !_auditionMutedTracks.Contains(state.Track) && !_auditionMutedTracks.Contains(_description.Resource.MasterTrack));
 bool ParentControlEnabled(AudioTrack track,AudioInstrument instrument) => CurrentTimelinePosition>=instrument.StartTime
  && (instrument.TrimEnd<=0 || CurrentTimelinePosition<instrument.StartTime+instrument.TrimEnd)
  && AudioEventEvaluator.Matches(instrument.Conditions,_parameters) && AudioEventEvaluator.Matches(track.Conditions,_parameters)
  && AudioEventEvaluator.Matches(_description.Resource.MasterTrack.Conditions,_parameters);
 void UpdateParentLayers()
 {
  foreach(var track in _description.Resource.Tracks.Prepend(_description.Resource.MasterTrack))
  foreach(var instrument in track.Instruments.Where(i=>i.LayerControl is not null))
  {
   var rule=instrument.LayerControl;
   var child=(_voices.FirstOrDefault(v=>!v.Pending && v.Instrument.Id==rule.SubEventInstrumentId)?.Voice as SubEventVoice)?.Child;
   if(child?.sharedMusic is not {IsRunning:true} || child._paused || rule.ChildEvent is not null && !ReferenceEquals(rule.ChildEvent,child._description.Resource))continue;
   // Child conditions and request diagnostics must see the same world update as the parent control.
   child.ReceiveParentParameters(this);
   var target=child._description.Resource.Tracks.Prepend(child._description.Resource.MasterTrack).FirstOrDefault(t=>t.Id==rule.ChildTrackId);
   if(target is null)continue; // Another eligible composition may have its own controller.
   var counter=_parameters[rule.TriggerParameter].Number;
   var clock=child.sharedMusic.Position/44100d;
   if(!parentLayers.TryGetValue(instrument,out var state) || !ReferenceEquals(state.Child,child))
   {
    state=new(){Track=track,Instrument=instrument,Child=child,
     Counter=counter,Playback=new(rule.Prepare(child._description.Resource.BeatGrids[0].SecondsPerBeat),clock,counter)};
    parentLayers[instrument]=state;
   }
   var enabled=ParentControlEnabled(track,instrument);
   var controlGain=_auditionMutedTracks.Contains(track)||_auditionMutedTracks.Contains(_description.Resource.MasterTrack)?0
    : (ReferenceEquals(track,_description.Resource.MasterTrack)?1:track.Volume*AudioEventEvaluator.EvaluateAutomations(track.Automations,AudioAutomationTarget.Volume,_parameters))
      *AudioEventEvaluator.EvaluateAutomations(instrument.Automations,AudioAutomationTarget.Volume,_parameters);
   if(rule.Mode==AudioMusicControlMode.TriggerPhraseBank)
   {
    if(child.parentLayerGains.GetValueOrDefault(target.Id,1f)!=controlGain)
    {child.parentLayerGains[target.Id]=controlGain;child.UpdateSharedMusicMix();}
    if(enabled && counter>state.Counter)child.RequestParentPhrase(target);
    state.Counter=counter;
    if(!enabled && child.overlayStates.TryGetValue(target,out var phraseState)){phraseState.Pending=false;phraseState.ChainCredits=0;}
    continue;
   }
   var tailBlocked=!enabled || controlGain==0;
   var tailChanged=tailBlocked?child.blockedParentTails.Add(target.Id):child.blockedParentTails.Remove(target.Id);
   state.Playback.Update(clock,counter,enabled);
   var releaseEcho=target.Instruments.Any(i=>i.Echo is {ReleaseOnly:true});
   var gain=controlGain*(releaseEcho&&!state.Playback.Active?0:state.Playback.Gain);
   if(tailChanged || child.parentLayerGains.GetValueOrDefault(target.Id,1f)!=gain)
   {child.parentLayerGains[target.Id]=gain;child.UpdateSharedMusicMix();}
  }
 }
 bool TryParentLayerLiveVoice(AudioTrack track,AudioInstrument instrument,out AudioLiveVoice voice)
 {
  voice=null;if(instrument.LayerControl is not {} rule)return false;
  parentLayers.TryGetValue(instrument,out var state);
  var active=state?.Child.sharedMusic is {IsRunning:true};
  var playback=active?state.Playback:null;
  var gain=playback?.Gain??0;
  if(active && rule.Mode==AudioMusicControlMode.TriggerPhraseBank)
  {
   var target=state.Child._description.Resource.Tracks.FirstOrDefault(t=>t.Id==rule.ChildTrackId);
   var rows=new List<AudioLiveVoice>();
   if(target is not null)foreach(var phrase in target.Instruments)state.Child.AddSharedOverlayLiveVoices(target,phrase,rows);
   var row=rows.OrderByDescending(v=>v.Gain).FirstOrDefault();
   voice=new(track.Name,row?.Source,row?.State??"Inactive",row?.Gain??0,1,row?.SourcePosition??0,instrument.StartTime,instrument.StartTime+Math.Max(1,instrument.TrimEnd),
    ChildInstanceId:state.Child.LiveId,ChildEvent:state.Child._description.Resource.ResourcePath,InstrumentId:instrument.Id,Reason:(row?.Reason??"Parent action control: waiting for a new successful service.")
     +(target is not null && state.Child.overlayStates.TryGetValue(target,out var decision) && decision.LastDecision is not null ? " Last request: "+decision.LastDecision : ""));
   return true;
  }
  var multiplier=gain;
  AudioLiveVoice sourceVoice=null;
  if(active)
  {
   var target=state.Child._description.Resource.Tracks.Prepend(state.Child._description.Resource.MasterTrack).FirstOrDefault(t=>t.Id==rule.ChildTrackId);
   foreach(var part in target?.Instruments ?? new())
    if(state.Child.TrySharedLiveVoice(target,part,out var row) && (sourceVoice is null || row.Gain>sourceVoice.Gain))sourceVoice=row;
  }
  gain=sourceVoice?.Gain??0;
  if(_auditionMutedTracks.Contains(track)||_auditionMutedTracks.Contains(_description.Resource.MasterTrack))gain=0;
  voice=new(track.Name,sourceVoice?.Source??state?.Child._description.Resource.ResourcePath,active?(_paused?"Paused":gain>0?"Playing":"Silent"):"Inactive",
   gain,1,active?state.Child.CurrentTimelinePosition:0,instrument.StartTime,instrument.StartTime+Math.Max(1,instrument.TrimEnd),
   ChildInstanceId:active?state.Child.LiveId:default,ChildEvent:active?state.Child._description.Resource.ResourcePath:null,InstrumentId:instrument.Id,
   Reason:playback is null?"Waiting for the selected child track.":$"{rule.TriggerParameter.Name}: {(playback.Active ? "sustain" : "rest")}; phrases {playback.Phrases}/{rule.MaximumPhrases}; next boundary {playback.NextBoundary:0.00}s. Continuous source position is unchanged. Control multiplier {multiplier:0.###}. Displayed gain is the largest target voice before the mixer, not measured output.");
  return true;
 }
}

public static partial class AudioEventValidator
{
 static void ValidateLayerControls(AudioEvent resource,string path,IReadOnlyDictionary<string,AudioParameter> parameters,AudioEventValidationResult result)
 {
  var tracks=(resource.Tracks??new()).Prepend(resource.MasterTrack).Where(t=>t is not null).ToArray();
  var instruments=tracks.SelectMany(t=>t.Instruments??new()).Where(i=>i is not null).ToArray();
  var targets=new List<AudioMusicLayerControl>();
  foreach(var instrument in instruments.Where(i=>i.LayerControl is not null))
  {
   var rule=instrument.LayerControl;
   if(resource.SharedMusicOutput || instrument.SourceId!=Guid.Empty || instrument.IsSubEvent || instrument.StartTriggerConditions.Count>0
    || tracks.Any(t=>t.Instruments.Contains(instrument)&&t.Overlay?.Enabled==true))result.Error(path,"Layer controls require a non-shared parent and no sound, sub-event, or start trigger.");
   if(rule.TriggerParameter is null || !parameters.Values.Contains(rule.TriggerParameter))result.Error(path,"Layer control trigger must be declared in the parent.");
   if(rule.PhraseBeats<1||rule.PhraseBeats>64||rule.MaximumPhrases<1||rule.MaximumPhrases>8||rule.RestBeats<1||rule.RestBeats>64)
    result.Error(path,"Layer phrase/rest beats must be 1 to 64 and the phrase limit 1 to 8.");
   if(!Enum.IsDefined(rule.ReleaseCurve))result.Error(path,"Unknown sustain release curve.");
   if(!float.IsFinite(rule.IdleGain)||!float.IsFinite(rule.ActiveGain)||rule.IdleGain<0||rule.ActiveGain<rule.IdleGain||rule.ActiveGain>4
    ||!float.IsFinite(rule.AttackSeconds)||!float.IsFinite(rule.ReleaseSeconds)||rule.AttackSeconds<.01f||rule.ReleaseSeconds<.01f||rule.AttackSeconds>5||rule.ReleaseSeconds>5)
    result.Error(path,"Layer gains require 0 <= idle <= active <= 4 and attack/release of 0.01 to 5 seconds.");
   var owner=instruments.FirstOrDefault(i=>i.Id==rule.SubEventInstrumentId&&i.IsSubEvent);
   var children=owner?.SubEvents.Select(c=>c.Event).Where(e=>e is not null).ToArray()??Array.Empty<AudioEvent>();
   if(rule.ChildEvent is not null && !children.Contains(rule.ChildEvent))result.Error(path,"Music control Child Event must be a direct choice of its sub-event instrument.");
   var found=children.Where(e=>e.SharedMusicOutput && (rule.ChildEvent is null || ReferenceEquals(e,rule.ChildEvent))).SelectMany(e=>(e.Tracks??new()).Prepend(e.MasterTrack)).Where(t=>t?.Id==rule.ChildTrackId).ToArray();
   if(!Enum.IsDefined(rule.Mode)||found.Length==0||found.Any(t=>t.Instruments.Count==0 || (rule.Mode==AudioMusicControlMode.SustainLayer ? t.Overlay?.Enabled==true : t.Overlay is not {Enabled:true,ParentControlled:true})))result.Error(path+"/"+instrument.Name,$"Music control {rule.Mode} target {rule.ChildTrackId:N} must be a continuous track or parent-controlled phrase bank in a direct shared-output child choice. Found {found.Length} tracks in {children.Length} choices.");
   if(targets.Any(other=>other.SubEventInstrumentId==rule.SubEventInstrumentId && other.ChildTrackId==rule.ChildTrackId && (other.ChildEvent is null || rule.ChildEvent is null || ReferenceEquals(other.ChildEvent,rule.ChildEvent))))result.Error(path,"Only one parent layer control may own a child track in the same child event.");
   targets.Add(rule);
  }
 }
}
