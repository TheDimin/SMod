namespace Smod;

/// <summary>48 kHz stereo input on the shared 44.1 kHz clock. No source files are changed.</summary>
internal static class AudioMusicResampler
{
 const int Radius=64, Phases=147;
 internal const int ScratchBytes=Phases*Radius*2*sizeof(double);
 static readonly double[][] weights=BuildWeights();
 static double[][] BuildWeights()
 {
  var result=new double[Phases][];
  const double cutoff=44100d/48000*.97;
  for(var phase=0;phase<Phases;phase++)
  {
   var row=new double[Radius*2];double sum=0;
   for(var tap=0;tap<row.Length;tap++)
   {
    var x=tap-Radius+1-phase/(double)Phases;
    var window=.42+.5*Math.Cos(Math.PI*x/Radius)+.08*Math.Cos(2*Math.PI*x/Radius);
    var sinc=Math.Abs(x)<1e-12?cutoff:Math.Sin(Math.PI*cutoff*x)/(Math.PI*x);
    sum+=row[tap]=sinc*window;
   }
   for(var tap=0;tap<row.Length;tap++)row[tap]/=sum;
   result[phase]=row;
  }
  return result;
 }
 internal static long OutputFrames(long inputFrames,int rate)
 {
  if(rate is not (44100 or 48000))throw new ArgumentOutOfRangeException(nameof(rate),"Shared music supports 44100 or 48000 Hz sources.");
  return (long)Math.Round(inputFrames*(44100d/rate));
 }
 internal static float Sample(short[] source,int outputFrame,int channel)
 {
  var numerator=(long)outputFrame*160;
  var center=numerator/Phases;
  var row=weights[numerator%Phases];double value=0;
  var last=source.Length/2-1;
  for(var tap=0;tap<row.Length;tap++)
   value+=source[(int)Math.Clamp(center+tap-Radius+1,0,last)*2+channel]*row[tap];
  return (float)(value/32768d);
 }
}
