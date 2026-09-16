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
  => Gain(request.SourceData,request.Volume,request.Pitch);
 internal static float Gain(AudioSourceDefinition sound,float volume,float pitch)
 {
  ArgumentNullException.ThrowIfNull(sound);
  // Changing speed destroys shared-clock alignment. Do not silently discard pitch authoring.
  if(pitch!=1 || sound.Pitch.Min!=1 || sound.Pitch.Max!=1)
   throw new InvalidOperationException("Shared continuous backing requires fixed unit pitch.");
  if(sound.Volume.Min!=sound.Volume.Max)
   throw new InvalidOperationException("Shared continuous backing requires a fixed source gain; use track automation for variation.");
  var gain=volume*sound.Volume.Min;
  if(!float.IsFinite(gain)||gain<0)throw new InvalidOperationException("Music track gain must be finite and zero or greater.");
  return gain;
 }
 public bool[] EvaluateTailPermissions(IReadOnlyDictionary<AudioParameter,AudioParameterValue> parameters,AudioMusicMixState mix)
  =>slots.Select(slot=>AudioEventEvaluator.EvaluateSelectedVoice(resource,slot,parameters).Active && (mix?.TailAllowed(slot.Track)??true)).ToArray();
 public float[] EvaluateGains(IReadOnlyDictionary<AudioParameter,AudioParameterValue> parameters,AudioMusicMixState mix=null)
 {
  var gains=new float[slots.Length];
  for(var index=0;index<slots.Length;index++)
  {
   var slot=slots[index];
   var current=AudioEventEvaluator.EvaluateSelectedVoice(resource,slot,parameters);
   if(!current.Active)continue;
   var gain=Gain(slot.SourceData,current.Volume,current.Pitch);
   if(mix is not null)
   {
    var duration=AudioMusicSourcePreparation.ResolveRange(slot.Instrument,slot.SoundTrack).Length;
    gain*=mix.Gain(slot.Track)*(IncludesTimeline?1:AudioInstrumentFade.Gain(slot.Instrument,mix.Position-slot.StartTime,duration));
   }
   gains[index]=gain;
  }
  return gains;
 }
}
