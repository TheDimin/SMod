using Smod;
public sealed partial class AudioEventTests
{
    [TestMethod] public void MusicSampleBoundary_UsesGridOriginAndExclusiveEnd()
    {
        var grid=new AudioBeatGrid{Start=new(2),End=new(8),BeatsPerMinute=100};
        Assert.AreEqual(88200L,grid.NextSampleBoundary(0,44100,4));
        Assert.AreEqual(194040L,grid.NextSampleBoundary(88201,44100,4));
        Assert.IsNull(grid.NextSampleBoundary(352800,44100,1));
    }
    [TestMethod] public void MusicSampleBoundary_DoesNotAccumulateFractionalBeatRounding()
    {
        var grid=new AudioBeatGrid{End=new(3600),BeatsPerMinute=123};
        for(var beat=1;beat<=2460;beat++)
        {
            var expected=(long)Math.Round(beat*60d/123*44100,MidpointRounding.AwayFromZero);
            Assert.AreEqual(expected,grid.NextSampleBoundary(expected,44100,1));
        }
    }
    [TestMethod] public void MusicProcessor_PublishesOnlyCompletedStereoPosition()
    {
        var core=new AudioMusicPcmRenderer(1);var processor=new AudioMusicPcmProcessor(core,4);
        processor.MixChannel(0,new float[4]);Assert.AreEqual(0L,processor.PublishedPosition);
        processor.MixChannel(1,new float[4]);Assert.AreEqual(4L,processor.PublishedPosition);
        core.Paused=true;processor.MixChannel(0,new float[4]);processor.MixChannel(1,new float[4]);Assert.AreEqual(4L,processor.PublishedPosition);
    }
}
