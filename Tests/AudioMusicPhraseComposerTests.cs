using Smod;
using System.Threading;
public sealed partial class AudioEventTests
{
 [TestMethod] public void CompoundInstrument_RejectsUnsupportedTracksAndMalformedHits()
 {
  var e=SharedOverlaySong();var clip=e.Tracks[0].Instruments[0];
  clip.CompoundHits.Add(new(){Length=.14f});clip.CompoundHits.Add(new(){Length=.14f,BeatOffset=2});
  Assert.IsTrue(AudioEventValidator.Validate(e).IsValid);
  e.SharedMusicOutput=false;Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);e.SharedMusicOutput=true;
  clip.OverlayBodySeconds=.1f;Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);clip.OverlayBodySeconds=0;
  clip.CompoundHits[1].BeatOffset=float.NaN;Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);clip.CompoundHits[1].BeatOffset=2;
  clip.CompoundHits[1].Gain=2;Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);clip.CompoundHits[1].Gain=1;
  clip.CompoundHits[1].Length=0;Assert.IsFalse(AudioEventValidator.Validate(e).IsValid);
 }
 [TestMethod] public void CompoundInstrument_LiveDurationIncludesEveryHitAndItsTail()
 {
  var e=SharedOverlaySong();var clip=e.Tracks[0].Instruments[0];
  clip.CompoundHits.Add(new(){Length=.14f});clip.CompoundHits.Add(new(){Length=.14f,BeatOffset=2});
  var session=new EventMusicSessionFake();session.Loaded.SetResult(true);session.Phrase.SetResult(new(26460,System.Threading.Tasks.Task.FromResult(true)));
  var instance=AudioSystem.GetEvent(e).CreateInstance();instance.SharedMusicSessionFactory=()=>session;
  try
  {
   instance.Start();instance.SetParameterByName("Service",1);
   var row=instance.CaptureLiveSnapshot().Voices.Single(v=>v.InstrumentId==clip.Id);
   Assert.AreEqual(1.34f,row.End-row.Start,.0001f);
  }
  finally {instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }
 [TestMethod] [DataRow(100d)] [DataRow(110d)]
 public void CompoundPhrase_PlacesStereoHitsOnDestinationBeatsAndKeepsLastTail(double bpm)
 {
  var first=new short[]{100,200,50,75};var last=new short[]{300,400,150,200,75,100};
  var hits=new[]{new AudioMusicPhraseComposer.Hit(first,0,1),new AudioMusicPhraseComposer.Hit(first,1,.5f),new AudioMusicPhraseComposer.Hit(last,2,1)};
  var result=AudioMusicPhraseComposer.Compose(hits,bpm,4_000_000);
  var one=(int)Math.Round(60d/bpm*44100);var two=(int)Math.Round(120d/bpm*44100);
  Assert.AreEqual(two*2+last.Length,result.Length);
  CollectionAssert.AreEqual(first,result.Take(first.Length).ToArray());
  Assert.AreEqual((short)50,result[one*2]);Assert.AreEqual((short)100,result[one*2+1]);
  CollectionAssert.AreEqual(last,result.Skip(two*2).ToArray());
  Assert.AreEqual((short)0,result[one*2-1]);
  CollectionAssert.AreEqual(new short[]{100,200,50,75},first,"Input sources stay unchanged.");
 }
 [TestMethod] public void CompoundPhrase_SumsOverlapWithoutTruncatingEitherHit()
 {
  var hits=new[]{new AudioMusicPhraseComposer.Hit(new short[]{1000,-1000,500,-500},0,.5f),new AudioMusicPhraseComposer.Hit(new short[]{2000,-2000},0,.25f)};
  CollectionAssert.AreEqual(new short[]{1000,-1000,250,-250},AudioMusicPhraseComposer.Compose(hits,110,1000));
 }
 [TestMethod] public void CompoundPhrase_RejectsClippingInvalidDataBudgetAndCancellation()
 {
  var hit=new AudioMusicPhraseComposer.Hit(new short[]{20000,20000},0,1);
  Assert.ThrowsExactly<InvalidOperationException>(()=>AudioMusicPhraseComposer.Compose(new[]{hit,hit},110,1000));
  Assert.ThrowsExactly<InvalidOperationException>(()=>AudioMusicPhraseComposer.Compose(new[]{hit},110,1));
  Assert.ThrowsExactly<ArgumentException>(()=>AudioMusicPhraseComposer.Compose(new[]{hit,hit,hit,hit},110,1000));
  Assert.ThrowsExactly<ArgumentException>(()=>AudioMusicPhraseComposer.Compose(new[]{hit with{BeatOffset=double.NaN}},110,1000));
  Assert.ThrowsExactly<ArgumentException>(()=>AudioMusicPhraseComposer.Compose(new[]{hit with{Samples=new short[]{1}}},110,1000));
  Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>AudioMusicPhraseComposer.Compose(new[]{hit},0,1000));
  Assert.ThrowsExactly<OperationCanceledException>(()=>AudioMusicPhraseComposer.Compose(new[]{hit},110,1000,new CancellationToken(true)));
 }
}
