namespace Smod;

public sealed partial class AudioEventInstance
{
 // The parent owns a one-pass bridge. The destination stays preloaded and paused.
 private sealed class SubEventBridge
 {
  internal AudioSubEventChoice Choice;
  internal SubEventVoice Voice;
  internal bool Started;
  internal float OverlapSeconds;
 }
 private readonly Dictionary<AudioInstrument,SubEventBridge> _subEventBridges=new();

 private bool PrepareSubEventBridge(AudioInstrument instrument,SubEventVoice active)
 {
  if(active is null || active.EarlyEntry || active.Choice.ExitEvent is not {} resource)return true;
  if(!_subEventBridges.TryGetValue(instrument,out var bridge))
  {
   var choice=new AudioSubEventChoice{Event=resource,EntryFadeBeats=0,PreserveExitTail=true};
   bridge=new(){Choice=choice,Voice=new SubEventVoice(this,choice,0),OverlapSeconds=active.Choice.ExitEventOverlapSeconds};
   _subEventBridges[instrument]=bridge;
   bridge.Voice.Prepare();
  }
  bridge.Voice.SyncParameters();
  if(bridge.Voice.Ready)bridge.Voice.Child.sharedMusic.LimitPlaybackTo((long)Math.Round(resource.TimelineDuration*44100d));
  return bridge.Voice.Ready;
 }

 private float? PendingSubEventDelay(float position)
 {
  var running=_subEventBridges.Values.FirstOrDefault(b=>b.Started);
  if(running is not null)return Math.Max(0,running.Choice.Event.TimelineDuration-running.OverlapSeconds-(float)(running.Voice.Child.sharedMusic?.Position ?? 0)/44100);
  if(_pendingSubEvents.Count==0)return null;
  var boundary=_pendingSubEvents.Values.Min(p=>p.ParentPosition);
  return float.IsFinite(boundary)?Math.Max(0,boundary-position):null;
 }

 private void CancelSubEventBridge(AudioInstrument instrument)
 {
  if(_subEventBridges.Remove(instrument,out var bridge))bridge.Voice.Stop(0);
 }

 private AudioSubEventChoice BeginSubEventBridge(AudioInstrument instrument,AudioSubEventChoice target)
 {
  if(!_subEventBridges.TryGetValue(instrument,out var bridge))return null;
  bridge.Started=true;
  _pendingSubEvents[instrument]=new(target,float.PositiveInfinity);
  return bridge.Choice;
 }

 private bool SelectRunningBridge(AudioInstrument instrument,SubEventVoice active,out AudioSubEventChoice choice)
 {
  choice=null;
  if(!_subEventBridges.TryGetValue(instrument,out var bridge) || !bridge.Started)return false;
  var eligible=instrument.SubEvents.Where(c=>c.Eligible(_parameters)).ToArray();
  var target=_pendingSubEvents.TryGetValue(instrument,out var pending) && eligible.Contains(pending.Choice)
   ? pending.Choice : eligible.FirstOrDefault();
  if(target is null)
  {
   CancelPreparedSubEvent(instrument);
   _pendingSubEvents.Remove(instrument);
   return true;
  }
  if(!_preparedSubEvents.TryGetValue(instrument,out var prepared) || !ReferenceEquals(prepared.Choice,target))
  {
   if(prepared is not null)prepared.Stop(0);
   prepared=new SubEventVoice(this,target,0,target.EntryFadeBeats ?? instrument.SubEventTransitionBeats);
   _preparedSubEvents[instrument]=prepared;
   prepared.Prepare();
  }
  prepared.SyncParameters();
  _pendingSubEvents[instrument]=new(target,float.PositiveInfinity);
  // Use the audio clock, not editor frames or the parent's unrelated timeline.
  var complete=active is not null && ReferenceEquals(active.Choice,bridge.Choice)
   && active.Child.sharedMusic is not null
   && active.Child.sharedMusic.Position>=Math.Round((bridge.Choice.Event.TimelineDuration-bridge.OverlapSeconds)*44100d);
  if(!complete || !prepared.Ready){choice=bridge.Choice;return true;}
  _subEventBridges.Remove(instrument);
  _pendingSubEvents.Remove(instrument);
  _selectedSubEvents[instrument]=target;
  choice=target;
  return true;
 }
}
