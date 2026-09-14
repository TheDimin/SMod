using System.Threading.Tasks;
namespace Smod;

public sealed partial class AudioEventInstance
{
 AudioMusicEventPlayback sharedMusic;
 double sharedTimelineOffset;
 Task<bool> sharedSeek;
 internal Func<IAudioMusicEventSession> SharedMusicSessionFactory;
 public string SharedMusicError {get;private set;}
 internal bool HasSharedMusicOutput=>sharedMusic is not null;
 float SharedMusicPosition=>sharedMusic.IsRunning && (sharedSeek is null || sharedSeek.IsCompletedSuccessfully && sharedSeek.Result)
  ? (float)(sharedMusic.Position/44100d+sharedTimelineOffset) : _timelinePosition;


 void AddSharedMusicStates(List<AudioInstrumentPlaybackState> states)
 {
  if(sharedMusic is null)return;
  AddSharedOverlayStates(states);
  foreach(var part in sharedMusic.BackingParts)
  {
   var track=_description.Resource.Tracks.Prepend(_description.Resource.MasterTrack).First(t=>t.Instruments.Contains(part.Instrument));
   var range=AudioMusicSourcePreparation.ResolveRange(part.Instrument,part.Source);
   var local=CurrentTimelinePosition-part.Instrument.StartTime;
   states.Add(new(track,part.Instrument,sharedMusic.IsRunning && local>=0 && local<range.Length,!sharedMusic.IsRunning || local<0,_paused,
    (float)(range.Offset+Math.Clamp(local,0,range.Length)),part.Source,part.Source.Sound));
  }
 }
 bool TrySharedLiveVoice(AudioTrack track,AudioInstrument instrument,out AudioLiveVoice voice)
 {
  voice=null;if(sharedMusic is null)return false;
  var index=-1;for(var i=0;i<sharedMusic.BackingParts.Count;i++)if(ReferenceEquals(sharedMusic.BackingParts[i].Instrument,instrument)){index=i;break;}
  if(index<0)return false;
  var part=sharedMusic.BackingParts[index];var range=AudioMusicSourcePreparation.ResolveRange(instrument,part.Source);
  var gain=index<sharedMusic.BackingGains.Count?sharedMusic.BackingGains[index]:0;
  var local=CurrentTimelinePosition-instrument.StartTime;
  var envelope=local<0 || local>=range.Length?0:AudioInstrumentFade.Gain(instrument,local,(float)range.Length);
  voice=new(track.Name,part.Source.Sound?.ResourcePath,!sharedMusic.IsRunning?"Loading":local<0?"Scheduled":local>=range.Length?"Stopped":_paused?"Paused":gain*_volume*envelope<=.0001f?"Silent":"Playing",
   gain*_volume*envelope,1,(float)(range.Offset+Math.Clamp(local,0,range.Length)),instrument.StartTime,instrument.StartTime+(float)range.Length,
   NativeGain:instrument.Sound.Volume.Min,EventGain:gain,InstanceGain:_volume,InstrumentGain:envelope);
  return true;
 }
 void StartSharedMusic()
 {
  var resource=_description.Resource;
  if(_pitch!=1 || _hasAttributes3D || _volume>1)throw new InvalidOperationException("Shared music requires local playback at unit pitch.");
  var grid=AudioSharedMusicDefinition.RequireGrid(resource);
  AudioSharedMusicDefinition.RequireBacking(resource,grid,_parameters);
  sharedPhraseGrid=new(grid,(long)Math.Round((grid.End.Seconds-grid.Start.Seconds)*44100));
  sharedTimelineOffset=0;sharedSeek=null;SharedMusicError=null;
  var owner=new AudioMusicEventPlayback(resource,SharedMusicSessionFactory){Paused=_paused,Volume=Math.Clamp(_volume,0,1)};
  sharedMusic=owner;owner.UpdateMix(CaptureSharedMusicMix());
  _=CompleteSharedMusicStart(owner,grid);
 }
 async Task CompleteSharedMusicStart(AudioMusicEventPlayback owner,AudioBeatGrid grid)
 {
  try
  {
   var started=await owner.StartAsync(grid,_parameters,(long)Math.Round(_timelinePosition*44100d));
   if(!ReferenceEquals(sharedMusic,owner) || _playbackState is AudioPlaybackState.Stopping or AudioPlaybackState.Stopped)return;
   if(!started)throw new InvalidOperationException("Shared music preparation did not complete.");
   _playbackState=AudioPlaybackState.Playing;
  }
  catch(Exception error)
  {
   owner.Dispose();
   if(!ReferenceEquals(sharedMusic,owner))return;
   SharedMusicError=error.Message;sharedMusic=null;_playbackState=AudioPlaybackState.Stopped;
   Log.Warning($"Smod shared music failed: {error.Message}");
  }
 }
 void SeekSharedMusic(float position)
 {
  StopSharedOverlays();
  foreach(var state in overlayStates.Values)state.Pending=false;
  sharedTimelineOffset=0;
  sharedSeek=sharedMusic.Seek((long)Math.Round(position*44100d));
 }
 void UpdateSharedMusicMix()
 {
  if(sharedMusic is null || _playbackState==AudioPlaybackState.Stopping)return;
  sharedMusic.Volume=Math.Clamp(_volume,0,1);
  sharedMusic.UpdateMix(CaptureSharedMusicMix());
  foreach(var phrase in sharedOverlays)ApplySharedOverlayGain(phrase);
 }
 void StopSharedMusic(float fade)
 {
  var owner=sharedMusic;_timelinePosition=CurrentTimelinePosition;
  CancelMusicRequests();_playbackCancellation?.Cancel();
  if(fade<=0)StopOverlay();else foreach(var state in overlayStates.Values)state.Pending=false;
  if(fade<=0){owner.Dispose();sharedMusic=null;_playbackState=AudioPlaybackState.Stopped;return;}
  _playbackState=AudioPlaybackState.Stopping;
  _=CompleteSharedMusicStop(owner,fade);
 }
 async Task CompleteSharedMusicStop(AudioMusicEventPlayback owner,float fade)
 {
  try {await owner.StopAsync(fade);}
  catch(Exception error){SharedMusicError=error.Message;Log.Warning($"Smod shared music stop failed: {error.Message}");}
  finally
  {
   owner.Dispose();
   if(ReferenceEquals(sharedMusic,owner)){StopOverlay();sharedMusic=null;_playbackState=AudioPlaybackState.Stopped;}
  }
 }
}

internal static class AudioSharedMusicDefinition
{
 internal static AudioBeatGrid RequireGrid(AudioEvent resource)
 {
  var grid=resource.BeatGrids.Count==1 ? resource.BeatGrids[0] : null;
  if(grid is null || !grid.IsValid || grid.Start.Seconds!=0 || Math.Abs(grid.End.Seconds-resource.TimelineDuration)>.0001
   || resource.TransitionMarkers.Count!=0 || resource.TransitionRegions.Count!=0
   || resource.LoopRegions.Count!=1 || resource.LoopRegions[0].Start.Seconds!=0
   || Math.Abs(resource.LoopRegions[0].End.Seconds-grid.End.Seconds)>.0001
   || resource.LoopRegions[0].EndMode!=LoopEndMode.Fixed || resource.LoopRegions[0].Conditions.Conditions.Count!=0)
   throw new InvalidOperationException("Shared music requires one full-song grid and an unconditional full-song loop; timeline transitions belong in the parent.");
  return grid;
 }
 internal static void RequireBacking(AudioEvent resource,AudioBeatGrid grid,IReadOnlyDictionary<AudioParameter,AudioParameterValue> parameters)
 {
  // Reject unsupported normal instruments instead of dropping them from this output.
  var backing=new AudioMusicEventBacking(resource,grid,parameters,includeTimeline:true);
  var instruments=resource.Tracks.Prepend(resource.MasterTrack).Where(t=>t.Overlay?.Enabled!=true).SelectMany(t=>t.Instruments).ToArray();
  if(instruments.Length!=backing.Parts.Count || instruments.Any(i=>!backing.Parts.Any(p=>ReferenceEquals(p.Instrument,i))))
   throw new InvalidOperationException("Every timeline instrument must fit within the song grid. Nested events belong in the parent.");
  foreach(var request in AudioEventEvaluator.BuildPlaybackPlan(resource,parameters,includeInactive:true).Where(r=>r.Track.Overlay?.Enabled==true))
  {
   var gain=AudioMusicEventBacking.Gain(request)*request.Track.Overlay.Volume;
   if(gain>1)throw new InvalidOperationException("Shared action phrase gain must be within 0-1.");
  }
 }
}
