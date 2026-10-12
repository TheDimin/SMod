namespace Smod;

/// <summary>Stereo sample statistics. No weighting, gating, oversampling, or audio storage.</summary>
public sealed class AudioLevelStatistics
{
 public long Frames {get;private set;}
 public long ClippedFrames {get;private set;}
 public long InvalidFrames {get;private set;}
 double leftEnergy,rightEnergy,monoEnergy,cross,peak;
 public void Add(float left,float right)
 {
  Frames++;
  if(!float.IsFinite(left)||!float.IsFinite(right)){InvalidFrames++;return;}
  var l=(double)left;var r=(double)right;
  leftEnergy+=l*l;rightEnergy+=r*r;cross+=l*r;monoEnergy+=(l+r)*(l+r)*.25;
  peak=Math.Max(peak,Math.Max(Math.Abs(l),Math.Abs(r)));
  if(Math.Abs(l)>=1 || Math.Abs(r)>=1)ClippedFrames++;
 }
 public AudioLevelReading Read()
 {
  var count=Math.Max(1,Frames-InvalidFrames);
  var rms=Math.Sqrt((leftEnergy+rightEnergy)/(2*count));
  var mono=Math.Sqrt(monoEnergy/count);
  return new(Frames,peak,rms,Decibels(peak),Decibels(rms),Decibels(mono),
   rms>0?Decibels(mono)-Decibels(rms):null,
   leftEnergy>0&&rightEnergy>0?Math.Clamp(cross/Math.Sqrt(leftEnergy*rightEnergy),-1,1):null,ClippedFrames,InvalidFrames);
 }
 public static double Decibels(double amplitude)=>amplitude>0?20*Math.Log10(amplitude):-160;
}
public sealed record AudioLevelReading(long Frames,double SamplePeak,double Rms,double PeakDbfs,double RmsDbfs,double MonoRmsDbfs,double? MonoLossDb,double? StereoCorrelation,long ClippedFrames,long InvalidFrames);
