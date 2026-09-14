using Smod;
using System.Threading.Tasks;
public sealed partial class AudioEventTests
{
 static AudioEvent SharedOverlaySong()
 {
  var e=SharedSong();var action=new AudioParameter{Name="Service",Type=AudioParameterType.Discrete,Minimum=0,Maximum=10};e.Parameters.Add(action);
  e.Tracks.Add(new(){Name="Action",Overlay=new(){Enabled=true,TriggerParameter=action,BeatGridName="Song",BeatsPerMinute=100,QuantizationBeats=1,CooldownBeats=1,Volume=.5f},
   Instruments=new(){new(){Sound=CreateSoundEvent(),TrimEnd=.6f,FadeInDuration=.01f}}});return e;
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
