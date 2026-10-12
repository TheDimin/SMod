namespace Smod;

/// <summary>A bounded echo on a local sound instrument. Captured when the voice starts.</summary>
public sealed class AudioEchoEffect
{
 [Property, Description("Shared stems only: play buffered sound when the parent closes this instrument. No new source notes enter the tail.")] public bool ReleaseOnly {get;set;}
 [Property, Range(.02f,.5f), Description("Seconds between repeats. Applied on the next voice start.")]
 public float DelaySeconds {get;set;}=.22f;
 [Property, Range(0,.6f), Description("Level of the first repeat. The original sound is unchanged.")]
 public float WetGain {get;set;}=.25f;
 [Property, Range(0,.75f), Description("Fraction retained by each following repeat. Lower values decay faster.")]
 public float Feedback {get;set;}=.4f;
 [Property, Range(.1f,4f), Description("Maximum seconds of echo after the source stops. The final 150 ms fade to silence. Does not delay completion triggers.")]
 public float TailSeconds {get;set;}=1.4f;
 internal bool IsValid => float.IsFinite(DelaySeconds)&&DelaySeconds>=.02f&&DelaySeconds<=.5f
  &&float.IsFinite(WetGain)&&WetGain>=0&&WetGain<=.6f&&float.IsFinite(Feedback)&&Feedback>=0&&Feedback<=.75f
  &&float.IsFinite(TailSeconds)&&TailSeconds>=.1f&&TailSeconds<=4;
}

internal sealed class AudioEchoDelay
{
 readonly float[] ring;
 readonly float wet,feedback;
 int cursor;
 internal AudioEchoDelay(AudioEchoEffect effect,int rate=44100)
 {
  if(effect is null||!effect.IsValid||rate<8000)throw new ArgumentOutOfRangeException(nameof(effect));
  ring=new float[Math.Max(1,(int)Math.Round(effect.DelaySeconds*rate))];wet=effect.WetGain;feedback=effect.Feedback;
 }
 internal void Process(Span<float> samples)
 {
  for(var i=0;i<samples.Length;i++)
  {
   var dry=samples[i];var delayed=ring[cursor];ring[cursor]=dry+delayed*feedback;
   samples[i]=dry+delayed*wet;if(++cursor==ring.Length)cursor=0;
  }
 }
}

internal sealed class AudioEchoProcessor : Sandbox.Audio.AudioProcessor<AudioEchoProcessor.State>
{
 internal sealed class State : ListenerState {internal AudioEchoDelay[] Channels;internal long Position,BlockPosition;}
 readonly AudioEchoEffect effect;
 long frames,tailStart=-1;
 int paused,suppressed,killed;
 internal bool Paused {get=>System.Threading.Interlocked.CompareExchange(ref paused,0,0)!=0;set=>System.Threading.Interlocked.Exchange(ref paused,value?1:0);}
 internal bool Suppressed {get=>System.Threading.Interlocked.CompareExchange(ref suppressed,0,0)!=0;set=>System.Threading.Interlocked.Exchange(ref suppressed,value?1:0);}
 internal bool Killed {get=>System.Threading.Interlocked.CompareExchange(ref killed,0,0)!=0;set=>System.Threading.Interlocked.Exchange(ref killed,value?1:0);}
 internal AudioEchoProcessor(AudioEchoEffect value)=>effect=new(){DelaySeconds=value.DelaySeconds,WetGain=value.WetGain,Feedback=value.Feedback,TailSeconds=value.TailSeconds};
 internal void BeginTail()=>System.Threading.Interlocked.CompareExchange(ref tailStart,System.Threading.Interlocked.Read(ref frames),-1);
 internal bool Finished=>Killed || System.Threading.Interlocked.Read(ref tailStart)>=0 && System.Threading.Interlocked.Read(ref frames)-System.Threading.Interlocked.Read(ref tailStart)>=effect.TailSeconds*44100;
 protected override void ProcessSingleChannel(Sandbox.Audio.AudioChannel channel,Span<float> input)=>ProcessChannel(CurrentState,channel.Get(),input);
 internal void ProcessChannel(State state,int index,Span<float> input)
 {
  if(Killed){input.Clear();return;}
  if(Paused){input.Clear();return;}
  if(index<0||index>=8)return;
  state.Channels??=Enumerable.Range(0,8).Select(_=>new AudioEchoDelay(effect)).ToArray();
  if(index==0)state.BlockPosition=state.Position;
  var start=System.Threading.Interlocked.Read(ref tailStart);var position=state.BlockPosition;
  state.Channels[index].Process(input);
  if(start>=0)
   for(var i=0;i<input.Length;i++)input[i]*=(float)Math.Clamp((effect.TailSeconds*44100-(position-start+i))/(.15*44100),0,1);
  if(Suppressed)input.Clear();
  if(index==1){state.Position+=input.Length;System.Threading.Interlocked.Exchange(ref frames,state.Position);}
 }
}

internal interface IAudioEchoVoice
{
 bool EchoActive {get;}
 void SetEcho(AudioEchoEffect effect);
 void CancelEcho();
 void PauseEcho(bool paused);
 void SuppressEcho(bool suppressed);
}
