namespace Smod;

/// <summary>One fade curve implementation for native voices and shared-clock backing.</summary>
internal static class AudioInstrumentFade
{
 internal static float Gain(AudioInstrument instrument,float localPosition,float duration,bool pending=false)
 {
  if(pending)return instrument.FadeInDuration>0 ? 0 : 1;
  localPosition=MathF.Max(0,localPosition);var gain=1f;
  if(instrument.FadeInDuration>0)
   gain=MathF.Min(gain,Curve(0,instrument.FadeInCurveMidpoint,1,Math.Clamp(localPosition/instrument.FadeInDuration,0,1)));
  if(instrument.FadeOutDuration>0 && float.IsFinite(duration))
  {
   var remaining=MathF.Max(0,MathF.Max(0,duration)-localPosition);
   gain=MathF.Min(gain,Curve(1,instrument.FadeOutCurveMidpoint,0,Math.Clamp(1-remaining/instrument.FadeOutDuration,0,1)));
  }
  return Math.Clamp(gain,0,1);
 }
 private static float Curve(float start,float midpoint,float end,float progress)
 {
  var inverse=1-progress;
  return inverse*inverse*start+2*inverse*progress*midpoint+progress*progress*end;
 }
}

/// <summary>Copied per-track runtime multipliers. Event/source gain and instance output volume
/// remain separate. An event can capture its handoff, director and audition state once per update.</summary>
internal sealed class AudioMusicMixState
{
 internal readonly record struct TrackMix(float Gain,bool TailAllowed);
 readonly Dictionary<AudioTrack,TrackMix> tracks;
 public bool TailAllowed(AudioTrack track)=>tracks is null || !tracks.TryGetValue(track,out var value)||value.TailAllowed;
 public float Gain(AudioTrack track)=>tracks is not null && tracks.TryGetValue(track,out var value)?value.Gain:1;
 public float Position {get;}
 internal int TrackCount=>tracks?.Count??-1;
 internal bool Matches(AudioTrack track,float gain,bool tailAllowed)
  =>tracks.TryGetValue(track,out var value)&&value.Gain==gain&&value.TailAllowed==tailAllowed;
 internal AudioMusicMixState WithPosition(float position)=>position==Position?this:new(position,tracks);
 internal static AudioMusicMixState Own(float position,Dictionary<AudioTrack,TrackMix> tracks)=>new(position,tracks);
 private AudioMusicMixState(float position,Dictionary<AudioTrack,TrackMix> owned)
 {
  if(!float.IsFinite(position)||position<0)throw new ArgumentOutOfRangeException(nameof(position));
  Position=position;tracks=owned;
 }
 public AudioMusicMixState(float position,IReadOnlyDictionary<AudioTrack,float> trackGains=null,IEnumerable<AudioTrack> blockedTails=null)
 {
  if(!float.IsFinite(position)||position<0)throw new ArgumentOutOfRangeException(nameof(position));
  Position=position;tracks=new();
  if(trackGains is not null)foreach(var item in trackGains)
  {
   ArgumentNullException.ThrowIfNull(item.Key);
   if(!float.IsFinite(item.Value)||item.Value<0)throw new ArgumentOutOfRangeException(nameof(trackGains));
   tracks.Add(item.Key,new(item.Value,true));
  }
  if(blockedTails is not null)foreach(var track in blockedTails)
   tracks[track]=new(Gain(track),false);
 }
}
