using System.Threading.Tasks;
namespace Smod;

/// <summary>Session-owned prepared ranges. Call from the session thread. Dispose only after its native session is detached.
/// The budget bounds our managed PCM arrays and scratch, not native decoder memory.</summary>
internal sealed class AudioMusicSourceBank : IDisposable
{
 readonly long maximumBytes;
 readonly Dictionary<Key,short[]> prepared = new();
 readonly System.Threading.SemaphoreSlim gate = new(1,1);
 long residentBytes;
 bool closed;
 readonly System.Threading.CancellationTokenSource cancellation=new();
 public bool LastPreparationOffThread {get;private set;}
 public bool PreparationInProgress {get;private set;}
 internal sealed record Key(SoundFile Source,int Start,int Count,float MasterDb,float MasterHz,float TrackDb,float TrackHz);
 public long ResidentBytes => residentBytes;
 internal int PreparedRangeCount=>prepared.Count;
 public string LastBudgetRejection {get;private set;}
 bool Admit(long decodedFrames,long outputFrames,bool retainDecoded=false)
 {
  if(AudioMusicSourcePreparation.FitsBudget(residentBytes,decodedFrames,outputFrames,maximumBytes,retainDecoded))return true;
  LastBudgetRejection=$"Resident={residentBytes}, decoded={decodedFrames*4}, prepared={(retainDecoded?0:outputFrames*4)}, scratch=8192, limit={maximumBytes} bytes.";
  return false;
 }
 public Task<short[]> PrepareAsync(AudioInstrument instrument,SoundEventTrack selectedSource,AudioTone master=null,AudioTone track=null)
 {
  var range=AudioMusicSourcePreparation.ResolveRange(instrument,selectedSource);
  return PrepareAsync(selectedSource.Sound,range.Offset,range.Length,master,track);
 }
 public AudioMusicSourceBank(long maximumBytes=64L*1024*1024)
 {
  if(maximumBytes<8192)throw new ArgumentOutOfRangeException(nameof(maximumBytes));
  this.maximumBytes=maximumBytes;
 }
 /// <summary>Source offset includes both the source-track offset and instrument trim. Null means budget rejected; no late-play fallback.</summary>
 public async Task<short[]> PrepareAsync(SoundFile source,float offset,float length,AudioTone master=null,AudioTone track=null)
 {
  ArgumentNullException.ThrowIfNull(source);
  if(!float.IsFinite(offset)||offset<0||!float.IsFinite(length)||length<0)throw new ArgumentOutOfRangeException(nameof(offset));
  if(master is {IsValid:false} || track is {IsValid:false})throw new ArgumentException("Invalid tone settings.");
  // Snapshot authored values before any await; never follow resource edits mid-decode.
  master=new AudioTone{BassGainDb=master?.BassGainDb??0,BassFrequencyHz=master?.BassFrequencyHz??180};
  track=new AudioTone{BassGainDb=track?.BassGainDb??0,BassFrequencyHz=track?.BassFrequencyHz??180};
  await gate.WaitAsync(cancellation.Token);
  try
  {
   if(closed)throw new ObjectDisposedException(nameof(AudioMusicSourceBank));
   if(!await source.LoadAsync())throw new InvalidOperationException("Music source is not ready.");
   if(closed)throw new ObjectDisposedException(nameof(AudioMusicSourceBank));
   if(source.Rate!=44100 || source.Channels!=2 || !float.IsFinite(source.Duration) || source.Duration<=0)
    throw new InvalidOperationException("Shared music requires stereo 44100 Hz PCM.");
   var frames=(long)Math.Round(source.Duration*44100d);
   var start=(long)Math.Round(offset*44100d);
   var count=length==0 ? frames-start : (long)Math.Round(length*44100d);
   if(start<0||count<1||start+count>frames||frames>int.MaxValue/2)throw new ArgumentOutOfRangeException(nameof(length));
   var key=new Key(source,(int)start,(int)count,master.BassGainDb,master.BassFrequencyHz,track.BassGainDb,track.BassFrequencyHz);
   if(prepared.TryGetValue(key,out var cached))return cached;
   // Allow for metadata rounding before allocating the native decoded result.
   var canRetain=AudioMusicSourcePreparation.CanRetainDecoded(start,count,frames,master,track);
   if(!Admit(frames+1024,count,canRetain))return null;
   var decoded=await source.GetSamplesInterleavedAsync();
   if(closed)throw new ObjectDisposedException(nameof(AudioMusicSourceBank));
   if(decoded is null || decoded.Length%2!=0 || start+count>decoded.Length/2)
    throw new InvalidOperationException("Decoded music does not match its source range.");
   canRetain=AudioMusicSourcePreparation.CanRetainDecoded(start,count,decoded.Length/2,master,track);
   if(!Admit(decoded.Length/2,count,canRetain))return null;
   var callerThread=Environment.CurrentManagedThreadId;
   (short[] Pcm,bool OffThread) work;
   PreparationInProgress=true;
   try { work=await GameTask.RunInThreadAsync(async () =>
   {
    var workerThread=Environment.CurrentManagedThreadId;
    var pcm=await AudioMusicSourcePreparation.PrepareOwnedAsync(decoded,(int)start,(int)count,master,track,GameTask.Yield,cancellation.Token);
    return (Pcm:pcm,OffThread:workerThread!=callerThread);
   }); } finally { PreparationInProgress=false; }
   if(closed)throw new ObjectDisposedException(nameof(AudioMusicSourceBank));
   LastPreparationOffThread=work.OffThread;
   var result=work.Pcm;
   prepared.Add(key,result);residentBytes+=result.Length*2L;return result;
  }
  finally { gate.Release(); }
 }
 public void Dispose(){closed=true;cancellation.Cancel();prepared.Clear();residentBytes=0;}
}
