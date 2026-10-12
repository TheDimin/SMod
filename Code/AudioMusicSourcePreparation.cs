using System.Threading;
using System.Threading.Tasks;
namespace Smod;

/// <summary>Prepares authored source ranges before the audio clock starts. Never called on the mixer thread.</summary>
internal static class AudioMusicSourcePreparation
{
 internal static (float Offset,float Length) ResolveRange(AudioInstrument instrument,SoundEventTrack source)
 {
  ArgumentNullException.ThrowIfNull(instrument);ArgumentNullException.ThrowIfNull(source);
  if(!float.IsFinite(source.Offset)||source.Offset<0||!float.IsFinite(source.Length)||source.Length<0
   ||!float.IsFinite(instrument.TrimStart)||instrument.TrimStart<0||!float.IsFinite(instrument.TrimEnd)||instrument.TrimEnd<0)
   throw new ArgumentException("Invalid authored music range.");
  var end=instrument.TrimEnd;
  if(source.Length>0)end=end>0 ? Math.Min(end,source.Length) : source.Length;
  if(end>0 && end<=instrument.TrimStart)throw new ArgumentException("Authored music range is empty.");
  var offset=source.Offset+instrument.TrimStart;
  if(!float.IsFinite(offset))throw new ArgumentException("Authored music offset overflow.");
  return (offset,end>0 ? end-instrument.TrimStart : 0);
 }

 // Ownership is transferred by the source bank only. General callers keep copy semantics.
 internal static bool CanRetainDecoded(long firstFrame,long frameCount,long decodedFrames,AudioTone master,AudioTone track)
  => firstFrame==0 && frameCount>0 && frameCount==decodedFrames
   && (master is null || master.IsValid && master.BassGainDb==0)
   && (track is null || track.IsValid && track.BassGainDb==0);
 internal static Task<short[]> PrepareOwnedAsync(short[] source,int firstFrame,int frameCount,AudioTone master,AudioTone track,Func<Task> yield=null,CancellationToken cancellation=default)
 {
  ArgumentNullException.ThrowIfNull(source);
  cancellation.ThrowIfCancellationRequested();
  if(source.Length%2==0 && CanRetainDecoded(firstFrame,frameCount,source.Length/2,master,track))return Task.FromResult(source);
  return PrepareAsync(source,firstFrame,frameCount,master,track,yield,cancellation);
 }

 internal static short[] Prepare(short[] source, int firstFrame, int frameCount, AudioTone master, AudioTone track)
  => PrepareAsync(source,firstFrame,frameCount,master,track).GetAwaiter().GetResult();
 internal static async Task<short[]> PrepareAsync(short[] source,int firstFrame,int frameCount,AudioTone master,AudioTone track,Func<Task> yield=null,CancellationToken cancellation=default)
 {
  ArgumentNullException.ThrowIfNull(source);
  if(source.Length % 2 != 0 || firstFrame < 0 || frameCount < 1 || (long)firstFrame + frameCount > source.Length / 2)
   throw new ArgumentOutOfRangeException(nameof(frameCount));
  if(master is {IsValid:false} || track is {IsValid:false}) throw new ArgumentException("Invalid authored tone settings.");
  cancellation.ThrowIfCancellationRequested();
  var result = new short[checked(frameCount * 2)];
  if((master?.BassGainDb ?? 0)==0 && (track?.BassGainDb ?? 0)==0)
  {
   for(var at=0;at<result.Length;at+=32768)
   {
    cancellation.ThrowIfCancellationRequested();
    Array.Copy(source,firstFrame*2+at,result,at,Math.Min(32768,result.Length-at));
    if(yield is not null)await yield();
   }
   cancellation.ThrowIfCancellationRequested();return result;
  }
  // Warm the filter from source context, not from a zero state at each phrase entry.
  var warmStart = Math.Max(0,firstFrame-4096);
  for(var channel=0;channel<2;channel++)
  {
   var first = new AudioBassShelf();var second = new AudioBassShelf();var block=new float[512];
   for(var at=warmStart;at<firstFrame+frameCount;at+=block.Length)
   {
    cancellation.ThrowIfCancellationRequested();
    var count=Math.Min(block.Length,firstFrame+frameCount-at);
    for(var i=0;i<count;i++)block[i]=source[(at+i)*2+channel]/32768f;
    var span=block.AsSpan(0,count);
    first.Process(span,master?.BassGainDb ?? 0,master?.BassFrequencyHz ?? 180);
    second.Process(span,track?.BassGainDb ?? 0,track?.BassFrequencyHz ?? 180);
    for(var i=0;i<count;i++)
    {
     if(at+i<firstFrame)continue;
     var sample=Math.Round(block[i]*32768d);
     if(sample<short.MinValue || sample>short.MaxValue)throw new InvalidOperationException("Tone processing exceeds PCM headroom. Reduce the authored boost.");
     result[(at+i-firstFrame)*2+channel]=(short)sample;
    }
    if(yield is not null && ((at-warmStart)/block.Length)%32==31)await yield();
   }
  }
  cancellation.ThrowIfCancellationRequested();return result;
 }
 internal static bool FitsBudget(long residentBytes,long decodedFrames,long outputFrames,long maximumBytes,bool retainDecoded=false)
 {
  if(residentBytes<0 || decodedFrames<0 || outputFrames<1 || maximumBytes<1 || residentBytes>maximumBytes)return false;
  if(maximumBytes-residentBytes<8192)return false;
  // A retained neutral full-source result needs no second array.
  if(retainDecoded) return outputFrames<=decodedFrames && decodedFrames <= (maximumBytes-residentBytes-8192)/4;
  // Includes one full decoder result, one prepared range, and fixed processing scratch.
  return decodedFrames <= (maximumBytes-residentBytes-8192)/4
   && outputFrames <= (maximumBytes-residentBytes-8192-decodedFrames*4)/4;
 }
}
