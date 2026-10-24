using System.Threading.Tasks;
namespace Smod;

/// <summary>Session-owned prepared ranges. Call from the session thread. Dispose only after its native session is detached.
/// The budget bounds our managed PCM arrays and scratch, not native decoder memory.</summary>
internal sealed partial class AudioMusicSourceBank : IDisposable
{
 readonly long maximumBytes;
 readonly Dictionary<Key,short[]> prepared = new();
 readonly Dictionary<CompoundKey,short[]> compounds = new();
 sealed record CompoundKey(AudioMusicPhraseComposer.Hit First,AudioMusicPhraseComposer.Hit? Second,AudioMusicPhraseComposer.Hit? Third,double Bpm);
 readonly System.Threading.SemaphoreSlim gate = new(1,1);
 long residentBytes;
 bool closed;
 readonly System.Threading.CancellationTokenSource cancellation=new();
 public bool LastPreparationOffThread {get;private set;}
 public bool PreparationInProgress {get;private set;}
 internal sealed record Key(SoundFile Source,int Start,int Count,float MasterDb,float MasterHz,float TrackDb,float TrackHz);
 public int NativeDecodeCount {get;private set;}
 public long ResidentBytes => residentBytes;
 internal int PreparedRangeCount=>prepared.Count;
 public string LastBudgetRejection {get;private set;}
 bool Admit(long decodedFrames,long outputFrames,bool retainDecoded=false,int sourceRate=44100)
 {
  if(AudioMusicSourcePreparation.FitsBudget(residentBytes,decodedFrames,outputFrames,maximumBytes-(sourceRate==48000?AudioMusicResampler.ScratchBytes:0),retainDecoded))return true;
  LastBudgetRejection=$"Resident={residentBytes}, decoded={decodedFrames*4}, prepared={(retainDecoded?0:outputFrames*4)}, scratch={8192+(sourceRate==48000?AudioMusicResampler.ScratchBytes:0)}, limit={maximumBytes} bytes.";
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
   if(source.Rate is not (44100 or 48000) || source.Channels!=2 || !float.IsFinite(source.Duration) || source.Duration<=0)
    throw new InvalidOperationException($"Shared music source {source.ResourcePath} requires stereo 44100 or 48000 Hz audio with positive duration; got {source.Rate} Hz, {source.Channels} channels, {source.Duration} seconds.");
   var sourceRate=source.Rate;
   var frames=(long)Math.Round(source.Duration*44100d);
   var start=(long)Math.Round(offset*44100d);
   var count=length==0 ? frames-start : (long)Math.Round(length*44100d);
   if(start<0||count<1||start+count>frames||frames>int.MaxValue/2)throw new ArgumentOutOfRangeException(nameof(length));
   var key=new Key(source,(int)start,(int)count,master.BassGainDb,master.BassFrequencyHz,track.BassGainDb,track.BassFrequencyHz);
   if(prepared.TryGetValue(key,out var cached))return cached;
   var generation=AudioMusicPreparationCache.Generation;
   if(AudioMusicPreparationCache.Enabled && AudioMusicPreparationCache.Ranges.TryGet(key,out cached))
   {
    if(!Admit(0,cached.Length/2))return null;
    prepared.Add(key,cached);residentBytes+=cached.Length*2L;return cached;
   }
   // Allow for metadata rounding before allocating the native decoded result.
   var canRetain=sourceRate==44100 && AudioMusicSourcePreparation.CanRetainDecoded(start,count,frames,master,track);
   if(!Admit((long)Math.Ceiling(source.Duration*(double)sourceRate)+1024,count,canRetain,sourceRate))return null;
   cancellation.Token.ThrowIfCancellationRequested();
   NativeDecodeCount++;AudioMusicPreparationCache.NativeDecodes++;
   var decoded=await source.GetSamplesInterleavedAsync();
   if(closed)throw new ObjectDisposedException(nameof(AudioMusicSourceBank));
   if(decoded is null || decoded.Length%2!=0 || start+count>AudioMusicResampler.OutputFrames(decoded.Length/2,sourceRate))
    throw new InvalidOperationException("Decoded music does not match its source range.");
   canRetain=sourceRate==44100 && AudioMusicSourcePreparation.CanRetainDecoded(start,count,decoded.Length/2,master,track);
   if(!Admit(decoded.Length/2,count,canRetain,sourceRate))return null;
   var callerThread=Environment.CurrentManagedThreadId;
   (short[] Pcm,bool OffThread) work;
   PreparationInProgress=true;
   try { work=await GameTask.RunInThreadAsync(async () =>
   {
    var workerThread=Environment.CurrentManagedThreadId;
    var pcm=await AudioMusicSourcePreparation.PrepareOwnedAsync(decoded,(int)start,(int)count,master,track,GameTask.Yield,cancellation.Token,sourceRate);
    return (Pcm:pcm,OffThread:workerThread!=callerThread);
   }); } finally { PreparationInProgress=false; }
   if(closed)throw new ObjectDisposedException(nameof(AudioMusicSourceBank));
   LastPreparationOffThread=work.OffThread;
   var result=work.Pcm;
   prepared.Add(key,result);residentBytes+=result.Length*2L;
   if(AudioMusicPreparationCache.Enabled && generation==AudioMusicPreparationCache.Generation)
    AudioMusicPreparationCache.Ranges.Add(key,result);
   return result;
  }
  finally { gate.Release(); }
 }
 /// <summary>Compose only ranges owned by this bank. Cache the complete result within the same budget.</summary>
 internal async Task<short[]> ComposeAsync(IReadOnlyList<AudioMusicPhraseComposer.Hit> hits,double bpm)
 {
  ArgumentNullException.ThrowIfNull(hits);
  if(hits.Count is <1 or >3)throw new ArgumentException("A percussion set requires one to three hits.",nameof(hits));
  var snapshot=hits.ToArray();
  var key=new CompoundKey(snapshot[0],snapshot.Length>1?snapshot[1]:null,snapshot.Length>2?snapshot[2]:null,bpm);
  await gate.WaitAsync(cancellation.Token);
  try
  {
   if(closed)throw new ObjectDisposedException(nameof(AudioMusicSourceBank));
   if(snapshot.Any(hit=>!prepared.Values.Any(pcm=>ReferenceEquals(pcm,hit.Samples))))
    throw new ArgumentException("Compound hits must use prepared ranges owned by this source bank.",nameof(hits));
   if(compounds.TryGetValue(key,out var cached))return cached;
   var available=maximumBytes-residentBytes-8192;
   if(available<=0){LastBudgetRejection="No working-memory budget remains for a compound phrase.";return null;}
   var callerThread=Environment.CurrentManagedThreadId;
   PreparationInProgress=true;
   (short[] Pcm,bool OffThread) work;
   try
   {
    work=await GameTask.RunInThreadAsync(()=>
     (Pcm:AudioMusicPhraseComposer.Compose(snapshot,bpm,available,cancellation.Token),OffThread:Environment.CurrentManagedThreadId!=callerThread));
   }
   finally {PreparationInProgress=false;}
   if(closed)throw new ObjectDisposedException(nameof(AudioMusicSourceBank));
   LastPreparationOffThread=work.OffThread;
   compounds.Add(key,work.Pcm);residentBytes+=work.Pcm.Length*2L;
   return work.Pcm;
  }
  finally {gate.Release();}
 }
 public void Dispose(){closed=true;cancellation.Cancel();prepared.Clear();compounds.Clear();residentBytes=0;}
}
