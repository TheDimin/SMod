using System.Threading.Tasks;
namespace Smod;
/// <summary>Editor-only native source admission check; does not start playback.</summary>
public static class AudioMusicSourceNativeCheck
{
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
 public static async Task<BackingResult> PrepareBacking(AudioBeatGrid grid,AudioTrack master,AudioTrack track)
 {
  using var bank=new AudioMusicSourceBank();
  var instrument=track.Instruments.Single();var source=instrument.Sound.Tracks.Single();
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
