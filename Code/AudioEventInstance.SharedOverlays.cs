using System.Threading.Tasks;
namespace Smod;

public sealed partial class AudioEventInstance
{
 sealed class SharedOverlay
 {
  internal AudioVoiceRequest Request;
  internal AudioMusicRequestOwner Owner;
  internal long? Sample;
  internal int Frames;
  internal int BodyFrames;
  internal bool Admitted;
  internal bool Triggered;
  internal long? ChainSample;
  internal string Reason;
 }
 readonly List<SharedOverlay> sharedOverlays=new();
 readonly Dictionary<string,long> sharedOverlayReady=new();
 readonly Dictionary<string,long> sharedChainRest=new();
 AudioMusicLoopGrid sharedPhraseGrid;
 (float Offset,float Length) SharedOverlayRange(AudioInstrument instrument,SoundEventTrack source)
  => instrument.CompoundHits is {Count:>0} hits
   ? (0,(float)hits.Max(hit=>hit.BeatOffset*60d/_description.Resource.BeatGrids[0].BeatsPerMinute+hit.Length))
   : AudioMusicSourcePreparation.ResolveRange(instrument,source);
 void AddSharedOverlayStates(List<AudioInstrumentPlaybackState> states)
 {
  foreach(var phrase in sharedOverlays)
  {
   var local=phrase.Sample.HasValue?(sharedMusic.Position-phrase.Sample.Value)/44100f:0;
   var pending=!phrase.Admitted || !phrase.Sample.HasValue || sharedMusic.Position<phrase.Sample.Value;
   var range=SharedOverlayRange(phrase.Request.Instrument,phrase.Request.SoundTrack);
   states.Add(new(phrase.Request.Track,phrase.Request.Instrument,!pending&&local<range.Length,pending,_paused,
    range.Offset+Math.Clamp(local,0,range.Length),phrase.Request.SoundTrack,phrase.Request.SoundTrack.Sound));
  }
 }
 bool AddSharedOverlayLiveVoices(AudioTrack track,AudioInstrument instrument,List<AudioLiveVoice> voices)
 {
  if(sharedMusic is null)return false;
  var found=false;
  foreach(var phrase in sharedOverlays)
  {
  if(phrase.Request.Instrument!=instrument)continue;
  found=true;
  var local=phrase.Sample.HasValue?(sharedMusic.Position-phrase.Sample.Value)/44100f:0;
  var range=SharedOverlayRange(instrument,phrase.Request.SoundTrack);
  var pending=!phrase.Admitted || !phrase.Sample.HasValue || sharedMusic.Position<phrase.Sample.Value;
  var envelope=pending||local>=range.Length?0:AudioInstrumentFade.Gain(phrase.Owner.Envelope,local,range.Length);
  var gain=phrase.Owner.Gain*_volume*envelope;
  var start=phrase.Sample.HasValue?(float)((phrase.Sample.Value+sharedTimelineOffsetSamples)/44100d):CurrentTimelinePosition;
  voices.Add(new(track.Name,phrase.Request.SoundTrack.Sound?.ResourcePath,!phrase.Sample.HasValue?"Loading":pending?"Scheduled":_paused?"Paused":gain<=.0001f?"Silent":"Playing",
   gain,1,range.Offset+Math.Clamp(local,0,range.Length),start,start+range.Length,EventGain:phrase.Owner.Gain,InstanceGain:_volume,InstrumentGain:envelope,InstrumentId:instrument.Id,IsReleaseTail:!pending && phrase.BodyFrames<phrase.Frames && sharedMusic.Position>=phrase.Sample.Value+phrase.BodyFrames,Reason:phrase.Reason));
  }
  return found;
 }
 void StopSharedOverlays()
 {
  foreach(var phrase in sharedOverlays)phrase.Owner.Cancel();
  sharedOverlays.Clear();sharedOverlayReady.Clear();sharedChainRest.Clear();
  foreach(var state in overlayStates.Values){state.ChainCount=state.ChainCredits=0;state.ChainEnd=state.ChainRestUntil=0;}
 }
 bool SharedGroupBusy(string group)
 {
  var phrases=sharedOverlays.Where(p=>p.Request.Track.Overlay.VoiceGroup==group).ToArray();
  // One active body plus one retained tail. Pending preparation also occupies a slot.
  return phrases.Length>=2 || phrases.Any(p=>!p.Sample.HasValue || sharedMusic.Position<p.Sample.Value+p.BodyFrames);
 }
 bool SharedActivityCanYield(string group)
 {
  var phrases=sharedOverlays.Where(p=>p.Request.Track.Overlay.VoiceGroup==group).ToArray();
  return phrases.Length>0 && phrases.All(p=>!p.Triggered && (!p.Sample.HasValue || sharedMusic.Position<p.Sample.Value));
 }
 void YieldSharedActivity(string group)
 {
  foreach(var phrase in sharedOverlays.Where(p=>p.Request.Track.Overlay.VoiceGroup==group).ToArray())
  {
   phrase.Owner.Cancel();sharedOverlays.Remove(phrase);
  }
  sharedOverlayReady.Remove(group);
 }
 void UpdateSharedOverlays()
 {
  if(sharedMusic is not {IsRunning:true} || _paused || _playbackState!=AudioPlaybackState.Playing || sharedSeek is not null && (!sharedSeek.IsCompletedSuccessfully || !sharedSeek.Result))return;
  var clock=sharedMusic.Position;var grid=_description.Resource.BeatGrids[0];
  foreach(var phrase in sharedOverlays.ToArray())
  {
   var rule=phrase.Request.Track.Overlay;
   var ended=phrase.Sample.HasValue && clock>=phrase.Sample.Value+phrase.Frames;
   var withdrawn=(!phrase.Sample.HasValue || clock<phrase.Sample.Value) && (!OverlayMatches(rule.Conditions)
    || !phrase.Triggered && !OverlayMatches(rule.ActivityConditions));
   if(ended || withdrawn || phrase.Owner.IsCancelled)
   {
    if(withdrawn)
    {
     sharedOverlayReady.Remove(rule.VoiceGroup);
     // A first load has no scheduled end. Give its cancelled chain a finite release
     // here, because its asynchronous completion no longer owns a live phrase.
     if(rule.MaximumChainPhrases>1 && overlayStates.TryGetValue(phrase.Request.Track,out var state))
     {
      state.ChainCredits=0;
      state.ChainRestUntil=Math.Max(state.ChainRestUntil,clock+(long)Math.Round(rule.ChainRestBeats*grid.SecondsPerBeat*44100));
      sharedChainRest[rule.VoiceGroup]=state.ChainRestUntil;
     }
    }
    phrase.Owner.Cancel();sharedOverlays.Remove(phrase);continue;
   }
   ApplySharedOverlayGain(phrase);
  }
  foreach(var pair in overlayStates)
  {
   var rule=pair.Key.Overlay;var state=pair.Value;
   if(state.ChainCount>0 && clock>=state.ChainRestUntil && state.ChainRestUntil>0)
   {state.ChainCount=state.ChainCredits=0;state.ChainEnd=state.ChainRestUntil=0;}
   if(!rule.ParentControlled && rule.TriggerParameter is {} parameter)
   {
    var value=_parameters[parameter];
    var activityCanYield=SharedActivityCanYield(rule.VoiceGroup);
    if(rule.MaximumChainPhrases>1)
    {
     // Coalesce bursts into a bounded number of extensions. Never bank work during rest.
     if(value.Number>state.Last.Number && OverlayMatches(rule.Conditions))
     {
      if(state.ChainCount>0 && (state.ChainEnd==0 || clock<state.ChainEnd))
       state.ChainCredits=Math.Min(rule.MaximumChainPhrases-state.ChainCount,state.ChainCredits+1);
      else if(state.ChainCount==0 && clock>=sharedChainRest.GetValueOrDefault(rule.VoiceGroup)
       && (!SharedGroupBusy(rule.VoiceGroup) || activityCanYield))state.Pending=true;
     }
    }
    else if(!value.Equals(state.Last) && (rule.QueueWhilePlaying || !SharedGroupBusy(rule.VoiceGroup) || activityCanYield)
     && (!rule.IgnoreTriggersDuringCooldown || clock>=sharedOverlayReady.GetValueOrDefault(rule.VoiceGroup) || activityCanYield))state.Pending=true;
    state.Last=value;
   }
   if(!OverlayMatches(rule.Conditions)){state.Pending=false;state.ChainCredits=0;}
  }
  IReadOnlyList<AudioVoiceRequest> requests=null;
  foreach(var pair in overlayStates.OrderByDescending(p=>p.Value.Pending || p.Value.ChainCredits>0))
  {
   var track=pair.Key;var rule=track.Overlay;var state=pair.Value;
   var leadSamples=(int)Math.Round(rule.SchedulingLeadMilliseconds*44.1);
   var extending=rule.MaximumChainPhrases>1 && state.ChainCredits>0;
   long? chainSample=null;
   if(extending)
   {
    var own=sharedOverlays.Where(p=>p.Request.Track==track).OrderByDescending(p=>p.Sample ?? long.MaxValue).ToArray();
    if(own.Length!=1 || !own[0].Admitted || !own[0].Sample.HasValue)continue;
    chainSample=own[0].Sample.Value+own[0].BodyFrames;
    if(clock+leadSamples>chainSample){state.ChainCredits=0;continue;}
   }
   if(!extending && clock<sharedChainRest.GetValueOrDefault(rule.VoiceGroup))continue;
   var replaceActivity=state.Pending && SharedActivityCanYield(rule.VoiceGroup);
   if(!extending && !replaceActivity && (SharedGroupBusy(rule.VoiceGroup) || clock<sharedOverlayReady.GetValueOrDefault(rule.VoiceGroup)))continue;
   if(!OverlayMatches(rule.Conditions) || !string.IsNullOrEmpty(rule.BeatGridName)&&rule.BeatGridName!=grid.Name || Math.Abs(rule.BeatsPerMinute-grid.BeatsPerMinute)>.01)continue;
   if(!extending && !state.Pending && (rule.ParentControlled || rule.ActivityConditions?.Conditions is not {Count:>0} || !OverlayMatches(rule.ActivityConditions)))continue;
   var quantum=rule.QuantizationBeats>0?rule.QuantizationBeats:grid.BeatsPerBar;
   requests??=AudioEventEvaluator.BuildPlaybackPlan(_description.Resource,_parameters,SelectSoundTrack);
   var candidates=requests.Where(r=>r.Track==track).Select(r=>new {
    Request=r,Frame=sharedPhraseGrid.Next(chainSample ?? clock+leadSamples,quantum,(int)Math.Round(SharedOverlayRange(r.Instrument,r.SoundTrack).Length*44100d),r.Instrument.OverlayBars,r.Instrument.AllowReleaseTailAcrossLoop?(int)Math.Round(r.Instrument.OverlayBodySeconds*44100):0)
   }).Where(c=>c.Frame.HasValue && (!chainSample.HasValue || c.Frame==chainSample) && (chainSample.HasValue || c.Request.Instrument.MaximumMusicWaitBeats==0
    || c.Frame.Value-clock<=c.Request.Instrument.MaximumMusicWaitBeats*grid.SecondsPerBeat*44100d)).ToArray();
   // Do not retain an action until a distant permitted bar. Activity can try again;
   // an action requires a new counter change. A rejected lead must not occupy a group.
   if(candidates.Length==0){state.LastDecision="Rejected: no complete compatible phrase can enter within its wait limit or required chain boundary.";state.Pending=false;state.ChainCredits=0;continue;}
   var earliest=candidates.Min(c=>c.Frame.Value);var choices=candidates.Where(c=>c.Frame.Value==earliest).Select(c=>c.Request).ToList();
   if(choices.Count>1)choices.RemoveAll(r=>r.Instrument==state.Previous);
   // Replace only after a valid, timely action phrase is found. Never cut an audible set.
   if(replaceActivity)YieldSharedActivity(rule.VoiceGroup);
   var request=choices[overlayRandom.Next(choices.Count)];
   var phrase=new SharedOverlay{Request=request,Triggered=state.Pending || extending,ChainSample=chainSample,Owner=new(request.Instrument),Frames=(int)Math.Round(SharedOverlayRange(request.Instrument,request.SoundTrack).Length*44100d)};
   if(rule.MaximumChainPhrases>1)
   {state.ChainCount++;if(extending)state.ChainCredits--;}
   phrase.Reason=extending ? $"Success chain {state.ChainCount}/{rule.MaximumChainPhrases}; continues at the prior phrase body end."
    : state.Pending && rule.TriggerParameter is {} trigger
    ? $"{trigger.Name} changed to {_parameters[trigger].Number}; entry spacing {quantum} beats."
    : "Activity: "+string.Join(", ",rule.ActivityConditions.Conditions.Select(c=>$"{c.Parameter.Name}={_parameters[c.Parameter].Number}"));
   phrase.BodyFrames=request.Instrument.OverlayBodySeconds>0
    ? Math.Min(phrase.Frames,(int)Math.Round(request.Instrument.OverlayBodySeconds*44100d)) : phrase.Frames;
   state.LastDecision="Accepted: "+phrase.Reason;
   ApplySharedOverlayGain(phrase);sharedOverlays.Add(phrase);state.Pending=false;
   _=StartSharedOverlay(phrase,sharedMusic,quantum,state,grid);
  }
 }
 void RequestParentPhrase(AudioTrack track)
 {
  if(sharedMusic is not {IsRunning:true} || !overlayStates.TryGetValue(track,out var state) || track.Overlay is not {ParentControlled:true} rule)return;
  var clock=sharedMusic.Position;
  if(!OverlayMatches(rule.Conditions)){state.LastDecision="Rejected: child world conditions do not match.";return;}
  if(state.ChainCount>0 && (state.ChainEnd==0 || clock<state.ChainEnd))
  {
   state.ChainCredits=Math.Min(rule.MaximumChainPhrases-state.ChainCount,state.ChainCredits+1);
   state.LastDecision=state.ChainCredits>0 ? "Pending: extend at the current phrase end." : "Rejected: the phrase chain is at its limit.";
  }
  else if(clock<sharedChainRest.GetValueOrDefault(rule.VoiceGroup) || state.ChainCount>0)
   state.LastDecision="Rejected: the lead is in its rest period. This request is not queued.";
  else if(SharedGroupBusy(rule.VoiceGroup))state.LastDecision="Rejected: another phrase occupies this voice group. This request is not queued.";
  else {state.Pending=true;state.LastDecision="Pending: find a compatible entry.";}
  UpdateSharedOverlays();
 }
 void ApplySharedOverlayGain(SharedOverlay phrase)
 {
  var track=phrase.Request.Track;
  var current=AudioEventEvaluator.EvaluateSelectedVoice(_description.Resource,phrase.Request,_parameters);
  phrase.Owner.Gain=!current.Active || _auditionMutedTracks.Contains(track) || _auditionMutedTracks.Contains(_description.Resource.MasterTrack)
   ? 0 : AudioMusicEventBacking.Gain(phrase.Request.SourceData,current.Volume,current.Pitch)*track.Overlay.Volume*_timelineControl.TransitionGain*HandoffGain(track)*parentLayerGains.GetValueOrDefault(track.Id,1f);
 }
 void RejectSharedOverlay(SharedOverlay phrase,AudioMusicEventPlayback music,AudioBeatGrid grid)
 {
  phrase.Owner.Cancel();
  // A withdrawn or replaced request no longer owns the group's retry delay.
  var wasCurrent=sharedOverlays.Remove(phrase);
  if(wasCurrent && ReferenceEquals(sharedMusic,music) && music.IsRunning)
  {
   sharedOverlayReady[phrase.Request.Track.Overlay.VoiceGroup]=music.Position+(long)Math.Round(grid.SecondsPerBeat*44100);
   if(overlayStates.TryGetValue(phrase.Request.Track,out var state) && phrase.Request.Track.Overlay.MaximumChainPhrases>1)
   {
    state.ChainCredits=0;
    state.ChainRestUntil=Math.Max(state.ChainRestUntil,music.Position+(long)Math.Round(phrase.Request.Track.Overlay.ChainRestBeats*grid.SecondsPerBeat*44100));
    sharedChainRest[phrase.Request.Track.Overlay.VoiceGroup]=state.ChainRestUntil;
   }
  }
 }
 async Task StartSharedOverlay(SharedOverlay phrase,AudioMusicEventPlayback music,double quantum,OverlayState state,AudioBeatGrid grid)
 {
  try
  {
   var request=phrase.Request;
   var scheduled=await music.PrepareOwnedPhraseAsync(phrase.Owner,request.Instrument,request.SoundTrack,_description.Resource.MasterTrack.Tone,
    ReferenceEquals(request.Track,_description.Resource.MasterTrack)?null:request.Track.Tone,quantum,1,phrase.ChainSample,(int)Math.Round(request.Track.Overlay.SchedulingLeadMilliseconds*44.1));
   if(scheduled is null || !ReferenceEquals(sharedMusic,music) || phrase.Owner.IsCancelled){RejectSharedOverlay(phrase,music,grid);return;}
   phrase.Sample=scheduled.Sample;
   phrase.Admitted=await scheduled.Admission;
   if(!phrase.Admitted || phrase.Owner.IsCancelled || !ReferenceEquals(sharedMusic,music)){RejectSharedOverlay(phrase,music,grid);return;}
   if(request.Track.Overlay.MaximumChainPhrases>1)
   {
    state.ChainEnd=scheduled.Sample+phrase.Frames;
    state.ChainRestUntil=state.ChainEnd+(long)Math.Round(request.Track.Overlay.ChainRestBeats*grid.SecondsPerBeat*44100);
    sharedChainRest[request.Track.Overlay.VoiceGroup]=state.ChainRestUntil;
    if(state.ChainCount>=request.Track.Overlay.MaximumChainPhrases)state.ChainCredits=0;
   }
   state.Previous=request.Instrument;
   sharedOverlayReady[request.Track.Overlay.VoiceGroup]=request.Track.Overlay.MaximumChainPhrases>1 ? state.ChainRestUntil : scheduled.Sample+Math.Max(phrase.BodyFrames,(long)Math.Round(request.Track.Overlay.CooldownBeats*grid.SecondsPerBeat*44100));
  }
  catch(Exception error)
  {
   RejectSharedOverlay(phrase,music,grid);SharedMusicError=error.Message;
   Log.Warning($"Smod action phrase failed: {error.Message}");
  }
 }
}
