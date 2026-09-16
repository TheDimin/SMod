using System.Threading;
namespace Smod;

/// <summary>Builds a bounded stereo phrase from prepared hits. No decoding, playback or file writes.</summary>
internal static class AudioMusicPhraseComposer
{
 internal readonly record struct Hit(short[] Samples,double BeatOffset,float Gain);
 internal const int SampleRate=44100;
 internal static short[] Compose(IReadOnlyList<Hit> hits,double beatsPerMinute,long maximumWorkingBytes,CancellationToken cancellation=default)
 {
  ArgumentNullException.ThrowIfNull(hits);
  if(hits.Count is <1 or >3)throw new ArgumentException("A percussion set requires one to three hits.",nameof(hits));
  if(!double.IsFinite(beatsPerMinute)||beatsPerMinute<20||beatsPerMinute>400)throw new ArgumentOutOfRangeException(nameof(beatsPerMinute));
  if(maximumWorkingBytes<1)throw new ArgumentOutOfRangeException(nameof(maximumWorkingBytes));
  cancellation.ThrowIfCancellationRequested();
  var starts=new int[hits.Count];long frames=0;
  for(var i=0;i<hits.Count;i++)
  {
   var hit=hits[i];
   if(hit.Samples is null || hit.Samples.Length==0 || hit.Samples.Length%2!=0)throw new ArgumentException("Hits require complete stereo frames.",nameof(hits));
   if(!double.IsFinite(hit.BeatOffset)||hit.BeatOffset<0||hit.BeatOffset>16 || !float.IsFinite(hit.Gain)||hit.Gain<0||hit.Gain>1)
    throw new ArgumentException("Invalid hit offset or gain.",nameof(hits));
   starts[i]=checked((int)Math.Round(hit.BeatOffset*60d/beatsPerMinute*SampleRate));
   frames=Math.Max(frames,(long)starts[i]+hit.Samples.Length/2);
  }
  // Budget includes the accumulation and result arrays; input samples remain caller-owned.
  var sampleCount=checked(frames*2);
  if(sampleCount>int.MaxValue || sampleCount*(sizeof(double)+sizeof(short))>maximumWorkingBytes)
   throw new InvalidOperationException("Compound phrase exceeds its working-memory budget.");
  var mix=new double[(int)sampleCount];
  for(var i=0;i<hits.Count;i++)
  {
   var hit=hits[i];var start=starts[i]*2;
   for(var at=0;at<hit.Samples.Length;at++)
   {
    if((at&8191)==0)cancellation.ThrowIfCancellationRequested();
    mix[start+at]+=hit.Samples[at]*(double)hit.Gain;
   }
  }
  var result=new short[mix.Length];
  for(var at=0;at<mix.Length;at++)
  {
   if((at&8191)==0)cancellation.ThrowIfCancellationRequested();
   var sample=Math.Round(mix[at]);
   if(sample<short.MinValue||sample>short.MaxValue)throw new InvalidOperationException("Compound phrase clips. Reduce hit gains.");
   result[at]=(short)sample;
  }
  cancellation.ThrowIfCancellationRequested();return result;
 }
}
