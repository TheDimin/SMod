namespace Smod;

/// <summary>Immutable stereo sources placed on one repeating sample clock. Short clips can reuse the same PCM array.</summary>
internal sealed class AudioMusicPcmBacking
{
 internal sealed record Stem(short[] Pcm, float Gain, int StartFrame=0, AudioInstrument Envelope=null);
 readonly short[][] stems;
 readonly int[] starts;
 readonly AudioInstrument[] envelopes;
 readonly float[] currentGains;
 float[] targetGains;
 readonly AudioMusicRequestOwner owner = new();
 int release = -1;
 const int ReleaseFrames = 128;
 const float GainStep = 1f / 882; // 20 ms at 44.1 kHz for a full-scale change.
 public int FrameCount { get; }
 public AudioMusicPcmBacking(short[] pcm, float gain) : this(new Stem[]{new(pcm,gain)}) { }
 public AudioMusicPcmBacking(IReadOnlyList<Stem> sources,int compositionFrames=0)
 {
  ArgumentNullException.ThrowIfNull(sources);
  if(sources.Count<1 || sources.Count>128)throw new ArgumentOutOfRangeException(nameof(sources));
  if(compositionFrames<0)throw new ArgumentOutOfRangeException(nameof(compositionFrames));
  stems=new short[sources.Count][];starts=new int[sources.Count];envelopes=new AudioInstrument[sources.Count];currentGains=new float[sources.Count];
  for(var index=0;index<sources.Count;index++)
  {
   var stem=sources[index];ArgumentNullException.ThrowIfNull(stem);ArgumentNullException.ThrowIfNull(stem.Pcm);
   if(stem.Pcm.Length==0 || stem.Pcm.Length%2!=0)throw new ArgumentException("Expected nonempty stereo PCM.",nameof(sources));
   if(compositionFrames==0 && index>0 && stem.Pcm.Length!=stems[0].Length)throw new ArgumentException("Continuous stems must have the same frame count.",nameof(sources));
   if(stem.StartFrame<0 || (compositionFrames==0 && stem.StartFrame!=0)
    || (compositionFrames>0 && (long)stem.StartFrame+stem.Pcm.Length/2>compositionFrames))
    throw new ArgumentException("A timeline stem must fit within its composition.",nameof(sources));
   starts[index]=stem.StartFrame;
   if(stem.Envelope is {} envelope)envelopes[index]=new AudioInstrument{FadeInDuration=envelope.FadeInDuration,FadeInCurveMidpoint=envelope.FadeInCurveMidpoint,FadeOutDuration=envelope.FadeOutDuration,FadeOutCurveMidpoint=envelope.FadeOutCurveMidpoint};
   ValidateGain(stem.Gain);stems[index]=stem.Pcm;currentGains[index]=stem.Gain;
  }
  FrameCount=compositionFrames>0?compositionFrames:stems[0].Length/2;targetGains=currentGains.ToArray();
 }
 static void ValidateGain(float gain)
 {
  if(!float.IsFinite(gain)||gain<0||gain>1)throw new ArgumentOutOfRangeException(nameof(gain));
 }
 // Session thread publishes one immutable target set. Rendering takes one snapshot per stereo block.
 public void SetGains(IReadOnlyList<float> gains)
 {
  ArgumentNullException.ThrowIfNull(gains);
  if(gains.Count!=stems.Length)throw new ArgumentException("Provide one gain for each backing slot.",nameof(gains));
  var copy=new float[gains.Count];
  for(var index=0;index<copy.Length;index++){ValidateGain(gains[index]);copy[index]=gains[index];}
  System.Threading.Interlocked.Exchange(ref targetGains,copy);
 }
 public void Cancel() => owner.Cancel();
 // Render thread only. Silent stems still follow position; pause holds all gain ramps.
 internal void Render(Span<float> output,long position)
 {
  if(owner.IsCancelled && release<0)release=ReleaseFrames;
  var targets=System.Threading.Interlocked.CompareExchange(ref targetGains,null,null);
  for(var i=0;i<output.Length/2;i++)
  {
   if(release==0)return;
   var frame=(int)((position+i)%FrameCount);
   var envelope=release<0 ? 1 : release/(float)ReleaseFrames;
   for(var stem=0;stem<stems.Length;stem++)
   {
    currentGains[stem]+=Math.Clamp(targets[stem]-currentGains[stem],-GainStep,GainStep);
    var local=frame-starts[stem];
    if(local<0 || local>=stems[stem].Length/2)continue;
    var source=local*2;
    var level=currentGains[stem]*envelope;
    if(envelopes[stem] is {} authored)level*=AudioInstrumentFade.Gain(authored,local/44100f,stems[stem].Length/88200f);
    output[i*2]+=stems[stem][source]/32768f*level;
    output[i*2+1]+=stems[stem][source+1]/32768f*level;
   }
   if(release>0)release--;
  }
 }
}
