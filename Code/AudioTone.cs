namespace Smod;

/// <summary>Non-destructive low-frequency balance, owned by the event track.</summary>
public sealed class AudioTone
{
	[Property, Range( -18f, 6f ), Title( "Bass Gain (dB)" )]
	public float BassGainDb { get; set; }
	[Property, Range( 40f, 500f ), Title( "Bass Frequency (Hz)" )]
	public float BassFrequencyHz { get; set; } = 180f;
	internal bool IsValid => float.IsFinite( BassGainDb ) && BassGainDb >= -18 && BassGainDb <= 6
		&& float.IsFinite( BassFrequencyHz ) && BassFrequencyHz >= 40 && BassFrequencyHz <= 500;
}

// First-order low shelf: unity at Nyquist, requested gain at DC. No source edits.
// The state belongs to one channel of one listener; changes settle in about 20 ms.
internal sealed class AudioBassShelf
{
	double low, previous, gain = 1, coefficient;
	bool initialized;
	internal void Process( Span<float> samples, float db, float hz, int sampleRate = 44100 )
	{
		if ( sampleRate < 8000 || !float.IsFinite(db) || db < -18 || db > 6
			|| !float.IsFinite(hz) || hz < 40 || hz > 500 ) throw new ArgumentOutOfRangeException();
		var targetGain = Math.Pow(10, db / 20d);
		var k = Math.Tan(Math.PI * hz / sampleRate);
		var targetCoefficient = k / (1 + k);
		var smoothing = 1 - Math.Exp(-1d / (.02 * sampleRate));
		if (!initialized) { gain = targetGain; coefficient = targetCoefficient; initialized = true; }
		for (var i = 0; i < samples.Length; i++)
		{
			gain += (targetGain - gain) * smoothing;
			coefficient += (targetCoefficient - coefficient) * smoothing;
			var x = samples[i];
			low = coefficient * (x + previous) + (1 - 2 * coefficient) * low;
			previous = x;
			samples[i] = (float)(x + (gain - 1) * low);
		}
	}
}

internal sealed class AudioToneProcessor : Sandbox.Audio.AudioProcessor<AudioToneProcessor.State>
{
	internal sealed class State : ListenerState
	{
		internal readonly AudioBassShelf[] Master = Enumerable.Range(0, 8).Select(_ => new AudioBassShelf()).ToArray();
		internal readonly AudioBassShelf[] Track = Enumerable.Range(0, 8).Select(_ => new AudioBassShelf()).ToArray();
	}
	internal sealed record Settings(float MasterDb, float MasterHz, float TrackDb, float TrackHz);
	Settings settings = new(0, 180, 0, 180);
	internal void Configure(AudioTone master, AudioTone track)
	{
		var md = master?.BassGainDb ?? 0; var mh = master?.BassFrequencyHz ?? 180;
		var td = track?.BassGainDb ?? 0; var th = track?.BassFrequencyHz ?? 180;
		if (master is { IsValid: false } || track is { IsValid: false }) return;
		var old = settings;
		if (old.MasterDb != md || old.MasterHz != mh || old.TrackDb != td || old.TrackHz != th)
			settings = new(md, mh, td, th);
	}
	protected override void ProcessSingleChannel(Sandbox.Audio.AudioChannel channel, Span<float> input)
	{
		var value = settings; var i = channel.Get();
		CurrentState.Master[i].Process(input, value.MasterDb, value.MasterHz);
		CurrentState.Track[i].Process(input, value.TrackDb, value.TrackHz);
	}
}

internal interface IAudioToneVoice
{
	void SetTone(AudioTone master, AudioTone track);
}
