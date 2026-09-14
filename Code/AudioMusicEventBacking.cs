namespace Smod;

/// <summary>Maps one authored composition's continuous parts to fixed shared-session slots.
/// The caller supplies a grid selected by the event's music policy, never by gameplay.</summary>
internal sealed class AudioMusicEventBacking
{
 readonly AudioEvent resource;
 readonly AudioVoiceRequest[] slots;
 internal bool IncludesTimeline {get;}
 public IReadOnlyList<AudioMusicRuntimeSession.BackingPart> Parts {get;}
 public AudioMusicEventBacking(AudioEvent resource,AudioBeatGrid grid,IReadOnlyDictionary<AudioParameter,AudioParameterValue> parameters,bool includeTimeline=false)
 {
  ArgumentNullException.ThrowIfNull(resource);ArgumentNullException.ThrowIfNull(grid);
  if(!grid.IsValid)throw new ArgumentException("A valid composition grid is required.",nameof(grid));
  this.resource=resource;IncludesTimeline=includeTimeline;
  var duration=grid.End.Seconds-grid.Start.Seconds;
  slots=AudioEventEvaluator.BuildPlaybackPlan(resource,parameters,includeInactive:true)
   .Where(request=>!request.Instrument.IsSubEvent && request.Track.Overlay?.Enabled!=true
    && (includeTimeline
     ? request.StartTime>=grid.Start.Seconds && request.StartTime+AudioMusicSourcePreparation.ResolveRange(request.Instrument,request.SoundTrack).Length<=grid.End.Seconds+1d/44100
     : Math.Abs(request.StartTime-grid.Start.Seconds)<1d/44100
       && Math.Abs(AudioMusicSourcePreparation.ResolveRange(request.Instrument,request.SoundTrack).Length-duration)<1d/44100))
   .ToArray();
  if(slots.Length==0)throw new InvalidOperationException("The selected grid has no full-length continuous backing parts.");
  Parts=slots.Select(request=>new AudioMusicRuntimeSession.BackingPart(request.Instrument,request.SoundTrack,
   ReferenceEquals(request.Track,resource.MasterTrack)?null:request.Track.Tone,Gain(request))).ToArray();
 }
 internal static float Gain(AudioVoiceRequest request)
 {
  var sound=request.Instrument.Sound;
  // Changing speed destroys shared-clock alignment. Do not silently discard pitch authoring.
  if(request.Pitch!=1 || sound.Pitch.Min!=1 || sound.Pitch.Max!=1)
   throw new InvalidOperationException("Shared continuous backing requires fixed unit pitch.");
  if(sound.Volume.Min!=sound.Volume.Max)
   throw new InvalidOperationException("Shared continuous backing requires a fixed source gain; use track automation for variation.");
  var gain=request.Volume*sound.Volume.Min;
  if(!float.IsFinite(gain)||gain<0||gain>1)throw new InvalidOperationException("Shared backing gain must be within 0–1.");
  return gain;
 }
 public float[] EvaluateGains(IReadOnlyDictionary<AudioParameter,AudioParameterValue> parameters,AudioMusicMixState mix=null)
 {
  // Preserve the selected source even when the authoring offers random alternatives.
  var plan=AudioEventEvaluator.BuildPlaybackPlan(resource,parameters,(instrument,eligible)=>
  {
   var slot=slots.FirstOrDefault(item=>ReferenceEquals(item.Instrument,instrument));
   return slot is not null && eligible.Contains(slot.SoundTrack)?slot.SoundTrack:eligible[0];
  });
  return slots.Select(slot=>
  {
   var current=plan.FirstOrDefault(item=>ReferenceEquals(item.Instrument,slot.Instrument)
    && ReferenceEquals(item.SoundTrack,slot.SoundTrack));
   if(current is null)return 0;
   var gain=Gain(current);
   if(mix is not null)
   {
    var duration=AudioMusicSourcePreparation.ResolveRange(current.Instrument,current.SoundTrack).Length;
    gain*=mix.Gain(current.Track)*(IncludesTimeline?1:AudioInstrumentFade.Gain(current.Instrument,mix.Position-current.StartTime,duration));
   }
   return gain;
  }).ToArray();
 }
}
