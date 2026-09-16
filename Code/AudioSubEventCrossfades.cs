namespace Smod;

public sealed partial class AudioEventInstance
{
 private sealed record SubEventCrossfade(PlayingVoice Voice,long StartSample,float Seconds,bool PreserveTail=false);
 private readonly List<SubEventCrossfade> _crossfadingSubEvents=new();
 private void ClearSubEventCrossfades()
 {
  foreach(var fade in _crossfadingSubEvents)fade.Voice.Voice.Stop(0);
  _crossfadingSubEvents.Clear();
 }
 private static long SubEventOutputSample(SubEventVoice child) => child.Child.sharedMusic?.Position ?? (long)Math.Round(child.Child.CurrentTimelinePosition*44100d);
 private void RetainCrossfade(PlayingVoice voice,AudioSubEventChoice target)
 {
  // Bound overlap on rapid reversals. Keep outgoing children owned until their fade ends.
  ClearSubEventCrossfades();
  if(voice.Voice is SubEventVoice passage && passage.Choice.PreserveExitTail && passage.Child.sharedMusic is {} passageOutput)
  {
   var remaining=(Math.Round(passage.Choice.Event.TimelineDuration*44100d)-passageOutput.Position)/44100d;
   if(remaining>0){_crossfadingSubEvents.Add(new(voice,passageOutput.Position,(float)remaining,true));return;}
  }
  var seconds=target?.EntryCrossfadeSeconds??voice.Instrument.SubEventExitFadeSeconds;
  if(seconds>0 && voice.Voice is SubEventVoice child)
  {
   child.Child.handoffExitSeconds=0;
   _crossfadingSubEvents.Add(new(voice,SubEventOutputSample(child),seconds));
  }
  else voice.Voice.Stop(0);
 }
 private void UpdateSubEventCrossfades()
 {
  for(var i=_crossfadingSubEvents.Count-1;i>=0;i--)
  {
   var fade=_crossfadingSubEvents[i];
   var child=(SubEventVoice)fade.Voice.Voice;
   var elapsed=(SubEventOutputSample(child)-fade.StartSample)/44100d;
   var tailComplete=fade.PreserveTail && child.Child.sharedMusic is {} tailOutput && tailOutput.Position>=Math.Round(child.Choice.Event.TimelineDuration*44100d);
   if(tailComplete || !fade.PreserveTail && elapsed>=fade.Seconds || !child.IsPlaying)
   {
    child.Stop(0);_crossfadingSubEvents.RemoveAt(i);continue;
   }
   ApplyGain(fade.Voice);
   var t=(float)Math.Clamp(elapsed/fade.Seconds,0,1);
   if(!fade.PreserveTail)child.Volume*=1-t*t*(3-2*t);
  }
 }
}
