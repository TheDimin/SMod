using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod] public void MusicEventBacking_RuntimeMixMatchesNativeFadesAndAuditionMute()
 {
  var e=CreateEvent();e.MasterTrack.Volume=.6f;
  var instrument=e.MasterTrack.Instruments[0];instrument.TrimEnd=2.4f;
  instrument.FadeInDuration=1;instrument.FadeInCurveMidpoint=.2f;
  instrument.FadeOutDuration=1;instrument.FadeOutCurveMidpoint=.8f;
  var parameters=new Dictionary<AudioParameter,AudioParameterValue>();
  var backing=new AudioMusicEventBacking(e,new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},parameters);
  var instance=AudioSystem.GetEvent(e).CreateInstance();instance.Volume=.7f;instance.Start();
  try
  {
   foreach(var position in new[]{0f,.25f,.5f,1.2f,1.8f,2.2f})
   {
    instance.TimelinePosition=position;
    var actual=backing.EvaluateGains(parameters,instance.CaptureSharedMusicMix())[0]*instance.Volume;
    Assert.AreEqual(_backend.Voices.Last().Volume,actual,.00001f,$"Gain mismatch at {position} seconds.");
   }
   instance.SetTrackMutedForAudition(e.MasterTrack,true);
   var muted=instance.CaptureSharedMusicMix();
   Assert.AreEqual(0f,backing.EvaluateGains(parameters,muted)[0]);
   instance.SetTrackMutedForAudition(e.MasterTrack,false);
   Assert.AreEqual(0f,backing.EvaluateGains(parameters,muted)[0],"A captured mix must not follow later editor changes.");
   Assert.IsTrue(backing.EvaluateGains(parameters,instance.CaptureSharedMusicMix())[0]>0);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] public void MusicEventBacking_RuntimeMixCopiesTrackGainsAndPreservesSourceGain()
 {
  var e=CreateEvent();var instrument=e.MasterTrack.Instruments[0];instrument.TrimEnd=2.4f;
  var values=new Dictionary<AudioParameter,AudioParameterValue>();
  var backing=new AudioMusicEventBacking(e,new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},values);
  var gains=new Dictionary<AudioTrack,float>{{e.MasterTrack,.25f}};
  var mix=new AudioMusicMixState(1.2f,gains);gains[e.MasterTrack]=0;
  Assert.AreEqual(backing.EvaluateGains(values)[0]*.25f,backing.EvaluateGains(values,mix)[0],.00001f);
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>new AudioMusicMixState(float.NaN));
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>new AudioMusicMixState(0,new Dictionary<AudioTrack,float>{{e.MasterTrack,2}}));
 }
 [TestMethod] public void MusicEventBacking_WorldFactsChangeGainsWithoutChangingSources()
 {
  var e=CreateEvent();var pressure=new AudioParameter{Name="Pressure",Minimum=0,Maximum=1};e.Parameters.Add(pressure);
  e.MasterTrack.Instruments[0].TrimEnd=2.4f;
  var instrument=new AudioInstrument{Sound=CreateSoundEvent(),TrimEnd=2.4f};
  e.Tracks.Add(new(){Name="Busy keys",Instruments=new(){instrument},
   Conditions=new(){new(){Parameter=pressure,Minimum=.5f,Maximum=1}},
   Automations=new(){new(){Parameter=pressure,Points=new(){new(){Input=0,Output=0},new(){Input=1,Output=.5f}}}}});
  var values=new Dictionary<AudioParameter,AudioParameterValue>{{pressure,new(0,null,false)}};
  var binding=new AudioMusicEventBacking(e,new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100},values);
  Assert.AreEqual(2,binding.Parts.Count,"Initially inactive parts must still be prepared.");
  CollectionAssert.AreEqual(new float[]{1,0},binding.EvaluateGains(values));
  var selected=binding.Parts[1].Source;
  values[pressure]=new(1,null,false);
  CollectionAssert.AreEqual(new float[]{1,.5f},binding.EvaluateGains(values));
  values[pressure]=new(0,null,false);
  CollectionAssert.AreEqual(new float[]{1,0},binding.EvaluateGains(values));
  Assert.AreSame(selected,binding.Parts[1].Source);
 }
 [TestMethod] public void MusicEventBacking_ExcludesShortPhrasesAndRejectsPitchChanges()
 {
  var e=CreateEvent();e.MasterTrack.Instruments[0].TrimEnd=2.4f;
  e.Tracks.Add(new(){Instruments=new(){new(){Sound=CreateSoundEvent(),TrimEnd=.6f}}});
  var grid=new AudioBeatGrid{End=new(2.4),BeatsPerMinute=100};
  var values=new Dictionary<AudioParameter,AudioParameterValue>();
  Assert.AreEqual(1,new AudioMusicEventBacking(e,grid,values).Parts.Count);
  e.MasterTrack.Pitch=.9f;
  Assert.ThrowsExactly<InvalidOperationException>(()=>new AudioMusicEventBacking(e,grid,values));
 }
}
