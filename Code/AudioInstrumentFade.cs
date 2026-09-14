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
 readonly Dictionary<AudioTrack,float> gains;
 public float Position {get;}
 public AudioMusicMixState(float position,IReadOnlyDictionary<AudioTrack,float> trackGains=null)
 {
  if(!float.IsFinite(position)||position<0)throw new ArgumentOutOfRangeException(nameof(position));
  Position=position;gains=new();
  if(trackGains is null)return;
  foreach(var item in trackGains)
  {
   ArgumentNullException.ThrowIfNull(item.Key);
   if(!float.IsFinite(item.Value)||item.Value<0||item.Value>1)throw new ArgumentOutOfRangeException(nameof(trackGains));
   gains.Add(item.Key,item.Value);
  }
 }
 public float Gain(AudioTrack track)=>gains.TryGetValue(track,out var value)?value:1;
}
