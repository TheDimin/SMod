using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Buffers.Binary;
using Sandbox;
namespace Editor.Mcp;

[McpToolset("audio_compression", "Check native compressed sound timing against unchanged AudioSource WAV files.")]
public static class AudioCompressionTimingMcpTools
{
 public sealed record Window(int SourceFrame,int? DelayFrames,double? Correlation);
 public sealed record Result(string ResourcePath,int OriginalFrames,int DecodedFrames,int Rate,Window[] Windows,bool TimingMatches);
 /// <summary>Decode one compiled project sound and its original WAV under AudioSource, in memory only. Compare one signal-bearing one-second window in each quarter for encoder delay within plus or minus 2048 frames. Reports measured delay and correlation, not listening quality. Writes no audio files.</summary>
 [McpTool.ReadOnly("check_compressed_sound_timing")]
 public static async Task<Result> Check(string resourcePath,string sourcePath)
 {
  if(!ExternalAudioSourceCatalog.IsWithinSourceRoot(sourcePath) || !sourcePath.EndsWith(".wav",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Original WAV must be under AudioSource.");
  var asset=AssetSystem.FindByPath(resourcePath);
  if(asset?.AssetType!=AssetType.SoundFile)throw new ArgumentException("A registered sound is required.");
  var sound=SoundFile.Load(asset.RelativePath);
  if(!await sound.LoadAsync())throw new InvalidOperationException("Compiled source could not load.");
  if(sound.Rate!=44100 || sound.Channels!=2)throw new InvalidOperationException("This music check requires stereo 44100 Hz.");
  var decoded=await sound.GetSamplesInterleavedAsync();
  var bytes=await File.ReadAllBytesAsync(sourcePath);
  return await GameTask.RunInThreadAsync(()=>
  {
   var original=ExternalWavPreviewDecoder.Decode(bytes);
   if(original.Rate!=44100 || original.Channels!=2)throw new InvalidOperationException("Original music must be stereo 44100 Hz.");
   var frames=original.Bytes.Length/4;var count=Math.Min(frames,decoded.Length/2);
   if(count<44100+4096)throw new InvalidOperationException("Source is too short for the four-window music check.");
   float Read(int frame,int channel)=>BinaryPrimitives.ReadInt16LittleEndian(original.Bytes.AsSpan(frame*4+channel*2,2));
   double Correlation(int start,int lag)
   {
    double xy=0,xx=0,yy=0;
    for(var i=0;i<44100;i+=16)
     for(var channel=0;channel<2;channel++)
     {
      var a=Read(start+i,channel);var b=(double)decoded[(start+i+lag)*2+channel];xy+=a*b;xx+=a*a;yy+=b*b;
     }
    return xx>0 && yy>0 ? xy/Math.Sqrt(xx*yy) : 0;
   }
   var windows=new Window[4];
   for(var w=0;w<4;w++)
   {
    // A silent window has no measurable delay. Select signal within each quarter.
    var available=count-44100-4096;
    var lower=2048+(int)(available*(w/4d));
    var upper=2048+(int)(available*((w+1)/4d));
    var start=lower;double energy=0;
    for(var candidate=lower;candidate<=upper;candidate+=11025)
    {
     double candidateEnergy=0;
     for(var i=0;i<44100;i+=128)
      for(var channel=0;channel<2;channel++){var sample=(double)Read(candidate+i,channel);candidateEnergy+=sample*sample;}
     if(candidateEnergy>energy){energy=candidateEnergy;start=candidate;}
    }
    if(energy==0){windows[w]=new(start,null,null);continue;}
    var best=double.NegativeInfinity;var lag=0;
    for(var candidate=-2048;candidate<=2048;candidate+=8)
    {var score=Correlation(start,candidate);if(score>best){best=score;lag=candidate;}}
    var coarse=lag;
    for(var candidate=Math.Max(-2048,coarse-7);candidate<=Math.Min(2048,coarse+7);candidate++)
    {var score=Correlation(start,candidate);if(score>best){best=score;lag=candidate;}}
    windows[w]=new(start,lag,best);
   }
   return new Result(resourcePath,frames,decoded.Length/2,44100,windows,
    Math.Abs(frames-decoded.Length/2)<=1 && windows.Any(x=>x.DelayFrames.HasValue) && windows.Where(x=>x.DelayFrames.HasValue).All(x=>Math.Abs(x.DelayFrames.Value)<=1 && x.Correlation>=.9));
  });
 }
}
