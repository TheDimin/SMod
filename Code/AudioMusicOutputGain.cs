using System.Threading;
using System.Threading.Tasks;
namespace Smod;
/// <summary>Output gain after the shared renderer. Keeps the native mixer active at mute.</summary>
internal sealed class AudioMusicOutputGain : Sandbox.Audio.AudioProcessor<AudioMusicOutputGain.State>
{
 internal sealed class Fade
 {
  internal float Initial;
  internal int Frames;
  internal readonly TaskCompletionSource<bool> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
 }
 internal sealed class State : ListenerState
 {
  internal readonly float[] Current=new float[8];
  internal readonly bool[] Initialized=new bool[8];
  internal readonly Fade[] Applied=new Fade[8];
  internal readonly float[] FadeStart=new float[8];
  internal readonly int[] Remaining=new int[8];
  internal float BlockTarget;
  internal Fade BlockFade;
 }
 float target=1;
 Fade fade;
 public float Volume
 {
  get=>target;
  set {if(!float.IsFinite(value)||value<0||value>1)throw new ArgumentOutOfRangeException(nameof(value));CancelFade();target=value;}
 }
 internal Task<bool> FadeOutAsync(int frames)
 {
  if(frames<1 || frames>44100*60)throw new ArgumentOutOfRangeException(nameof(frames));
  var next=new Fade{Initial=target,Frames=frames};
  Interlocked.Exchange(ref fade,next)?.Completion.TrySetResult(false);
  return next.Completion.Task;
 }
 internal void CancelFade()=>Interlocked.Exchange(ref fade,null)?.Completion.TrySetResult(false);
 protected override void ProcessSingleChannel(Sandbox.Audio.AudioChannel channel,Span<float> input)=>MixChannel(CurrentState,channel.Get(),input);
 internal void MixChannel(State state,int channel,Span<float> input)
 {
  if(channel<0 || channel>=8)return;
  if(channel==0){state.BlockTarget=target;state.BlockFade=Interlocked.CompareExchange(ref fade,null,null);}
  var active=state.BlockFade;var wanted=state.BlockTarget;
  if(!state.Initialized[channel]){state.Current[channel]=active?.Initial??wanted;state.Initialized[channel]=true;}
  var current=state.Current[channel];
  if(active is not null && !ReferenceEquals(state.Applied[channel],active))
  {
   state.Applied[channel]=active;state.FadeStart[channel]=current;state.Remaining[channel]=active.Frames;
  }
  if(active is null && current==0 && wanted==0){input.Clear();return;}
  const float step=1f/882f;
  for(var i=0;i<input.Length;i++)
  {
   if(active is not null)
   {
    state.Remaining[channel]=Math.Max(0,state.Remaining[channel]-1);
    current=state.FadeStart[channel]*(state.Remaining[channel]/(float)active.Frames);
   }
   else current=current<wanted?Math.Min(wanted,current+step):Math.Max(wanted,current-step);
   input[i]*=current;
  }
  state.Current[channel]=current;
  if(channel==1 && active is not null && state.Remaining[0]==0 && state.Remaining[1]==0)active.Completion.TrySetResult(true);
 }
}
