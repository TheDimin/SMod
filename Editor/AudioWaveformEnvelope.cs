using System;

namespace Editor;

internal readonly record struct AudioWaveformPeak( short Minimum, short Maximum );

/// <summary>
/// Stores multiresolution min/max peaks in a segment tree so a rendered waveform
/// column can query peaks without rescanning samples. Boundary bins are conservative
/// within 64 source frames (1.45 ms at 44.1 kHz); source audio is unchanged.
/// </summary>
internal sealed class AudioWaveformEnvelope
{
	private readonly AudioWaveformPeak[] _peaks;
	private readonly int _leafOffset;
	internal const int FramesPerBin = 64;
	internal long StorageBytes => _peaks.LongLength * 4;

	public int FrameCount { get; }

	private AudioWaveformEnvelope( AudioWaveformPeak[] peaks, int leafOffset, int frameCount )
	{
		_peaks = peaks;
		_leafOffset = leafOffset;
		FrameCount = frameCount;
	}

	public static AudioWaveformEnvelope Create( short[] samples, int channels )
	{
		samples ??= Array.Empty<short>();
		channels = Math.Max( 1, channels );
		var frameCount = samples.Length / channels;
		var leafOffset = 1;
		while ( leafOffset < (frameCount + FramesPerBin - 1) / FramesPerBin )
			leafOffset <<= 1;

		var peaks = new AudioWaveformPeak[leafOffset * 2];
		for ( var node = 0; node < peaks.Length; node++ )
			peaks[node] = new AudioWaveformPeak( short.MaxValue, short.MinValue );
		for ( var frame = 0; frame < frameCount; frame++ )
		{
			var sampleOffset = frame * channels;
			var minimum = short.MaxValue;
			var maximum = short.MinValue;
			for ( var channel = 0; channel < channels; channel++ )
			{
				var sample = samples[sampleOffset + channel];
				minimum = Math.Min( minimum, sample );
				maximum = Math.Max( maximum, sample );
			}
			var leaf = leafOffset + frame / FramesPerBin;
			peaks[leaf] = Combine( peaks[leaf], new AudioWaveformPeak( minimum, maximum ) );
		}

		for ( var node = leafOffset - 1; node > 0; node-- )
			peaks[node] = Combine( peaks[node * 2], peaks[node * 2 + 1] );

		return new AudioWaveformEnvelope( peaks, leafOffset, frameCount );
	}

	public AudioWaveformPeak GetPeak( int firstFrame, int finalFrame )
	{
		firstFrame = Math.Clamp( firstFrame, 0, FrameCount );
		finalFrame = Math.Clamp( finalFrame, firstFrame, FrameCount );
		if ( firstFrame == finalFrame )
			return default;

		var minimum = short.MaxValue;
		var maximum = short.MinValue;
		var left = _leafOffset + firstFrame / FramesPerBin;
		var right = _leafOffset + (finalFrame + FramesPerBin - 1) / FramesPerBin;
		while ( left < right )
		{
			if ( (left & 1) != 0 )
			{
				Include( _peaks[left], ref minimum, ref maximum );
				left++;
			}
			if ( (right & 1) != 0 )
			{
				right--;
				Include( _peaks[right], ref minimum, ref maximum );
			}
			left >>= 1;
			right >>= 1;
		}

		return new AudioWaveformPeak( minimum, maximum );
	}

	private static AudioWaveformPeak Combine( AudioWaveformPeak left, AudioWaveformPeak right )
	{
		if ( left.Minimum > left.Maximum )
			return right;
		if ( right.Minimum > right.Maximum )
			return left;
		return new AudioWaveformPeak(
			Math.Min( left.Minimum, right.Minimum ),
			Math.Max( left.Maximum, right.Maximum ) );
	}

	private static void Include( AudioWaveformPeak peak, ref short minimum, ref short maximum )
	{
		minimum = Math.Min( minimum, peak.Minimum );
		maximum = Math.Max( maximum, peak.Maximum );
	}
}
