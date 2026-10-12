using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using Smod;

namespace Smod.Editor;

internal readonly record struct AudioTempoEstimate(
	float BeatsPerMinute,
	float Confidence,
	bool HalfDoubleTimeAmbiguity,
	string Failure )
{
	public bool IsUsable => string.IsNullOrWhiteSpace( Failure ) && Confidence >= AudioTempoAnalyzer.MinimumConfidence;
}

internal readonly record struct AudioTempoClipResult( string Source, AudioTempoEstimate Estimate );

internal sealed record AudioTempoAnalysisSummary(
	IReadOnlyList<AudioTempoClipResult> Clips,
	float? SuggestedBeatsPerMinute,
	string Message );

internal readonly record struct AudioTempoClipSource(
	SoundFile Sound,
	string Source,
	float Offset,
	float Length );

internal static class AudioTempoAnalyzer
{
	internal const float MinimumBeatsPerMinute = 40f;
	internal const float MaximumBeatsPerMinute = 240f;
	internal const float MinimumConfidence = 0.22f;
	private const int EnvelopeRate = 200;

	public static bool IsPlausibleTempo( float beatsPerMinute )
	{
		return float.IsFinite( beatsPerMinute )
			&& beatsPerMinute >= MinimumBeatsPerMinute
			&& beatsPerMinute <= MaximumBeatsPerMinute;
	}

	public static AudioTempoEstimate Analyze(
		IReadOnlyList<short> samples,
		int channels,
		int sampleRate,
		CancellationToken cancellationToken = default )
	{
		if ( samples is null )
			throw new ArgumentNullException( nameof( samples ) );
		if ( channels <= 0 )
			throw new ArgumentOutOfRangeException( nameof( channels ) );
		if ( sampleRate <= 0 )
			throw new ArgumentOutOfRangeException( nameof( sampleRate ) );

		var frameCount = samples.Count / channels;
		if ( frameCount < sampleRate * 4 )
			return Failed( "At least four seconds of decoded audio are required." );

		var framesPerBin = Math.Max( 1, sampleRate / EnvelopeRate );
		var binCount = frameCount / framesPerBin;
		var energy = new float[binCount];
		for ( var bin = 0; bin < binCount; bin++ )
		{
			if ( (bin & 255) == 0 )
				cancellationToken.ThrowIfCancellationRequested();
			double sum = 0d;
			var firstFrame = bin * framesPerBin;
			for ( var frame = firstFrame; frame < firstFrame + framesPerBin; frame++ )
			{
				double channelSum = 0d;
				var sampleIndex = frame * channels;
				for ( var channel = 0; channel < channels; channel++ )
					channelSum += Math.Abs( (int)samples[sampleIndex + channel] );
				sum += channelSum / channels;
			}
			energy[bin] = (float)(sum / framesPerBin);
		}

		var onset = new float[binCount];
		double onsetEnergy = 0d;
		for ( var index = 1; index < binCount; index++ )
		{
			onset[index] = MathF.Max( 0f, energy[index] - energy[index - 1] );
			onsetEnergy += onset[index] * onset[index];
		}
		if ( onsetEnergy <= 1e-5d )
			return Failed( "No rhythmic onsets were found." );
		var onsetMean = onset.Average();
		for ( var index = 0; index < onset.Length; index++ )
			onset[index] -= onsetMean;

		var minimumLag = (int)MathF.Floor( EnvelopeRate * 60f / MaximumBeatsPerMinute );
		var maximumLag = (int)MathF.Ceiling( EnvelopeRate * 60f / MinimumBeatsPerMinute );
		var correlations = new float[maximumLag + 1];
		for ( var lag = minimumLag; lag <= maximumLag; lag++ )
		{
			cancellationToken.ThrowIfCancellationRequested();
			double product = 0d;
			double leftEnergy = 0d;
			double rightEnergy = 0d;
			for ( var index = lag; index < onset.Length; index++ )
			{
				var left = onset[index];
				var right = onset[index - lag];
				product += left * right;
				leftEnergy += left * left;
				rightEnergy += right * right;
			}
			var denominator = Math.Sqrt( leftEnergy * rightEnergy );
			var correlation = denominator > 1e-9d ? (float)(product / denominator) : 0f;
			correlations[lag] = correlation;
		}

		var strongestCorrelation = correlations.Max();
		var bestLag = minimumLag;
		var bestCorrelation = 0f;
		for ( var lag = minimumLag; lag <= maximumLag; lag++ )
		{
			var localCorrelation = 0f;
			var localLag = lag;
			for ( var nearby = Math.Max( minimumLag, lag - 2 ); nearby <= Math.Min( maximumLag, lag + 2 ); nearby++ )
			{
				if ( correlations[nearby] > localCorrelation )
				{
					localCorrelation = correlations[nearby];
					localLag = nearby;
				}
			}
			if ( localCorrelation < strongestCorrelation * 0.9f )
				continue;
			bestCorrelation = localCorrelation;
			bestLag = localLag;
			break;
		}

		if ( bestCorrelation < MinimumConfidence )
			return new AudioTempoEstimate( EnvelopeRate * 60f / bestLag, bestCorrelation, false, "Rhythmic confidence is too low." );

		var halfLag = bestLag * 2;
		var doubleLag = bestLag / 2;
		var alternateCorrelation = 0f;
		if ( halfLag <= maximumLag )
			alternateCorrelation = MathF.Max( alternateCorrelation, correlations[halfLag] );
		if ( doubleLag >= minimumLag )
			alternateCorrelation = MathF.Max( alternateCorrelation, correlations[doubleLag] );
		var ambiguous = alternateCorrelation >= bestCorrelation * 0.9f;

		return new AudioTempoEstimate( EnvelopeRate * 60f / bestLag, bestCorrelation, ambiguous, null );
	}

	public static AudioTempoAnalysisSummary Summarize( IReadOnlyList<AudioTempoClipResult> clips )
	{
		if ( clips is null || clips.Count == 0 )
			return new AudioTempoAnalysisSummary( clips ?? Array.Empty<AudioTempoClipResult>(), null, "No overlapping audio clips were found." );

		var usable = clips.Where( result => result.Estimate.IsUsable ).ToArray();
		if ( usable.Length != clips.Count )
			return new AudioTempoAnalysisSummary( clips, null, "Some clips did not produce a confident tempo. Author the tempo manually or analyze different material." );

		var ordered = usable.Select( result => result.Estimate.BeatsPerMinute ).OrderBy( value => value ).ToArray();
		var median = ordered.Length % 2 == 0
			? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) * 0.5f
			: ordered[ordered.Length / 2];
		var tolerance = MathF.Max( 2f, median * 0.02f );
		if ( ordered.Any( value => MathF.Abs( value - median ) > tolerance ) )
			return new AudioTempoAnalysisSummary( clips, null, "Detected clip tempos conflict. Set the event tempo manually." );

		return new AudioTempoAnalysisSummary( clips, median, $"Suggested tempo: {median:0.##} BPM." );
	}

	public static IReadOnlyList<AudioTempoClipSource> CollectSources( AudioEvent audioEvent, TransitionRegion region )
	{
		ArgumentNullException.ThrowIfNull( audioEvent );
		ArgumentNullException.ThrowIfNull( region );

		var sources = new List<AudioTempoClipSource>();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var tracks = new[] { audioEvent.MasterTrack }.Concat( audioEvent.Tracks ?? Enumerable.Empty<AudioTrack>() );
		foreach ( var instrument in tracks.Where( track => track is not null ).SelectMany( track => track.Instruments ?? Enumerable.Empty<AudioInstrument>() ) )
		{
			var sourceDefinition = AudioSourceAuthoring.Resolve( audioEvent, instrument );
			if ( sourceDefinition?.Tracks is null || instrument.StartTime >= region.End.Seconds )
				continue;

			foreach ( var variation in sourceDefinition.Tracks )
			{
				var sound = variation?.Sound;
				if ( !sound.IsValid() )
					continue;
				var offset = MathF.Max( 0f, variation.Offset + instrument.TrimStart );
				var available = sound.Duration > 0f ? MathF.Max( 0f, sound.Duration - offset ) : 0f;
				var variationLength = variation.Length > 0f
					? (available > 0f ? MathF.Min( variation.Length, available ) : variation.Length)
					: available;
				var trimLength = instrument.TrimEnd > instrument.TrimStart
					? instrument.TrimEnd - instrument.TrimStart
					: variationLength;
				var length = variationLength > 0f && trimLength > 0f ? MathF.Min( variationLength, trimLength ) : MathF.Max( variationLength, trimLength );
				if ( length > 0f && instrument.StartTime + length <= region.Start.Seconds )
					continue;

				var key = $"{sound.ResourcePath}|{sound.Channels}|{sound.Duration:R}|{offset:R}|{length:R}";
				if ( seen.Add( key ) )
					sources.Add( new AudioTempoClipSource( sound, sound.ResourcePath, offset, length ) );
			}
		}
		return sources;
	}

	private static AudioTempoEstimate Failed( string failure )
	{
		return new AudioTempoEstimate( 0f, 0f, false, failure );
	}
}

internal static class AudioTempoAnalysisService
{
	private static readonly Dictionary<string, AudioTempoEstimate> Cache = new( StringComparer.OrdinalIgnoreCase );

	public static async Task<AudioTempoAnalysisSummary> AnalyzeAsync(
		AudioEvent audioEvent,
		TransitionRegion region,
		CancellationToken cancellationToken )
	{
		var sources = AudioTempoAnalyzer.CollectSources( audioEvent, region );
		var results = new List<AudioTempoClipResult>( sources.Count );
		foreach ( var source in sources )
		{
			cancellationToken.ThrowIfCancellationRequested();
			if ( !await source.Sound.LoadAsync() )
			{
				results.Add( new AudioTempoClipResult( source.Source, new AudioTempoEstimate( 0f, 0f, false, "The sound could not be decoded." ) ) );
				continue;
			}

			var samples = await source.Sound.GetSamplesInterleavedAsync() ?? Array.Empty<short>();
			cancellationToken.ThrowIfCancellationRequested();
			var channels = Math.Max( 1, source.Sound.Channels );
			var frameCount = samples.Length / channels;
			var sampleRate = source.Sound.Duration > 0f
				? Math.Max( 1, (int)MathF.Round( frameCount / source.Sound.Duration ) )
				: 0;
			var firstFrame = Math.Clamp( (int)MathF.Round( source.Offset * sampleRate ), 0, frameCount );
			var effectiveLength = source.Length > 0f ? source.Length : MathF.Max( 0f, source.Sound.Duration - source.Offset );
			var analyzedFrames = Math.Clamp( (int)MathF.Round( effectiveLength * sampleRate ), 0, frameCount - firstFrame );
			var key = $"{source.Sound.ResourcePath}|{sampleRate}|{source.Sound.Duration:R}|{source.Offset:R}|{effectiveLength:R}";
			if ( !Cache.TryGetValue( key, out var estimate ) )
			{
				var sliced = new short[analyzedFrames * channels];
				Array.Copy( samples, firstFrame * channels, sliced, 0, sliced.Length );
				estimate = await GameTask.RunInThreadAsync( () => AudioTempoAnalyzer.Analyze( sliced, channels, sampleRate, cancellationToken ) );
				Cache[key] = estimate;
			}
			results.Add( new AudioTempoClipResult( source.Source, estimate ) );
		}
		return AudioTempoAnalyzer.Summarize( results );
	}
}
