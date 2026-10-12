using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Sandbox;
using Sandbox.Audio;
using Smod;
namespace Editor.Mcp;

// Editor only. Input is copied for stereo pairing but never changed or retained as audio.
internal sealed class AudioSessionMeterTap : AudioProcessor<AudioSessionMeterTap.State>
{
 public sealed class State : ListenerState
 {
  internal readonly float[] Left=new float[8192];
  internal int LeftCount,Id;
  internal float BlockGain;
  internal AudioLevelStatistics Statistics=new();
  internal readonly AudioLevelStatistics[] Channels=new AudioLevelStatistics[8];
  public State(){for(var i=0;i<Channels.Length;i++)Channels[i]=new AudioLevelStatistics();}
 }
 internal sealed record Window(int Listener,int Channel,long EndTicks,AudioLevelReading Levels,float DownstreamGain);
 readonly ConcurrentQueue<Window> windows=new();
 readonly int frames;
 readonly Mixer master;
 int nextListener,queued;
 internal int Dropped,UnsupportedChannels,PairingErrors;
 internal volatile bool Removed;
 internal AudioSessionMeterTap(Mixer master,double seconds){this.master=master;frames=(int)Math.Round(seconds*44100);}
 protected override void ProcessSingleChannel(AudioChannel channel,Span<float> input)
 {
  if(Removed)return;
  Observe(CurrentState,channel.Get(),input,master.Volume*Sound.MasterVolume);
 }
 internal void Observe(State state,int c,ReadOnlySpan<float> input,float downstreamGain)
 {
  if(state.Id==0)state.Id=Interlocked.Increment(ref nextListener);
  if(c<0||c>7){Interlocked.Increment(ref UnsupportedChannels);return;}
  if(c==0)state.BlockGain=downstreamGain;
  var gain=state.BlockGain;
  foreach(var sample in input)
  {
   state.Channels[c].Add(sample*gain,sample*gain);
   if(state.Channels[c].Frames>=frames){Publish(state.Id,c,state.Channels[c],gain);state.Channels[c]=new();}
  }
  if(c>1)return;
  if(input.Length>state.Left.Length){Interlocked.Increment(ref PairingErrors);return;}
  if(c==0){input.CopyTo(state.Left);state.LeftCount=input.Length;return;}
  if(c!=1 || state.LeftCount!=input.Length){Interlocked.Increment(ref PairingErrors);return;}
  for(var i=0;i<input.Length;i++)
  {
   state.Statistics.Add(state.Left[i]*gain,input[i]*gain);
   if(state.Statistics.Frames<frames)continue;
   Publish(state.Id,-1,state.Statistics,gain);
   state.Statistics=new();
  }
  state.LeftCount=0;
 }
 void Publish(int listener,int channel,AudioLevelStatistics levels,float gain)
 {
  if(Interlocked.Increment(ref queued)<=256)windows.Enqueue(new(listener,channel,Stopwatch.GetTimestamp(),levels.Read(),gain));
  else {Interlocked.Decrement(ref queued);Interlocked.Increment(ref Dropped);}
 }
 internal bool TryRead(out Window window)
 {
  if(!windows.TryDequeue(out window))return false;
  Interlocked.Decrement(ref queued);return true;
 }
 protected override void OnDestroy(){Removed=true;}
}
