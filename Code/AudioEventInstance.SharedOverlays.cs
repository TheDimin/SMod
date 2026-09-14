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
 }
 readonly List<SharedOverlay> sharedOverlays=new();
 readonly Dictionary<string,long> sharedOverlayReady=new();
 AudioMusicLoopGrid sharedPhraseGrid;
 void AddSharedOverlayStates(List<AudioInstrumentPlaybackState> states)
 {
  foreach(var phrase in sharedOverlays)
  {
   var local=phrase.Sample.HasValue?(sharedMusic.Position-phrase.Sample.Value)/44100f:0;
   var pending=!phrase.Admitted || !phrase.Sample.HasValue || sharedMusic.Position<phrase.Sample.Value;
   var range=AudioMusicSourcePreparation.ResolveRange(phrase.Request.Instrument,phrase.Request.SoundTrack);
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
  var range=AudioMusicSourcePreparation.ResolveRange(instrument,phrase.Request.SoundTrack);
  var pending=!phrase.Admitted || !phrase.Sample.HasValue || sharedMusic.Position<phrase.Sample.Value;
  var envelope=pending||local>=range.Length?0:AudioInstrumentFade.Gain(phrase.Owner.Envelope,local,range.Length);
  var gain=phrase.Owner.Gain*_volume*envelope;
  var start=phrase.Sample.HasValue?(float)(phrase.Sample.Value/44100d+sharedTimelineOffset):CurrentTimelinePosition;
  voices.Add(new(track.Name,phrase.Request.SoundTrack.Sound?.ResourcePath,!phrase.Sample.HasValue?"Loading":pending?"Scheduled":_paused?"Paused":gain<=.0001f?"Silent":"Playing",
   gain,1,range.Offset+Math.Clamp(local,0,range.Length),start,start+range.Length,EventGain:phrase.Owner.Gain,InstanceGain:_volume,InstrumentGain:envelope,InstrumentId:instrument.Id,IsReleaseTail:!pending && phrase.BodyFrames<phrase.Frames && sharedMusic.Position>=phrase.Sample.Value+phrase.BodyFrames));
  }
  return found;
 }
 void StopSharedOverlays()
 {
  foreach(var phrase in sharedOverlays)phrase.Owner.Cancel();
  sharedOverlays.Clear();sharedOverlayReady.Clear();
 }
 bool SharedGroupBusy(string group)
 {
  var phrases=sharedOverlays.Where(p=>p.Request.Track.Overlay.VoiceGroup==group).ToArray();
  // One active body plus one retained tail. Pending preparation also occupies a slot.
  return phrases.Length>=2 || phrases.Any(p=>!p.Sample.HasValue || sharedMusic.Position<p.Sample.Value+p.BodyFrames);
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
   if(ended || withdrawn || phrase.Owner.IsCancelled){if(withdrawn)sharedOverlayReady.Remove(rule.VoiceGroup);phrase.Owner.Cancel();sharedOverlays.Remove(phrase);continue;}
   ApplySharedOverlayGain(phrase);
  }
  foreach(var pair in overlayStates)
  {
   var rule=pair.Key.Overlay;var state=pair.Value;
   if(rule.TriggerParameter is {} parameter)
   {
    var value=_parameters[parameter];
    if(!value.Equals(state.Last) && (rule.QueueWhilePlaying || !SharedGroupBusy(rule.VoiceGroup))
     && (!rule.IgnoreTriggersDuringCooldown || clock>=sharedOverlayReady.GetValueOrDefault(rule.VoiceGroup)))state.Pending=true;
    state.Last=value;
   }
   if(!OverlayMatches(rule.Conditions))state.Pending=false;
  }
  IReadOnlyList<AudioVoiceRequest> requests=null;
  foreach(var pair in overlayStates.OrderByDescending(p=>p.Value.Pending))
  {
   var track=pair.Key;var rule=track.Overlay;var state=pair.Value;
   if(SharedGroupBusy(rule.VoiceGroup) || clock<sharedOverlayReady.GetValueOrDefault(rule.VoiceGroup))continue;
   if(!OverlayMatches(rule.Conditions) || !string.IsNullOrEmpty(rule.BeatGridName)&&rule.BeatGridName!=grid.Name || Math.Abs(rule.BeatsPerMinute-grid.BeatsPerMinute)>.01)continue;
   if(!state.Pending && (rule.ActivityConditions?.Conditions is not {Count:>0} || !OverlayMatches(rule.ActivityConditions)))continue;
   var quantum=rule.QuantizationBeats>0?rule.QuantizationBeats:grid.BeatsPerBar;
   requests??=AudioEventEvaluator.BuildPlaybackPlan(_description.Resource,_parameters,SelectSoundTrack);
   var candidates=requests.Where(r=>r.Track==track).Select(r=>new {
    Request=r,Frame=sharedPhraseGrid.Next(clock+11025,quantum,(int)Math.Round(AudioMusicSourcePreparation.ResolveRange(r.Instrument,r.SoundTrack).Length*44100d),r.Instrument.OverlayBars)
   }).Where(c=>c.Frame.HasValue).ToArray();
   if(candidates.Length==0)continue;
   var earliest=candidates.Min(c=>c.Frame.Value);var choices=candidates.Where(c=>c.Frame.Value==earliest).Select(c=>c.Request).ToList();
   if(choices.Count>1)choices.RemoveAll(r=>r.Instrument==state.Previous);
   var request=choices[overlayRandom.Next(choices.Count)];
   var phrase=new SharedOverlay{Request=request,Triggered=state.Pending,Owner=new(request.Instrument),Frames=(int)Math.Round(AudioMusicSourcePreparation.ResolveRange(request.Instrument,request.SoundTrack).Length*44100d)};
   phrase.BodyFrames=request.Instrument.OverlayBodySeconds>0
    ? Math.Min(phrase.Frames,(int)Math.Round(request.Instrument.OverlayBodySeconds*44100d)) : phrase.Frames;
   ApplySharedOverlayGain(phrase);sharedOverlays.Add(phrase);state.Pending=false;
   _=StartSharedOverlay(phrase,sharedMusic,quantum,state,grid);
  }
 }
 void ApplySharedOverlayGain(SharedOverlay phrase)
 {
  var track=phrase.Request.Track;
  var current=AudioEventEvaluator.BuildPlaybackPlan(_description.Resource,_parameters,(instrument,eligible)=>
   ReferenceEquals(instrument,phrase.Request.Instrument)&&eligible.Contains(phrase.Request.SoundTrack)?phrase.Request.SoundTrack:eligible[0])
   .FirstOrDefault(r=>ReferenceEquals(r.Instrument,phrase.Request.Instrument)&&ReferenceEquals(r.SoundTrack,phrase.Request.SoundTrack));
  phrase.Owner.Gain=current is null || _auditionMutedTracks.Contains(track) || _auditionMutedTracks.Contains(_description.Resource.MasterTrack)
   ? 0 : Math.Clamp(AudioMusicEventBacking.Gain(current)*track.Overlay.Volume*_timelineControl.TransitionGain*HandoffGain(track),0,1);
 }
 void RejectSharedOverlay(SharedOverlay phrase,AudioMusicEventPlayback music,AudioBeatGrid grid)
 {
  phrase.Owner.Cancel();sharedOverlays.Remove(phrase);
  if(ReferenceEquals(sharedMusic,music) && music.IsRunning)
   sharedOverlayReady[phrase.Request.Track.Overlay.VoiceGroup]=music.Position+(long)Math.Round(grid.SecondsPerBeat*44100);
 }
 async Task StartSharedOverlay(SharedOverlay phrase,AudioMusicEventPlayback music,int quantum,OverlayState state,AudioBeatGrid grid)
 {
  try
  {
   var request=phrase.Request;
   var scheduled=await music.PrepareOwnedPhraseAsync(phrase.Owner,request.Instrument,request.SoundTrack,_description.Resource.MasterTrack.Tone,
    ReferenceEquals(request.Track,_description.Resource.MasterTrack)?null:request.Track.Tone,quantum,1);
   if(scheduled is null || !ReferenceEquals(sharedMusic,music) || phrase.Owner.IsCancelled){RejectSharedOverlay(phrase,music,grid);return;}
   phrase.Sample=scheduled.Sample;
   phrase.Admitted=await scheduled.Admission;
   if(!phrase.Admitted || phrase.Owner.IsCancelled || !ReferenceEquals(sharedMusic,music)){RejectSharedOverlay(phrase,music,grid);return;}
   state.Previous=request.Instrument;
   sharedOverlayReady[request.Track.Overlay.VoiceGroup]=scheduled.Sample+Math.Max(phrase.BodyFrames,(long)Math.Round(request.Track.Overlay.CooldownBeats*grid.SecondsPerBeat*44100));
  }
  catch(Exception error)
  {
   RejectSharedOverlay(phrase,music,grid);SharedMusicError=error.Message;
   Log.Warning($"Smod action phrase failed: {error.Message}");
  }
 }
}
