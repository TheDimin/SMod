using Smod;
using System.Threading.Tasks;
public sealed partial class AudioEventTests
{
 [TestMethod]
 public void SharedOverlay_QuarterBeatCooldownIsValidButZeroIsNot()
 {
  var song=SharedOverlaySong();song.Tracks[0].Overlay.CooldownBeats=.25f;
  Assert.IsTrue(AudioEventValidator.Validate(song).IsValid);
  song.Tracks[0].Overlay.CooldownBeats=0;
  Assert.IsFalse(AudioEventValidator.Validate(song).IsValid);
 }
 [TestMethod]
 public void SharedOverlay_WithdrawnChainLoadCannotBlockFutureServices()
 {
  var song=SharedOverlaySong();var rule=song.Tracks[0].Overlay;rule.MaximumChainPhrases=3;rule.ChainRestBeats=1;
  var enabled=new AudioParameter{Name="Enabled",Minimum=0,Maximum=1,DefaultValue=1};song.Parameters.Add(enabled);
  rule.Conditions=new(){Conditions=new(){new(){Parameter=enabled,Comparison=AudioParameterComparison.Equal,Value=1}}};
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var loading=new TaskCompletionSource<AudioMusicPcmSession.Scheduled>();
  session.PhraseFactory=owner=>session.PhraseCalls==1?loading.Task:Task.FromResult(new AudioMusicPcmSession.Scheduled(52920,Task.FromResult(true)));
  var instance=AudioSystem.GetEvent(song).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();instance.SetParameterByName("Service",1);var withdrawn=session.RequestedOwner;
   instance.SetParameterByName("Enabled",0);Assert.IsTrue(withdrawn.IsCancelled);
   session.Position=26460;instance.SetParameterByName("Enabled",1);instance.SetParameterByName("Service",2);
   Assert.AreEqual(2,session.PhraseCalls,"A withdrawn first load must not leave the chain active forever.");
   var current=session.RequestedOwner;loading.SetResult(new(26460,Task.FromResult(true)));
   Assert.IsFalse(current.IsCancelled);Assert.AreEqual(2,session.PhraseCalls);
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod]
 public void SharedOverlay_SuccessChainIsBoundedPreservesTailsAndDiscardsRestTriggers()
 {
  var song=SharedOverlaySong();song.TimelineDuration=24;song.MasterTrack.Instruments[0].TrimEnd=24;song.BeatGrids[0].End=new(24);song.LoopRegions[0].End=new(24);
  var track=song.Tracks[0];var rule=track.Overlay;
  rule.MaximumChainPhrases=3;rule.ChainRestBeats=4;rule.CooldownBeats=16;
  rule.QueueWhilePlaying=false;rule.IgnoreTriggersDuringCooldown=true;
  track.Instruments[0].TrimEnd=1.2f;track.Instruments[0].OverlayBodySeconds=.6f;
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var owners=new List<AudioMusicRequestOwner>();
  session.PhraseFactory=owner=>{owners.Add(owner);return Task.FromResult(new AudioMusicPcmSession.Scheduled(session.RequestedChainSample ?? session.Position+26460,Task.FromResult(true)));};
  var instance=AudioSystem.GetEvent(song).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();instance.SetParameterByName("Service",1);
   session.Position=26460;instance.SetParameterByName("Service",2);
   Assert.AreEqual(2,session.PhraseCalls);Assert.AreEqual(52920L,session.RequestedChainSample);
   Assert.IsFalse(owners[0].IsCancelled,"A continuation must preserve the first tail.");
   instance.SetParameterByName("Service",3);instance.SetParameterByName("Service",4);
   Assert.AreEqual(2,session.PhraseCalls,"Only one following phrase can occupy the tail overlap slot.");
   // The retained tail fills the second body; the exact third join is now too late.
   session.Position=79380;instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreEqual(2,session.PhraseCalls,"A missed exact join is dropped, never shifted to a late beat.");
   session.Position=211680;instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreEqual(2,session.PhraseCalls,"Old services cannot restart after the rest.");
   instance.SetParameterByName("Service",5);Assert.AreEqual(3,session.PhraseCalls);
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
  Assert.IsTrue(owners.All(o=>o.IsCancelled));
 }
 [TestMethod]
 public void SharedOverlay_ThreePhraseCapThenFullRestEvenWithContinuousSuccess()
 {
  var song=SharedOverlaySong();song.TimelineDuration=24;song.MasterTrack.Instruments[0].TrimEnd=24;song.BeatGrids[0].End=new(24);song.LoopRegions[0].End=new(24);
  var track=song.Tracks[0];var rule=track.Overlay;
  rule.MaximumChainPhrases=3;rule.ChainRestBeats=4;rule.CooldownBeats=16;
  track.Instruments[0].TrimEnd=.75f;track.Instruments[0].OverlayBodySeconds=.6f;
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  session.PhraseFactory=owner=>Task.FromResult(new AudioMusicPcmSession.Scheduled(session.RequestedChainSample ?? session.Position+26460,Task.FromResult(true)));
  var instance=AudioSystem.GetEvent(song).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();instance.SetParameterByName("Service",1);
   session.Position=26460;instance.SetParameterByName("Service",2);instance.SetParameterByName("Service",3);
   session.Position=60000;instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreEqual(3,session.PhraseCalls);Assert.AreEqual(79380L,session.RequestedChainSample);
   instance.SetParameterByName("Service",4);session.Position=120000;instance.SetParameterByName("Service",5);
   Assert.AreEqual(3,session.PhraseCalls);
   session.Position=218295;instance.RefreshAuthoredPropertiesForAudition();Assert.AreEqual(3,session.PhraseCalls);
   instance.SetParameterByName("Service",6);Assert.AreEqual(4,session.PhraseCalls,"A fresh success can start after the complete tail plus four-beat rest.");
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod]
 public void SharedOverlay_ChainValidationRejectsUnboundedAndUnsupportedRules()
 {
  var song=SharedOverlaySong();var rule=song.Tracks[0].Overlay;
  rule.MaximumChainPhrases=3;Assert.IsTrue(AudioEventValidator.Validate(song).IsValid);
  rule.MaximumChainPhrases=0;Assert.IsFalse(AudioEventValidator.Validate(song).IsValid);
  rule.MaximumChainPhrases=9;Assert.IsFalse(AudioEventValidator.Validate(song).IsValid);
  rule.MaximumChainPhrases=3;rule.ChainRestBeats=0;Assert.IsFalse(AudioEventValidator.Validate(song).IsValid);
  rule.ChainRestBeats=4;song.SharedMusicOutput=false;Assert.IsFalse(AudioEventValidator.Validate(song).IsValid);
 }
 [TestMethod]
 public void SharedOverlay_DistantLeadCannotReturnAfterItsDeadline()
 {
  var song=SharedOverlaySong();song.TimelineDuration=24;song.MasterTrack.Instruments[0].TrimEnd=24;
  song.BeatGrids[0].End=new(24);song.LoopRegions[0].End=new(24);
  var lead=song.Tracks[0];lead.Overlay.VoiceGroup="response";
  lead.Instruments[0].OverlayBars=new(){5};lead.Instruments[0].MaximumMusicWaitBeats=2;
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  session.Phrase.SetResult(new(423360,Task.FromResult(true)));
  var instance=AudioSystem.GetEvent(song).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();instance.SetParameterByName("Service",1);
   Assert.AreEqual(0,session.PhraseCalls,"A bar 9.6 seconds away exceeds the 1.2-second deadline.");
   session.Position=414540;instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreEqual(0,session.PhraseCalls,"The expired action cannot return when its bar approaches.");
   instance.SetParameterByName("Service",2);Assert.AreEqual(1,session.PhraseCalls);
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }

 [TestMethod]
 [DataRow(false,false)] [DataRow(true,false)] [DataRow(false,true)]
 public void SharedOverlay_ActionReplacesOnlyUnstartedActivity(bool workStarted,bool noTimelyEntry)
 {
  var song=SharedOverlaySong();var action=song.Tracks[0];action.Overlay.VoiceGroup="drums";action.Overlay.QueueWhilePlaying=false;action.Overlay.IgnoreTriggersDuringCooldown=true;
  var work=new AudioParameter{Name="Work",Minimum=0,Maximum=1};song.Parameters.Add(work);
  song.Tracks.Add(new(){Name="Work",Overlay=new(){Enabled=true,VoiceGroup="drums",BeatGridName="Song",BeatsPerMinute=100,QuantizationBeats=1,CooldownBeats=1,
   ActivityConditions=new(){Conditions=new(){new(){Parameter=work,Comparison=AudioParameterComparison.GreaterOrEqual,Value=1}}}},
   Instruments=new(){AddSourceInstrument(song, instrument => instrument.TrimEnd=.6f)}});
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  session.PhraseFactory=owner=>Task.FromResult(new AudioMusicPcmSession.Scheduled(26460,Task.FromResult(true)));
  var instance=AudioSystem.GetEvent(song).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();instance.SetParameterByName("Work",1);var waitingWork=session.RequestedOwner;
   if(workStarted)session.Position=26460;
   if(noTimelyEntry){action.Instruments[0].OverlayBars=new(){5};action.Instruments[0].MaximumMusicWaitBeats=1;}
   instance.SetParameterByName("Service",1);
   if(workStarted || noTimelyEntry)
   {
    Assert.AreEqual(1,session.PhraseCalls);Assert.IsFalse(waitingWork.IsCancelled,"Preserve audible work and work with no timely replacement.");return;
   }
   Assert.AreEqual(2,session.PhraseCalls,"An action must replace activity that has not sounded yet.");
   Assert.IsTrue(waitingWork.IsCancelled);var actionOwner=session.RequestedOwner;
   Assert.AreEqual(.5f,actionOwner.Gain);
   session.Position=26460;instance.SetParameterByName("Service",2);
   Assert.AreEqual(2,session.PhraseCalls);Assert.IsFalse(actionOwner.IsCancelled,"Do not cut an audible set.");
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }

 static AudioEvent SharedOverlaySong()
 {
  var e=SharedSong();var action=new AudioParameter{Name="Service",Type=AudioParameterType.Discrete,Minimum=0,Maximum=10};e.Parameters.Add(action);
  e.Tracks.Add(new(){Name="Action",Overlay=new(){Enabled=true,TriggerParameter=action,BeatGridName="Song",BeatsPerMinute=100,QuantizationBeats=1,CooldownBeats=1,Volume=.5f},
   Instruments=new(){AddSourceInstrument(e, instrument => { instrument.TrimEnd=.6f;instrument.FadeInDuration=.01f; })}});return e;
 }
 [TestMethod] public void SharedOverlay_WithdrawnPreparationCannotDelayTheNextWorkPhrase()
 {
  var song=SharedOverlaySong();var rule=song.Tracks[0].Overlay;
  rule.TriggerParameter=null;
  rule.ActivityConditions=new(){Conditions=new(){new(){Parameter=song.Parameters[0],Comparison=AudioParameterComparison.GreaterOrEqual,Value=1}}};
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var oldPreparation=new TaskCompletionSource<AudioMusicPcmSession.Scheduled>();
  session.PhraseFactory=owner=>session.PhraseCalls==1 ? oldPreparation.Task : Task.FromResult(new AudioMusicPcmSession.Scheduled(26460,Task.FromResult(true)));
  var instance=AudioSystem.GetEvent(song).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();instance.SetParameterByName("Service",1);
   var oldOwner=session.RequestedOwner;
   instance.SetParameterByName("Service",0);Assert.IsTrue(oldOwner.IsCancelled);
   oldPreparation.SetResult(new(26460,Task.FromResult(true)));
   instance.SetParameterByName("Service",1);
   Assert.AreEqual(2,session.PhraseCalls,"A cancelled load must not add a cooldown to new work.");
   Assert.IsFalse(session.RequestedOwner.IsCancelled);
   Assert.AreEqual(0,session.Disposals);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void SharedOverlay_AuthoredBoostSurvivesValidationTriggerAndLiveGainRefresh()
 {
  var song=SharedOverlaySong();song.Tracks[0].Volume=4;
  Assert.IsTrue(AudioEventValidator.Validate(song).IsValid);
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);session.Phrase.SetResult(new(26460,Task.FromResult(true)));
  var instance=AudioSystem.GetEvent(song).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();instance.SetParameterByName("Service",1);
   Assert.AreEqual(1,session.PhraseCalls);
   Assert.AreEqual(2f,session.RequestedOwner.Gain);
   song.Tracks[0].Volume=6;instance.RefreshAuthoredPropertiesForAudition();
   Assert.AreEqual(3f,session.RequestedOwner.Gain);
   Assert.AreEqual(1,session.PhraseCalls,"A gain edit must not restart the phrase.");
   instance.SetTrackMutedForAudition(song.Tracks[0],true);
   Assert.AreEqual(0f,session.RequestedOwner.Gain);
   instance.SetTrackMutedForAudition(song.Tracks[0],false);
   Assert.AreEqual(3f,session.RequestedOwner.Gain);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod]
 [DataRow(false,false)] [DataRow(true,false)] [DataRow(false,true)] [DataRow(true,true)]
 public void SharedOverlay_BatchedFactsChooseServiceRegardlessOfOrder(bool reverse,bool nested)
 {
  var song=SharedOverlaySong();var work=new AudioParameter{Name="Work",Minimum=0,Maximum=1};song.Parameters.Add(work);
  song.Tracks[0].Overlay.VoiceGroup="lead";
  song.Tracks.Add(new(){Name="Work phrase",Overlay=new(){Enabled=true,VoiceGroup="lead",BeatGridName="Song",BeatsPerMinute=100,QuantizationBeats=1,CooldownBeats=1,
   ActivityConditions=new(){Conditions=new(){new(){Parameter=work,Comparison=AudioParameterComparison.GreaterOrEqual,Value=1}}}},
   Instruments=new(){AddSourceInstrument(song, instrument => instrument.TrimEnd=.6f)}});
  var root=nested?Nested(song):song;if(nested)root.Parameters.AddRange(song.Parameters);
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);session.Phrase.SetResult(new(26460,Task.FromResult(true)));
  var instance=AudioSystem.GetEvent(root).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();
   var values=new Dictionary<string,float>();values.Add(reverse?"Work":"Service",1);values.Add(reverse?"Service":"Work",1);
   instance.SetParametersByName(values);
   Assert.AreEqual(1,session.PhraseCalls);
   Assert.AreEqual(.5f,session.RequestedOwner.Gain,"Service gain identifies the selected role; work gain is 1.");
   var calls=session.PhraseCalls;
   Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>instance.SetParametersByName(new Dictionary<string,float>{{"Work",0},{"Service",100}}));
   Assert.AreEqual(1f,session.LastParameters[work].Number,"An invalid batch must not partly apply.");
   Assert.AreEqual(calls,session.PhraseCalls);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void SharedOverlay_InvalidBatchesNeverTriggerOrChangeFacts()
 {
  var e=SharedOverlaySong();var work=new AudioParameter{Name="Work",Minimum=0,Maximum=1};e.Parameters.Add(work);
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var instance=AudioSystem.GetEvent(e).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();
   foreach(var invalid in new[]{float.NaN,float.PositiveInfinity,-1f,11f,.5f})
    Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>instance.SetParametersByName(new Dictionary<string,float>{{"Work",1},{"Service",invalid}}));
   Assert.ThrowsExactly<InvalidOperationException>(()=>instance.SetParametersByName(new Dictionary<string,float>{{"Work",1},{"Missing",1}}));
   Assert.ThrowsExactly<ArgumentException>(()=>instance.SetParametersByName(new Dictionary<string,float>{{"Service",1},{"service",2}}));
   Assert.ThrowsExactly<ArgumentNullException>(()=>instance.SetParametersByName(null));
   instance.SetParametersByName(new Dictionary<string,float>());
   Assert.AreEqual(0,session.PhraseCalls);
   Assert.AreEqual(0f,session.LastParameters[work].Number);
   instance.SetParametersByName(new Dictionary<string,float>{{"Work",1},{"Service",1}});
   Assert.AreEqual(1,session.PhraseCalls,"Rejected batches must not consume the later valid trigger.");
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void SharedOverlay_BodyAllowsOneFollowingPhraseWithoutCuttingTheTail()
 {
  var e=SharedOverlaySong();e.Tracks[0].Instruments[0].TrimEnd=1.2f;e.Tracks[0].Instruments[0].OverlayBodySeconds=.6f;
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);
  var owners=new List<AudioMusicRequestOwner>();
  session.PhraseFactory=owner=>{owners.Add(owner);return Task.FromResult(new AudioMusicPcmSession.Scheduled(owners.Count==1?26460:60000,Task.FromResult(true)));};
  var instance=AudioSystem.GetEvent(e).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();instance.SetParameterByName("Service",1);
   session.Position=52920;instance.SetParameterByName("Service",2);
   Assert.AreEqual(2,session.PhraseCalls);Assert.IsFalse(owners[0].IsCancelled,"The first source tail must remain active.");
   Assert.AreEqual(2,instance.InstrumentPlaybackStates.Count);
   var snapshot=instance.CaptureLiveSnapshot();
   var rows=snapshot.Voices.Where(v=>v.InstrumentId==e.Tracks[0].Instruments[0].Id).ToArray();
   Assert.AreEqual(2,rows.Length);
   Assert.AreEqual(1,rows.Count(v=>v.IsReleaseTail));
   Assert.AreEqual(2,rows.Select(v=>v.Start).Distinct().Count());
   Assert.AreEqual(1,snapshot.Voices.Count(v=>v.InstrumentId==e.MasterTrack.Instruments[0].Id),"The backing keeps its separate identity.");
   instance.SetParameterByName("Service",3);Assert.AreEqual(2,session.PhraseCalls,"A third phrase must not exceed the two-slot group limit.");
   session.Position=79380;_=instance.InstrumentPlaybackStates;
   Assert.IsTrue(owners[0].IsCancelled);Assert.IsFalse(owners[1].IsCancelled);
   instance.Stop(AudioStopMode.Immediate);Assert.IsTrue(owners.All(o=>o.IsCancelled));
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void SharedPhrase_RendererRetainsTailDuringFollowingBody()
 {
  var core=new AudioMusicPcmRenderer(2);
  var first=new AudioMusicRequestOwner();var second=new AudioMusicRequestOwner();
  Assert.IsTrue(core.Schedule(first,Enumerable.Repeat((short)8192,16).ToArray(),0,1));
  Assert.IsTrue(core.Schedule(second,Enumerable.Repeat((short)16384,16).ToArray(),4,1));
  var output=new float[24];core.Render(output);
  for(var frame=0;frame<12;frame++)
  {
   var expected=frame<4?.25f:frame<8?.75f:.5f;
   Assert.AreEqual(expected,output[frame*2],.000001f);
   Assert.AreEqual(expected,output[frame*2+1],.000001f);
  }
 }
 [TestMethod] public void SharedOverlay_BodyValidationRejectsUnsupportedOrOutOfRangeAuthoring()
 {
  var e=SharedOverlaySong();var clip=e.Tracks[0].Instruments[0];clip.OverlayBodySeconds=.3f;
  Assert.IsTrue(AudioEventValidator.Validate(e).IsValid);
  clip.OverlayBodySeconds=.7f;Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
  clip.OverlayBodySeconds=float.NaN;Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
  clip.OverlayBodySeconds=.3f;e.SharedMusicOutput=false;Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
  e.SharedMusicOutput=true;e.Tracks[0].Overlay=null;Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
 }
 [TestMethod] public void SharedOverlay_WorldActionUsesSharedOwnerAndLiveMuteWithoutNativeVoices()
 {
  var e=SharedOverlaySong();var session=new EventMusicSessionFake();session.Loaded.SetResult(true);session.Phrase.SetResult(new(26460,Task.FromResult(true)));
  var instance=AudioSystem.GetEvent(e).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();instance.SetParameterByName("Service",1);
   Assert.AreEqual(1,session.PhraseCalls);Assert.IsNotNull(session.RequestedOwner);Assert.IsFalse(session.RequestedOwner.IsCancelled);
   Assert.AreEqual(.5f,session.RequestedOwner.Gain);Assert.AreEqual(0,_backend.Voices.Count);
   Assert.IsTrue(instance.InstrumentPlaybackStates.Single().IsPending);
   session.Position=26460;Assert.IsTrue(instance.InstrumentPlaybackStates.Single().IsPlaying);
   instance.SetTrackMutedForAudition(e.Tracks[0],true);Assert.AreEqual(0f,session.RequestedOwner.Gain);
   instance.Stop(AudioStopMode.Immediate);Assert.IsTrue(session.RequestedOwner.IsCancelled);
  }
  finally {if(instance.PlaybackState!=AudioPlaybackState.Stopped)instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void SharedOverlay_StopDuringPreparationCannotAdmitLateSound()
 {
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);var instance=AudioSystem.GetEvent(SharedOverlaySong()).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  instance.Start();instance.SetParameterByName("Service",1);var owner=session.RequestedOwner;
  instance.Stop(AudioStopMode.Immediate);session.Phrase.SetResult(new(26460,Task.FromResult(true)));
  Assert.IsTrue(owner.IsCancelled);Assert.AreEqual(AudioPlaybackState.Stopped,instance.PlaybackState);Assert.AreEqual(0,instance.InstrumentPlaybackStates.Count);instance.Release();
 }

 [TestMethod] public void SharedOverlay_ActivityWithdrawalCancelsOnlyTheQueuedPhrase()
 {
  var e=SharedOverlaySong();var rule=e.Tracks[0].Overlay;var fact=e.Parameters[0];
  rule.TriggerParameter=null;rule.ActivityConditions=new(){Conditions=new(){new(){Parameter=fact,Comparison=AudioParameterComparison.GreaterOrEqual,Value=1}}};
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);session.Phrase.SetResult(new(26460,Task.FromResult(true)));
  var instance=AudioSystem.GetEvent(e).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();instance.SetParameterByName("Service",1);var phrase=session.RequestedOwner;Assert.IsFalse(phrase.IsCancelled);
   instance.SetParameterByName("Service",0);Assert.IsTrue(phrase.IsCancelled);Assert.AreEqual(AudioPlaybackState.Playing,instance.PlaybackState);
   Assert.AreEqual(0,session.Disposals);Assert.AreEqual(0,instance.InstrumentPlaybackStates.Count);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod]
 [DataRow(false, false, true)]
 [DataRow(true, false, false)]
 [DataRow(false, true, false)]
 public void SharedOverlay_HybridRuleWithdrawsOnlyPendingActivity(bool triggered, bool started, bool cancelled)
 {
  var e=SharedOverlaySong();
  var workload=new AudioParameter{Name="Workload",Minimum=0,Maximum=1};e.Parameters.Add(workload);
  e.Tracks[0].Overlay.ActivityConditions=new(){Conditions=new(){new(){Parameter=workload,Comparison=AudioParameterComparison.GreaterOrEqual,Value=1}}};
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);session.Phrase.SetResult(new(26460,Task.FromResult(true)));
  var instance=AudioSystem.GetEvent(e).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();
   if(triggered)instance.SetParameterByName("Service",1);
   instance.SetParameterByName("Workload",1);
   var owner=session.RequestedOwner;Assert.IsNotNull(owner);Assert.AreEqual(1,session.PhraseCalls);
   if(started)session.Position=26460;
   instance.SetParameterByName("Workload",0);
   Assert.AreEqual(cancelled,owner.IsCancelled);
   Assert.AreEqual(cancelled?0:1,instance.InstrumentPlaybackStates.Count);
   Assert.AreEqual(0,session.Disposals,"The continuous backing must remain active.");
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void SharedOverlay_NonQueueingGroupDoesNotRepeatBusyActionsAfterThePhrase()
 {
  var e=SharedOverlaySong();e.Tracks[0].Overlay.QueueWhilePlaying=false;
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);session.Phrase.SetResult(new(26460,Task.FromResult(true)));
  var instance=AudioSystem.GetEvent(e).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();instance.SetParameterByName("Service",1);session.Position=26460;
   instance.SetParameterByName("Service",2);Assert.AreEqual(1,session.PhraseCalls);
   session.Position=60000;_ = instance.TimelinePosition;Assert.AreEqual(1,session.PhraseCalls);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void SharedPhrase_RendererAppliesCopiedFadeAndLiveOwnerGainOnTheSameClock()
 {
  var authored=new AudioInstrument{FadeInDuration=4/44100f};var owner=new AudioMusicRequestOwner(authored);
  authored.FadeInDuration=0;var core=new AudioMusicPcmRenderer(1);
  Assert.IsTrue(core.Schedule(owner,Enumerable.Repeat((short)16384,4096).ToArray(),2,1));
  var output=new float[12];core.Render(output);Assert.AreEqual(0f,output[4]);Assert.AreEqual(.125f,output[6],.0000001f);Assert.AreEqual(.375f,output[10],.0000001f);
  owner.Gain=0;core.Render(new float[1764]);output=new float[2];core.Render(output);Assert.AreEqual(0f,output[0]);Assert.AreEqual(0f,output[1]);
 }
}
