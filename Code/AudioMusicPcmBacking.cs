namespace Smod;

/// <summary>Immutable stereo sources placed on one repeating sample clock. Short clips can reuse the same PCM array.</summary>
internal sealed class AudioMusicPcmBacking
{
 internal sealed record Stem(short[] Pcm, float Gain, int StartFrame=0, AudioInstrument Envelope=null,AudioEchoEffect Echo=null);
 readonly short[][] stems;
 readonly int[] starts;
 readonly AudioInstrument[] envelopes;
 readonly float[] currentGains;
 sealed record Targets(float[] Gains,bool[] Tails);
 Targets targetGains;
 readonly AudioMusicReleaseEcho[] echoes;
 long previousEnd=-1;
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
  echoes=new AudioMusicReleaseEcho[sources.Count];
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
   if(stem.Echo is {ReleaseOnly:true} echo)echoes[index]=new(echo);
   if(stem.Envelope is {} envelope)envelopes[index]=new AudioInstrument{FadeInDuration=envelope.FadeInDuration,FadeInCurveMidpoint=envelope.FadeInCurveMidpoint,FadeOutDuration=envelope.FadeOutDuration,FadeOutCurveMidpoint=envelope.FadeOutCurveMidpoint};
   ValidateGain(stem.Gain);stems[index]=stem.Pcm;currentGains[index]=stem.Gain;
  }
  FrameCount=compositionFrames>0?compositionFrames:stems[0].Length/2;targetGains=new(currentGains.ToArray(),Enumerable.Repeat(true,sources.Count).ToArray());
 }
 static void ValidateGain(float gain)
 {
  if(!float.IsFinite(gain)||gain<0)throw new ArgumentOutOfRangeException(nameof(gain));
 }
 // Session thread publishes one immutable target set. Rendering takes one snapshot per stereo block.
 public void SetGains(IReadOnlyList<float> gains,IReadOnlyList<bool> tails=null)
 {
  ArgumentNullException.ThrowIfNull(gains);
  if(gains.Count!=stems.Length)throw new ArgumentException("Provide one gain for each backing slot.",nameof(gains));
  var copy=new float[gains.Count];
  for(var index=0;index<copy.Length;index++){ValidateGain(gains[index]);copy[index]=gains[index];}
  if(tails is not null && tails.Count!=gains.Count)throw new ArgumentException("One tail permission per slot is required.");
  System.Threading.Interlocked.Exchange(ref targetGains,new Targets(copy,tails?.ToArray()??Enumerable.Repeat(true,gains.Count).ToArray()));
 }
 internal void ResetEffects(){if(echoes is not null)foreach(var echo in echoes)echo?.Reset();}
 public void Cancel() => owner.Cancel();
 // Render thread only. Silent stems still follow position; pause holds all gain ramps.
 internal void Render(Span<float> output,long position)
 {
  if(previousEnd>=0 && previousEnd!=position)ResetEffects();
  previousEnd=position+output.Length/2;
  if(owner.IsCancelled && release<0)release=ReleaseFrames;
  var targets=System.Threading.Interlocked.CompareExchange(ref targetGains,null,null);
  for(var i=0;i<output.Length/2;i++)
  {
   if(release==0)return;
   var frame=(int)((position+i)%FrameCount);
   var envelope=release<0 ? 1 : release/(float)ReleaseFrames;
   for(var stem=0;stem<stems.Length;stem++)
   {
    // Retained renderers can predate these fields after an editor code reload.
    var target=targets?.Gains?[stem] ?? currentGains[stem];
    currentGains[stem]+=Math.Clamp(target-currentGains[stem],-GainStep,GainStep);
    var local=frame-starts[stem];
    var inRange=local>=0 && local<stems[stem].Length/2;
    var level=currentGains[stem];
    if(envelopes[stem] is {} authored)level*=AudioInstrumentFade.Gain(authored,local/44100f,stems[stem].Length/88200f);
    float l=inRange?stems[stem][local*2]/32768f*level:0;
    float r=inRange?stems[stem][local*2+1]/32768f*level:0;
    if(echoes is not null)echoes[stem]?.Process(ref l,ref r,inRange&&target>0,(targets?.Tails?[stem]??true)&&!owner.IsCancelled);
    output[i*2]+=l*envelope;output[i*2+1]+=r*envelope;
   }
   if(release>0)release--;
  }
 }
}
