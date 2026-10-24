using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public async System.Threading.Tasks.Task MusicSampleRate_PreservesDurationPitchAndStereo()
 {
  var source=new short[48000*2];
  for(var i=0;i<48000;i++){source[2*i]=(short)(12000*Math.Sin(2*Math.PI*1000*i/48000));source[2*i+1]=-4000;}
  var result=await AudioMusicSourcePreparation.PrepareOwnedAsync(source,0,44100,null,null,sourceRate:48000);
  Assert.AreEqual(88200,result.Length);
  double error=0;
  for(var i=100;i<44000;i++){error+=Math.Pow(result[2*i]-12000*Math.Sin(2*Math.PI*1000*i/44100),2);Assert.AreEqual(-4000,(int)result[2*i+1]);}
  Assert.IsTrue(Math.Sqrt(error/43900)<10);
 }
 [TestMethod] public async System.Threading.Tasks.Task MusicSampleRate_RejectsAliasingAndKeepsTrimPhase()
 {
  var source=new short[96000];
  for(var i=0;i<48000;i++)source[2*i]=source[2*i+1]=(short)(12000*Math.Sin(2*Math.PI*23500*i/48000));
  var full=await AudioMusicSourcePreparation.PrepareOwnedAsync(source,0,44100,null,null,sourceRate:48000);
  var trim=await AudioMusicSourcePreparation.PrepareOwnedAsync(source,1234,500,null,null,sourceRate:48000);
  CollectionAssert.AreEqual(full.Skip(2468).Take(1000).ToArray(),trim);
  Assert.IsTrue(full.Skip(200).Take(87800).Max(v=>Math.Abs((int)v))<120,"Stop-band attenuation must exceed 40 dB.");
 }
 [TestMethod] public async System.Threading.Tasks.Task MusicSampleRate_CancellationAndBounds()
 {
  using var cancel=new System.Threading.CancellationTokenSource();cancel.Cancel();
  await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>AudioMusicSourcePreparation.PrepareOwnedAsync(new short[96000],0,44100,null,null,cancellation:cancel.Token,sourceRate:48000));
  await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(()=>AudioMusicSourcePreparation.PrepareOwnedAsync(new short[96000],1,44100,null,null,sourceRate:48000));
 }
}
