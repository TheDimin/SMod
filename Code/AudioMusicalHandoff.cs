namespace Smod;

/// <summary>Authored stem role used only while changing song sub-graphs.</summary>
public enum AudioTransitionRole { Backing, Lead, Rhythm }

internal static class AudioMusicalHandoff
{
 internal static float Gain(AudioTransitionRole role,float position,float entrySeconds,float exitEnd,float exitSeconds)
 {
  var gain=1f;
  if(entrySeconds>0)
  {
   var seconds=role switch {AudioTransitionRole.Rhythm=>.02f,AudioTransitionRole.Lead=>entrySeconds*2,_=>entrySeconds};
   // Backing enters promptly, then settles to its requested intensity gain.
   var t=Math.Clamp(position/seconds,0,1);
   gain=role==AudioTransitionRole.Backing ? Math.Clamp(position/.02f,0,1)*(.4f+.6f*Smooth(t)) : Smooth(t);
  }
  if(exitSeconds>0)
  {
   var seconds=role switch {AudioTransitionRole.Rhythm=>.02f,AudioTransitionRole.Lead=>exitSeconds,_=>exitSeconds*.5f};
   gain*=Smooth(Math.Clamp((exitEnd-position)/seconds,0,1));
  }
  return gain;
 }
 private static float Smooth(float value)=>value*value*(3-2*value);
}

public sealed partial class AudioEventInstance
{
 private float handoffEntrySeconds;
 private float handoffEntryStart;
 private float handoffExitEnd;
 private float handoffExitSeconds;
 private float HandoffGain(AudioTrack track)=>AudioMusicalHandoff.Gain(track.TransitionRole,CurrentTimelinePosition-handoffEntryStart,handoffEntrySeconds,handoffExitEnd-handoffEntryStart,handoffExitSeconds);
}
