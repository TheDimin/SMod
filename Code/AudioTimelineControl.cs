namespace Smod.Audio;

public readonly record struct TimelineTime
{
	public TimelineTime( double seconds )
	{
		if ( !double.IsFinite( seconds ) || seconds < 0d )
			throw new ArgumentOutOfRangeException( nameof( seconds ) );
		Seconds = seconds;
	}

	[Property]
	public double Seconds { get; init; }

	public long ToSamples( int sampleRate ) => checked((long)Math.Round( Seconds * sampleRate, MidpointRounding.AwayFromZero ));
	public static TimelineTime FromSamples( long samples, int sampleRate ) => new( (double)samples / sampleRate );
}

public sealed class AudioTempoMap
{
	[Property, Range( 1f, 400f )]
	public float BeatsPerMinute { get; set; }

	[Property, Range( 1, 32 )]
	public int BeatsPerBar { get; set; } = 4;

	public bool IsValid => float.IsFinite( BeatsPerMinute ) && BeatsPerMinute > 0f && BeatsPerBar > 0;
}

public sealed class DestinationMarker
{
	[Property] public Guid Id { get; set; } = Guid.NewGuid();
	[Property] public string Name { get; set; }
	[Property] public TimelineTime Position { get; set; }
}

public enum AudioParameterComparison
{
	Equal,
	NotEqual,
	Less,
	LessOrEqual,
	Greater,
	GreaterOrEqual,
	InsideRange,
	OutsideRange
}

public sealed class TimelineParameterCondition
{
	[Property] public AudioParameter Parameter { get; set; }
	[Property] public AudioParameterComparison Comparison { get; set; }
	[Property] public float Value { get; set; }
	[Property] public float Maximum { get; set; }
	[Property, AudioParameterLabel( nameof( Parameter ) )] public string Label { get; set; }
}

public sealed class ParameterConditionSet
{
	[Property, InlineEditor] public List<TimelineParameterCondition> Conditions { get; set; } = new();
}

public sealed class TransitionMarker
{
	[Property] public Guid Id { get; set; } = Guid.NewGuid();
	[Property] public string Name { get; set; }
	[Property] public TimelineTime Position { get; set; }
	[Property] public Guid DestinationMarkerId { get; set; }
	[Property, InlineEditor] public ParameterConditionSet Conditions { get; set; } = new();
	[Property] public int Priority { get; set; }
}

public enum TransitionQuantization
{
	Immediate,
	NextBeat,
	NextBar
}

public sealed class TransitionRegion
{
	[Property] public Guid Id { get; set; } = Guid.NewGuid();
	[Property] public string Name { get; set; }
	[Property] public TimelineTime Start { get; set; }
	[Property] public TimelineTime End { get; set; }
	[Property] public Guid DestinationMarkerId { get; set; }
	[Property, InlineEditor] public ParameterConditionSet Conditions { get; set; } = new();
	[Property] public TransitionQuantization Quantization { get; set; }
	[Property] public int Priority { get; set; }
}

public sealed class LoopRegion
{
	[Property] public Guid Id { get; set; } = Guid.NewGuid();
	[Property] public string Name { get; set; }
	[Property] public TimelineTime Start { get; set; }
	[Property] public TimelineTime End { get; set; }
	[Property, InlineEditor] public ParameterConditionSet Conditions { get; set; } = new();
}

public readonly record struct AudioTimelineDebugSnapshot(
	long CurrentSample,
	Guid ActiveLoopRegionId,
	Guid PendingTransitionId,
	Guid PendingDestinationId,
	long ScheduledExecutionSample,
	int JumpsLastUpdate,
	bool JumpLimitExceeded );

internal readonly record struct AudioTimelineEvaluationResult(
	long Position,
	int Jumps,
	bool LimitExceeded,
	IReadOnlyList<Guid> ExecutedLoopRegionIds );

internal sealed class AudioTimelineControlEvaluator
{
	private readonly AudioEvent _event;
	private readonly Dictionary<Guid, long> _destinations = new();
	private Guid _pendingRegion;
	private long _pendingExecution = -1;
	private long _previousSample;

	public AudioTimelineControlEvaluator( AudioEvent audioEvent )
	{
		_event = audioEvent;
		foreach ( var marker in audioEvent.DestinationMarkers )
			_destinations.Add( marker.Id, marker.Position.ToSamples( audioEvent.TimelineSampleRate ) );
	}

	public Guid PendingRegionId => _pendingRegion;
	public long PendingExecutionSample => _pendingRegion == Guid.Empty ? -1 : _pendingExecution;

	public void Reset( long sample )
	{
		_previousSample = sample;
		_pendingRegion = Guid.Empty;
		_pendingExecution = -1;
	}

	public AudioTimelineEvaluationResult Advance( long currentSample, IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters )
	{
		var start = _previousSample;
		var end = Math.Max( start, currentSample );
		var jumps = 0;
		var limit = Math.Max( 1, _event.MaximumTimelineJumpsPerUpdate );
		var executedLoopRegionIds = new List<Guid>();

		while ( true )
		{
			var decision = FindDecision( start, end, parameters );
			if ( decision is null )
				break;
			if ( jumps >= limit )
			{
				_previousSample = end;
				return new AudioTimelineEvaluationResult( end, jumps, true, executedLoopRegionIds );
			}

			var overshoot = Math.Max( 0, end - decision.Value.ExecutionSample );
			start = decision.Value.DestinationSample;
			end = checked(start + overshoot);
			jumps++;
			if ( decision.Value.IsLoop )
				executedLoopRegionIds.Add( decision.Value.SourceId );
			_pendingRegion = Guid.Empty;
			_pendingExecution = -1;
		}

		_previousSample = end;
		return new AudioTimelineEvaluationResult( end, jumps, false, executedLoopRegionIds );
	}

	private TimelineDecision? FindDecision( long start, long end, IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters )
	{
		if ( _pendingRegion != Guid.Empty )
		{
			TransitionRegion region = null;
			for ( var i = 0; i < _event.TransitionRegions.Count; i++ )
			{
				if ( _event.TransitionRegions[i].Id == _pendingRegion )
				{
					region = _event.TransitionRegions[i];
					break;
				}
			}
			var scheduled = PendingExecutionSample;
			if ( region is null || !Matches( region.Conditions, parameters ) || scheduled > region.End.ToSamples( _event.TimelineSampleRate ) )
			{
				_pendingRegion = Guid.Empty;
				_pendingExecution = -1;
			}
			else if ( start <= scheduled && end >= scheduled )
				return DestinationDecision( scheduled, region.DestinationMarkerId, region.Id );
		}

		TransitionMarker selectedMarker = null;
		var selectedMarkerSample = long.MaxValue;
		for ( var i = 0; i < _event.TransitionMarkers.Count; i++ )
		{
			var candidate = _event.TransitionMarkers[i];
			var sample = candidate.Position.ToSamples( _event.TimelineSampleRate );
			if ( start >= sample || end < sample || !Matches( candidate.Conditions, parameters ) )
				continue;
			if ( selectedMarker is null || sample < selectedMarkerSample
				|| (sample == selectedMarkerSample && candidate.Priority > selectedMarker.Priority) )
			{
				selectedMarker = candidate;
				selectedMarkerSample = sample;
			}
		}
		if ( selectedMarker is not null )
			return DestinationDecision( selectedMarkerSample, selectedMarker.DestinationMarkerId, selectedMarker.Id );

		TransitionRegion selectedRegion = null;
		for ( var i = 0; i < _event.TransitionRegions.Count; i++ )
		{
			var candidate = _event.TransitionRegions[i];
			if ( end < candidate.Start.ToSamples( _event.TimelineSampleRate )
				|| start >= candidate.End.ToSamples( _event.TimelineSampleRate )
				|| !Matches( candidate.Conditions, parameters ) )
				continue;
			if ( selectedRegion is null || candidate.Priority > selectedRegion.Priority )
				selectedRegion = candidate;
		}
		if ( selectedRegion is not null )
		{
			var execution = Quantize( Math.Max( start, selectedRegion.Start.ToSamples( _event.TimelineSampleRate ) ), selectedRegion.Quantization );
			if ( execution > end )
			{
				if ( execution < selectedRegion.End.ToSamples( _event.TimelineSampleRate ) )
				{
					_pendingRegion = selectedRegion.Id;
					_pendingExecution = execution;
				}
			}
			else if ( execution < selectedRegion.End.ToSamples( _event.TimelineSampleRate ) )
				return DestinationDecision( execution, selectedRegion.DestinationMarkerId, selectedRegion.Id );
		}

		foreach ( var loop in _event.LoopRegions )
		{
			var loopEnd = loop.End.ToSamples( _event.TimelineSampleRate );
			if ( start < loopEnd && end >= loopEnd && Matches( loop.Conditions, parameters ) )
				return new TimelineDecision( loopEnd, loop.Start.ToSamples( _event.TimelineSampleRate ), loop.Id, true );
		}
		return null;
	}

	private TimelineDecision DestinationDecision( long execution, Guid destinationId, Guid sourceId )
		=> new( execution, _destinations[destinationId], sourceId, false );

	private long Quantize( long sample, TransitionQuantization quantization )
	{
		if ( quantization == TransitionQuantization.Immediate )
			return sample;
		var samplesPerBeat = 60d * _event.TimelineSampleRate / _event.TempoMap.BeatsPerMinute;
		var quantum = quantization == TransitionQuantization.NextBar ? samplesPerBeat * _event.TempoMap.BeatsPerBar : samplesPerBeat;
		return checked((long)Math.Round( (Math.Floor( sample / quantum ) + 1d) * quantum, MidpointRounding.AwayFromZero ));
	}

	private static bool Matches( ParameterConditionSet set, IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters )
	{
		if ( set?.Conditions is null || set.Conditions.Count == 0 )
			return true;
		foreach ( var condition in set.Conditions )
		{
			if ( !parameters.TryGetValue( condition.Parameter, out var actual ) )
				return false;
			var match = actual.IsLabel
				? condition.Comparison switch
				{
					AudioParameterComparison.Equal => string.Equals( actual.Label, condition.Label, StringComparison.Ordinal ),
					AudioParameterComparison.NotEqual => !string.Equals( actual.Label, condition.Label, StringComparison.Ordinal ),
					_ => false
				}
				: condition.Comparison switch
				{
					AudioParameterComparison.Equal => actual.Number == condition.Value,
					AudioParameterComparison.NotEqual => actual.Number != condition.Value,
					AudioParameterComparison.Less => actual.Number < condition.Value,
					AudioParameterComparison.LessOrEqual => actual.Number <= condition.Value,
					AudioParameterComparison.Greater => actual.Number > condition.Value,
					AudioParameterComparison.GreaterOrEqual => actual.Number >= condition.Value,
					AudioParameterComparison.InsideRange => actual.Number >= condition.Value && actual.Number <= condition.Maximum,
					AudioParameterComparison.OutsideRange => actual.Number < condition.Value || actual.Number > condition.Maximum,
					_ => false
				};
			if ( !match )
				return false;
		}
		return true;
	}

	private readonly record struct TimelineDecision( long ExecutionSample, long DestinationSample, Guid SourceId, bool IsLoop );
}
