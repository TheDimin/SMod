namespace Smod;

/// <summary>Detached, read-only values. Voice gain excludes mixer and spatial attenuation.</summary>
public sealed record AudioLiveVoice(string Track, string Source, string State, float Gain, float Pitch,
 float SourcePosition, float Start, float End, Guid ChildInstanceId = default, string ChildEvent = null, float NativeGain = 1, float EventGain = 1, float InstanceGain = 1, float InstrumentGain = 1, float MusicGain = 1, float HandoffGain = 1, Guid InstrumentId = default, bool IsReleaseTail = false);
public sealed record AudioLiveParameter(string Name, string Value);
public sealed record AudioLiveSnapshot(Guid Id, string Resource, string State, float Position, float Volume,
 string Section, string PendingSection, float Variation, double GridStart, double GridEnd, float Bpm,
 IReadOnlyList<AudioLiveParameter> Parameters, IReadOnlyList<AudioLiveVoice> Voices, Guid ParentInstanceId = default, float? PendingInSeconds = null, string Handoff = "None", int BeatsPerBar = 4);

/// <summary>Main-thread observer. Weak references do not keep abandoned playback instances alive.</summary>
public static class AudioLiveDiagnostics
{
 private static readonly List<WeakReference<AudioEventInstance>> Instances = new();
 internal static AudioEventInstance FindForAudition(Guid id)
 {
  foreach(var reference in Instances)
   if(reference.TryGetTarget(out var instance) && instance.LiveId==id && instance.IsValid && instance.CaptureLiveSnapshot() is not null)return instance;
  return null;
 }

 internal static void Register(AudioEventInstance instance)
 {
  if(instance.LiveRegistered)return;
  instance.LiveRegistered=true;
  // Prune during creation too, so a closed monitor cannot leave an unbounded registry.
  Instances.RemoveAll(x=>!x.TryGetTarget(out _));
  Instances.Add(new(instance));
 }
 public static IReadOnlyList<AudioLiveSnapshot> CaptureActive()
 {
  var result=new List<AudioLiveSnapshot>();
  for(var index=Instances.Count-1;index>=0;index--)
  {
   if(!Instances[index].TryGetTarget(out var instance)) {Instances.RemoveAt(index);continue;}
   var snapshot=instance.CaptureLiveSnapshot();
   if(snapshot is not null)result.Add(snapshot);
  }
  return result;
 }
}

public sealed partial class AudioEventInstance
{
 internal bool LiveRegistered;
 private Guid liveId=Guid.NewGuid();
 internal AudioLiveSnapshot CaptureLiveSnapshot()
 {
  // Do not call RefreshPlaybackState: opening a monitor must not drive the timeline.
  if(_playbackState==AudioPlaybackState.Stopped)return null;
  // A released parent's cached state can remain Stopping after all child fades finish.
  // Observe descendants without calling PlaybackState, which drives their timelines.
  if(_playbackState==AudioPlaybackState.Stopping && sharedMusic is null
   && _voices.All(x=>!x.Pending && (x.Voice is SubEventVoice child
    ? child.Child.CaptureLiveSnapshot() is null : !x.Voice.IsPlaying))
   && !overlayVoices.Any(x=>x.Handle.IsPlaying))return null;

  // A retained process can own shared-music requests after its native cue ends.
  // Keep observing that lifetime without refreshing or starting playback here.
  if(!_description.Resource.SustainUntilStopped && _voices.Count>0
   && _voices.All(x=>!x.Pending && !x.Voice.IsPlaying)
   && !overlayVoices.Any(x=>x.Handle.IsPlaying))return null;
  if(liveId==Guid.Empty)liveId=Guid.NewGuid();
  var resource=_description.Resource;
  var position=CurrentTimelinePosition;
  var grid=resource.BeatGridAt(position);
  var voices=new List<AudioLiveVoice>();
  foreach(var track in resource.Tracks.Prepend(resource.MasterTrack))
  foreach(var instrument in track.Instruments)
  {
   void Add(AudioLiveVoice value)=>voices.Add(value with {InstrumentId=instrument.Id});
   if(AddSharedOverlayLiveVoices(track,instrument,voices))continue;
   if(TrySharedLiveVoice(track,instrument,out var sharedVoice)){Add(sharedVoice);continue;}
   var overlay=overlayVoices.FirstOrDefault(x=>x.Request.Instrument==instrument);
   if(track.Overlay?.Enabled==true && overlay is not null)
   {
    var handle=overlay.Handle;
    var elapsed=overlayClock-overlay.Started;var remaining=instrument.TrimEnd-instrument.TrimStart-elapsed;
    var envelope=(float)(Math.Clamp(elapsed/Math.Max(.005,instrument.FadeInDuration),0,1)*Math.Clamp(remaining/Math.Max(.005,instrument.FadeOutDuration),0,1));
    Add(new(track.Name,instrument.Sound?.ResourcePath,handle.Paused ? "Paused" : handle.Volume<=.0001f ? "Silent" : "Playing",handle.Volume,handle.Pitch,handle.Time,overlay.Position,overlay.Position+instrument.TrimEnd-instrument.TrimStart,NativeGain:overlay.NativeVolume,EventGain:overlay.Request.Volume*track.Overlay.Volume,InstanceGain:_volume,InstrumentGain:envelope,HandoffGain:HandoffGain(track)));
    continue;
   }
   var voice=_voices.FirstOrDefault(x=>ReferenceEquals(x.Instrument,instrument));
   if ( voice?.Voice is SubEventVoice child )
   {
    var childSnapshot = child.Child.CaptureLiveSnapshot();
    var childGain = childSnapshot?.Voices.Where( item => item.State is "Playing" or "Silent" ).Select( item => item.Gain ).DefaultIfEmpty().Max() ?? 0f;
    Add( new( track.Name, child.Choice.Event.ResourcePath, voice.Pending ? "Scheduled" : childSnapshot?.State ?? "Stopped",
     childGain, child.Pitch, child.Time, instrument.StartTime, instrument.StartTime + Math.Max( 0, instrument.TrimEnd - instrument.TrimStart ),
     child.Child.LiveId, child.Choice.Event.ResourcePath ) );
    continue;
   }
   var state=voice is null ? "Inactive" : voice.Pending ? "Scheduled" : !voice.Voice.IsPlaying ? "Stopped" : voice.Voice.Paused ? "Paused" : voice.Voice.Volume<=.0001f ? "Silent" : "Playing";
   var length=instrument.TrimEnd>0 ? instrument.TrimEnd-instrument.TrimStart : voice?.SoundTrack.Length ?? 0;
   var source=voice?.Voice.SelectedSound?.ResourcePath ?? voice?.SoundTrack.Sound?.ResourcePath ?? instrument.Sound?.ResourcePath ?? "No source";
   Add(new(track.Name ?? "Master",source,state,voice?.Voice.Volume ?? 0,voice?.Voice.Pitch ?? 1,
    voice?.Voice.Time ?? 0,instrument.StartTime,instrument.StartTime+Math.Max(0,length),
    NativeGain:voice?.NativeVolume ?? 0,EventGain:voice?.EventVolume ?? 0,InstanceGain:_volume,InstrumentGain:voice is null ? 0 : InstrumentFadeGain(voice),MusicGain:_music?.Gain(track,position) ?? 1,HandoffGain:HandoffGain(track)));
  }
  return new(liveId,resource.ResourcePath ?? resource.ResourceName ?? "Unsaved event",_paused ? "Paused" : _playbackState.ToString(),position,_volume,
   CurrentMusicSection,PendingMusicSection,MusicVariation,grid?.Start.Seconds ?? 0,grid?.End.Seconds ?? resource.TimelineDuration,grid?.BeatsPerMinute ?? 0,
   _parameters.Select(x=>new AudioLiveParameter(x.Key.Name,x.Value.IsLabel ? x.Value.Label : x.Value.Number.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture))).ToArray(),voices.ToArray(),ParentLiveId,
   _pendingSubEvents.Count>0 ? Math.Max(0,_pendingSubEvents.Values.Min(p=>p.ParentPosition)-position) : null,
   handoffExitSeconds>0 && position>=handoffExitEnd-handoffExitSeconds ? "Exit" : handoffEntrySeconds>0 && position<handoffEntrySeconds*2 ? "Entry" : "None", grid?.BeatsPerBar ?? 4);
 }
}
