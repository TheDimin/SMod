using Smod;

public sealed partial class AudioEventTests
{
	[TestMethod] public void BassShelf_ReducesBassAndPreservesHighFrequencies()
	{
		static double Gain(int frequency)
		{
			var samples = Enumerable.Range(0, 44100).Select(i => (float)(.2 * Math.Sin(2 * Math.PI * frequency * i / 44100))).ToArray();
			new AudioBassShelf().Process(samples, -6, 180);
			return 20 * Math.Log10(Math.Sqrt(samples.Skip(22050).Average(x => (double)x*x)) / (.2 / Math.Sqrt(2)));
		}
		Assert.IsTrue(Gain(40) < -5.3);
		Assert.IsTrue(Math.Abs(Gain(4000)) < .1);
	}
	[TestMethod] public void BassShelf_BypassIsExactAndBlocksKeepState()
	{
		var source = Enumerable.Range(0, 2000).Select(i => (float)Math.Sin(i * .013)).ToArray();
		var bypass = source.ToArray(); new AudioBassShelf().Process(bypass, 0, 180);
		CollectionAssert.AreEqual(source, bypass);
		var whole = source.ToArray(); var split = source.ToArray();
		new AudioBassShelf().Process(whole, -6, 180);
		var filter = new AudioBassShelf(); filter.Process(split.AsSpan(0, 512), -6, 180); filter.Process(split.AsSpan(512), -6, 180);
		CollectionAssert.AreEqual(whole, split);
	}
	[TestMethod] public void BassShelf_LiveChangeRampsAndInvalidSettingsAreRejected()
	{
		var filter = new AudioBassShelf(); var initial = Enumerable.Repeat(.2f, 44100).ToArray();
		filter.Process(initial, 0, 180);
		var changed = Enumerable.Repeat(.2f, 44100).ToArray(); filter.Process(changed, -6, 180);
		Assert.IsTrue(Math.Abs(changed[0] - .2) < .001);
		Assert.AreEqual(.2 * Math.Pow(10, -6d/20), changed[^1], 1e-6);
		Assert.ThrowsException<ArgumentOutOfRangeException>(() => filter.Process(new float[2], float.NaN, 180));
		var resource = new AudioEvent { MasterTrack = new AudioTrack { Name = "Master", Tone = new() { BassFrequencyHz = 0 } } };
		Assert.IsTrue(AudioEventValidator.Validate(resource).Issues.Any(x => x.Path.EndsWith("/Tone")));
	}
}
