using System.Threading.Tasks;
namespace Smod;
/// <summary>Editor-only native source admission check; does not start playback.</summary>
public static class AudioMusicSourceNativeCheck
{
 public sealed record CompoundResult(bool Reused,bool ExactBeatOffsets,bool OffThread,bool BudgetRejected,long SourceBytes,long CompoundBytes,long ResidentAfterClose,bool Passed);
 public static async Task<CompoundResult> PrepareCompound(Sandbox.SoundFile source)
 {
  using var bank=new AudioMusicSourceBank(32L*1024*1024);
  var hit=await bank.PrepareAsync(source,0,.14f) ?? throw new InvalidOperationException("Drum source preparation exceeded budget.");
  var sourceBytes=bank.ResidentBytes;
  var hits=new[]{new AudioMusicPhraseComposer.Hit(hit,0,.25f),new AudioMusicPhraseComposer.Hit(hit,1,.25f),new AudioMusicPhraseComposer.Hit(hit,2,.25f)};
  var first=await bank.ComposeAsync(hits,110);var offThread=bank.LastPreparationOffThread;
  var compoundBytes=bank.ResidentBytes-sourceBytes;
  var second=await bank.ComposeAsync(hits,110);
  var reused=ReferenceEquals(first,second) && bank.ResidentBytes==sourceBytes+compoundBytes;
  var offsets=new[]{0,(int)Math.Round(60d/110*44100),(int)Math.Round(120d/110*44100)};
  var exact=first.Length==offsets[2]*2+hit.Length;
  foreach(var offset in offsets)
   for(var at=0;at<hit.Length;at++)exact &=first[offset*2+at]==(short)Math.Round(hit[at]*.25d);
  var rejected=false;
  try {await bank.ComposeAsync(new[]{new AudioMusicPhraseComposer.Hit(hit,16,.25f)},20);}
  catch(InvalidOperationException){rejected=true;}
  bank.Dispose();
  return new(reused,exact,offThread,rejected,sourceBytes,compoundBytes,bank.ResidentBytes,reused&&exact&&offThread&&rejected&&bank.ResidentBytes==0);
 }

 public sealed record CompoundPlaybackResult(bool Admitted,bool OneVoice,bool CancelledTogether,bool CancelledOwnerRejected,long ResidentAfterStop,bool Passed);
 public static async Task<CompoundPlaybackResult> ScheduleCompound(Sandbox.SoundFile source)
 {
  using var runtime=new AudioMusicRuntimeSession();runtime.Volume=0;
  var grid=new AudioBeatGrid{End=new((float)(96d/11)),BeatsPerMinute=110};
  var instrument=new AudioInstrument{TrimEnd=(float)(96d/11)};var selected=new SoundEventTrack{Sound=source};
  await runtime.StartAsync(grid,instrument,selected,null,null,0);
  var owner=new AudioMusicRequestOwner();
  var hits=new[]{new AudioMusicRuntimeSession.CompoundSourceHit(source,0,.14f,0,.25f),new AudioMusicRuntimeSession.CompoundSourceHit(source,0,.14f,1,.25f),new AudioMusicRuntimeSession.CompoundSourceHit(source,0,.14f,2,.25f)};
  var scheduled=await runtime.PrepareCompoundAndScheduleAsync(owner,hits,1,1);
  if(scheduled is null)throw new InvalidOperationException("Compound phrase was not scheduled.");
  var watch=System.Diagnostics.Stopwatch.StartNew();
  while(runtime.Position<scheduled.Sample+1024 && watch.Elapsed.TotalSeconds<3)await Task.Delay(5);
  var admitted=scheduled.Admission.IsCompletedSuccessfully && scheduled.Admission.Result;
  var one=runtime.ActivePhraseVoices==1;
  owner.Cancel();watch.Restart();
  while(runtime.ActivePhraseVoices!=0 && watch.Elapsed.TotalSeconds<2)await Task.Delay(5);
  var cancelled=runtime.ActivePhraseVoices==0;
  var rejected=await runtime.PrepareCompoundAndScheduleAsync(owner,hits,1,1) is null;
  runtime.Dispose();
  return new(admitted,one,cancelled,rejected,runtime.ResidentBytes,admitted&&one&&cancelled&&rejected&&runtime.ResidentBytes==0);
 }

 public sealed record CompoundEventResult(bool Started,bool Triggered,bool FullDuration,bool EditorDuration,bool Finished,bool Cleaned,string Error,bool Passed);
 public static async Task<CompoundEventResult> CheckCompoundEvent(Sandbox.SoundFile source)
 {
  var duration=(float)(96d/11);
  var sourceDefinition=new AudioSourceDefinition{Tracks=new(){new(){Sound=source}}};
  var action=new AudioParameter{Name="Action",Type=AudioParameterType.Discrete,Minimum=0,Maximum=10};
  var clip=new AudioInstrument{SourceId=sourceDefinition.Id,CompoundHits=new(){new(){Length=.14f,Gain=.25f},new(){Length=.14f,BeatOffset=1,Gain=.25f},new(){Length=.14f,BeatOffset=2,Gain=.25f}}};
  var backing=new AudioInstrument{SourceId=sourceDefinition.Id,TrimEnd=duration};
  var e=new AudioEvent{SharedMusicOutput=true,TimelineDuration=duration,Parameters=new(){action},Sources=new(){sourceDefinition},
   BeatGrids=new(){new(){Name="Test",End=new(duration),BeatsPerMinute=110}},LoopRegions=new(){new(){End=new(duration)}},
   Tracks=new(){new(){Name="Backing",Volume=0,Instruments=new(){backing}},
    new(){Name="Hit set",Overlay=new(){Enabled=true,VoiceGroup="hits",TriggerParameter=action,BeatGridName="Test",BeatsPerMinute=110,QuantizationBeats=1,CooldownBeats=4},Instruments=new(){clip}}}};
  AudioEventValidator.Validate(e).AssertValid();
  var instance=AudioSystem.GetEvent(e).CreateInstance();instance.Volume=0;
  var editorDuration = new double[]{100,110,130}.All(bpm => Math.Abs(global::Editor.AudioTimelineView.EstimateDuration(clip,bpm) - (120d/bpm+.14)) < .001);
  bool started=false,triggered=false,full=false,finished=false;
  try
  {
   instance.Start();var watch=System.Diagnostics.Stopwatch.StartNew();
   while(instance.PlaybackState==AudioPlaybackState.Starting && watch.Elapsed.TotalSeconds<5)await Task.Delay(5);
   started=instance.PlaybackState==AudioPlaybackState.Playing;
   instance.SetParametersByName(new Dictionary<string,float>{{"Action",1}});watch.Restart();
   while(watch.Elapsed.TotalSeconds<3)
   {
    var row=instance.CaptureLiveSnapshot().Voices.FirstOrDefault(v=>v.InstrumentId==clip.Id && v.State=="Silent");
    if(row is not null){triggered=true;full=Math.Abs(row.End-row.Start-(120d/110+.14))<.001;break;}
    _=instance.PlaybackState;await Task.Delay(5);
   }
   watch.Restart();
   while(watch.Elapsed.TotalSeconds<2)
   {
    _=instance.PlaybackState;
    if(!instance.CaptureLiveSnapshot().Voices.Any(v=>v.InstrumentId==clip.Id && v.State is "Silent" or "Scheduled" or "Loading")){finished=true;break;}
    await Task.Delay(5);
   }
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
  var cleaned=!AudioLiveDiagnostics.CaptureActive().Any(x=>x.Id==instance.LiveId);
  return new(started,triggered,full,editorDuration,finished,cleaned,instance.SharedMusicError,started&&triggered&&full&&editorDuration&&finished&&cleaned&&string.IsNullOrEmpty(instance.SharedMusicError));
 }

 public sealed record CancellationResult(bool ObservedActiveWorker,bool RejectedAfterClose,long ResidentBytes);
 public static async Task<CancellationResult> CancelBacking(Sandbox.SoundFile source)
 {
  using var bank=new AudioMusicSourceBank();
  var pending=bank.PrepareAsync(source,0,0,null,new AudioTone{BassGainDb=-6});
  var timeout=System.Diagnostics.Stopwatch.StartNew();
  while(!bank.PreparationInProgress && !pending.IsCompleted && timeout.Elapsed.TotalSeconds<5)await Task.Delay(1);
  var observed=bank.PreparationInProgress;
  bank.Dispose();bool rejected=false;
  try { await pending; } catch(OperationCanceledException){rejected=true;} catch(ObjectDisposedException){rejected=true;}
  return new(observed,rejected,bank.ResidentBytes);
 }

 public sealed record BackingResult(int Frames,long ResidentBytes,double PreparationMilliseconds,float BassGainDb,float BassFrequencyHz,bool OffThread);
 public static async Task<BackingResult> PrepareBacking(AudioEvent resource,AudioBeatGrid grid,AudioTrack master,AudioTrack track)
 {
  using var bank=new AudioMusicSourceBank();
  var instrument=track.Instruments.Single();
  var sourceDefinition=resource.Sources?.SingleOrDefault(candidate=>candidate?.Id==instrument.SourceId)
   ?? throw new InvalidOperationException("The backing instrument must reference a graph-owned source.");
  var source=sourceDefinition.Tracks?.SingleOrDefault(candidate=>candidate?.Sound is not null)
   ?? throw new InvalidOperationException("The backing source must contain one sound track.");
  var watch=System.Diagnostics.Stopwatch.StartNew();
  var pcm=await bank.PrepareAsync(instrument,source,master.Tone,track.Tone);
  if(pcm is null)throw new InvalidOperationException("Backing exceeded the managed PCM budget.");
  var session=new AudioMusicPcmSession(grid,backingPcm:pcm,repeatComposition:true);
  session.CloseRequests();
  return new(pcm.Length/2,bank.ResidentBytes,watch.Elapsed.TotalMilliseconds,track.Tone?.BassGainDb ?? 0,track.Tone?.BassFrequencyHz ?? 180,bank.LastPreparationOffThread);
 }

 public sealed record Result(int Frames,long ResidentBytes,bool Reused,bool SmallBudgetRejected);
 public static async Task<Result> Prepare(Sandbox.SoundFile source)
 {
  using var bank=new AudioMusicSourceBank();var tone=new AudioTone{BassGainDb=-6,BassFrequencyHz=180};
  var first=await bank.PrepareAsync(source,.6f,1.2f,null,tone);
  var second=await bank.PrepareAsync(source,.6f,1.2f,null,tone);
  using var small=new AudioMusicSourceBank(8192);
  var rejected=await small.PrepareAsync(source,.6f,1.2f,null,tone);
  return new(first?.Length/2 ?? 0,bank.ResidentBytes,ReferenceEquals(first,second),rejected is null);
 }
}
