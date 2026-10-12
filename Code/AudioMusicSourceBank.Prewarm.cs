using System.Threading.Tasks;
namespace Smod;

internal sealed partial class AudioMusicSourceBank
{
 /// <summary>Decode each required source at most once for this batch. Native reads stay
 /// on the owning context; only managed range processing runs on a worker.</summary>
 internal async Task<bool> PrewarmAsync(IReadOnlyList<AudioMusicPrewarmRange> ranges,AudioTone master=null)
 {
  ArgumentNullException.ThrowIfNull(ranges);
  if(master is {IsValid:false})throw new ArgumentException("Invalid master tone.");
  var masterCopy=new AudioTone{BassGainDb=master?.BassGainDb??0,BassFrequencyHz=master?.BassFrequencyHz??180};
  var snapshot=ranges.Select(range=>
  {
   if(range?.Source is null || !float.IsFinite(range.Offset) || range.Offset<0 || !float.IsFinite(range.Length) || range.Length<0 || range.Tone is {IsValid:false})
    throw new ArgumentException("Invalid prewarm source range.");
   return range with{Tone=new AudioTone{BassGainDb=range.Tone?.BassGainDb??0,BassFrequencyHz=range.Tone?.BassFrequencyHz??180}};
  }).ToArray();
  await gate.WaitAsync(cancellation.Token);
  try
  {
   if(closed)throw new ObjectDisposedException(nameof(AudioMusicSourceBank));
   PreparationInProgress=true;
   foreach(var group in snapshot.GroupBy(range=>range.Source))
   {
    var source=group.Key;
    cancellation.Token.ThrowIfCancellationRequested();
    if(!await source.LoadAsync())throw new InvalidOperationException("Music prewarm source is not ready.");
    cancellation.Token.ThrowIfCancellationRequested();
    if(source.Rate!=44100 || source.Channels!=2 || !float.IsFinite(source.Duration) || source.Duration<=0)
     throw new InvalidOperationException("Shared music requires stereo 44100 Hz PCM.");
    var frames=(long)Math.Round(source.Duration*44100d);
    if(frames>int.MaxValue/2)throw new InvalidOperationException("Music source is too large.");
    var requests=group.Select(range=>
    {
     var start=(long)Math.Round(range.Offset*44100d);
     var count=range.Length==0?frames-start:(long)Math.Round(range.Length*44100d);
     if(start<0 || count<1 || start+count>frames)throw new ArgumentException("Prewarm range exceeds its source.");
     return new Key(source,(int)start,(int)count,masterCopy.BassGainDb,masterCopy.BassFrequencyHz,range.Tone.BassGainDb,range.Tone.BassFrequencyHz);
    }).Distinct().Where(key=>!prepared.ContainsKey(key)).ToArray();
    if(requests.Length==0)continue;
    var generation=AudioMusicPreparationCache.Generation;
    if(AudioMusicPreparationCache.Enabled)
    {
     foreach(var key in requests)
      if(AudioMusicPreparationCache.Ranges.TryGet(key,out var cached))
      {
       if(!Admit(0,cached.Length/2))return false;
       prepared.Add(key,cached);residentBytes+=cached.Length*2L;
      }
     requests=requests.Where(key=>!prepared.ContainsKey(key)).ToArray();
     if(requests.Length==0)continue;
    }
    var neededEnd=requests.Max(key=>(long)key.Start+key.Count);
    // An existing neutral backing can supply source samples for its short phrases.
    var borrowed=prepared.FirstOrDefault(pair=>ReferenceEquals(pair.Key.Source,source) && pair.Key.Start==0
     && pair.Key.Count>=neededEnd && pair.Key.MasterDb==0 && pair.Key.TrackDb==0).Value;
    var outputFrames=requests.Sum(key=>(long)key.Count);
    if(!Admit(borrowed is null?frames+1024:0,outputFrames))return false;
    short[] decoded=borrowed;
    if(decoded is null)
    {
     cancellation.Token.ThrowIfCancellationRequested();
     NativeDecodeCount++;AudioMusicPreparationCache.NativeDecodes++;
     decoded=await source.GetSamplesInterleavedAsync();
    }
    cancellation.Token.ThrowIfCancellationRequested();
    if(decoded is null || decoded.Length%2!=0 || neededEnd>decoded.Length/2)
     throw new InvalidOperationException("Decoded prewarm source does not contain all authored ranges.");
    if(!Admit(borrowed is null?decoded.Length/2:0,outputFrames))return false;
    var callerThread=Environment.CurrentManagedThreadId;
    var work=await GameTask.RunInThreadAsync(async()=>
    {
     var workerThread=Environment.CurrentManagedThreadId;
     var outputs=new short[requests.Length][];
     for(var i=0;i<requests.Length;i++)
     {
      var key=requests[i];
      outputs[i]=await AudioMusicSourcePreparation.PrepareAsync(decoded,key.Start,key.Count,masterCopy,
       new AudioTone{BassGainDb=key.TrackDb,BassFrequencyHz=key.TrackHz},GameTask.Yield,cancellation.Token);
     }
     return (Outputs:outputs,OffThread:workerThread!=callerThread);
    });
    cancellation.Token.ThrowIfCancellationRequested();
    LastPreparationOffThread=work.OffThread;
    for(var i=0;i<requests.Length;i++)
    {
     prepared.Add(requests[i],work.Outputs[i]);
     residentBytes+=work.Outputs[i].Length*2L;
     if(AudioMusicPreparationCache.Enabled && generation==AudioMusicPreparationCache.Generation)
      AudioMusicPreparationCache.Ranges.Add(requests[i],work.Outputs[i]);
    }
   }
   return true;
  }
  finally{PreparationInProgress=false;gate.Release();}
 }
}
