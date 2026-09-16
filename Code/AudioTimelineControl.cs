namespace Smod;

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
	NextBar,
	NextPhrase
}

public sealed class TransitionRegion
{
    [Property, Range( 0f, 32f ), Description( "Fade before the selected beat. A late request shortens this fade to the remaining time." )]
    public float FadeOutBeats { get; set; }
    [Property, Range( 0f, 32f ), Description( "Fade after the jump, at the destination tempo." )]
    public float FadeInBeats { get; set; }
	[Property] public Guid Id { get; set; } = Guid.NewGuid();
	[Property] public string Name { get; set; }
	[Property] public TimelineTime Start { get; set; }
	[Property] public TimelineTime End { get; set; }
	[Property] public Guid DestinationMarkerId { get; set; }
	[Property, InlineEditor] public ParameterConditionSet Conditions { get; set; } = new();
	[Property] public TransitionQuantization Quantization { get; set; }
	[Property] public int Priority { get; set; }
}

public enum LoopEndMode
{
	Fixed,
	ContentEnd
}

public sealed class LoopRegion
{
	[Property] public Guid Id { get; set; } = Guid.NewGuid();
	[Property] public string Name { get; set; }
	[Property] public TimelineTime Start { get; set; }
	[Property] public TimelineTime End { get; set; }
	[Property] public LoopEndMode EndMode { get; set; }
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
    private List<TransitionRegion> _regionStorage;
    private List<TransitionRegion> _regions => _regionStorage ??= new(_event.TransitionRegions);
    private TransitionRegion _musicTransition;
    internal IReadOnlyList<TransitionRegion> Regions => _regions;
    internal void SetMusicTransition(TransitionRegion region)
    {
        if(ReferenceEquals(region,_musicTransition)) return;
        if(_musicTransition is not null)_regions.Remove(_musicTransition);
        _musicTransition=region;
        if(region is not null)_regions.Add(region);
    }
	private readonly Dictionary<Guid, long> _destinations = new();
	private Guid _pendingRegion;
	private long _pendingExecution = -1;
	private long _previousSample;
    private long _fadeOutStart;
    private long _fadeInStart = -1;
    private double _fadeInSamples;
    public float TransitionGain { get; private set; } = 1f;

	public AudioTimelineControlEvaluator( AudioEvent audioEvent )
	{
		_event = audioEvent;
        _regionStorage = new(audioEvent.TransitionRegions);
		foreach ( var marker in audioEvent.DestinationMarkers )
			_destinations.Add( marker.Id, marker.Position.ToSamples( audioEvent.TimelineSampleRate ) );
	}

	public Guid PendingRegionId => _pendingRegion;
	public long PendingExecutionSample => _pendingRegion == Guid.Empty ? -1 : _pendingExecution;

	public void Reset( long sample )
	{
		_previousSample = sample;
        _fadeInStart = -1;
        _fadeInSamples = 0;
        TransitionGain = 1f;
        _pendingRegion = Guid.Empty;
		_pendingExecution = -1;
	}

	public AudioTimelineEvaluationResult Advance(
		long currentSample,
		IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters,
		Guid completedContentLoopId = default )
	{
		var start = _previousSample;
		var end = Math.Max( start, currentSample );
		var jumps = 0;
		var limit = Math.Max( 1, _event.MaximumTimelineJumpsPerUpdate );
		var executedLoopRegionIds = new List<Guid>();

		while ( true )
		{
			var decision = FindDecision( start, end, parameters, completedContentLoopId, currentSample );
			if ( decision is null )
				break;
			if ( jumps >= limit )
			{
				_previousSample = end;
				return new AudioTimelineEvaluationResult( end, jumps, true, executedLoopRegionIds );
			}

			var transition = _regions.FirstOrDefault( x => x.Id == decision.Value.SourceId );
            if ( transition is not null )
            {
                _fadeInStart = decision.Value.DestinationSample;
                _fadeInSamples = transition.FadeInBeats * _event.SecondsPerBeatAt(
                    (double)_fadeInStart / _event.TimelineSampleRate ) * _event.TimelineSampleRate;
            }
            else { _fadeInStart = -1; _fadeInSamples = 0; }
            var overshoot = Math.Max( 0, end - decision.Value.ExecutionSample );
            // A shared composition has already looped in the sample renderer.
            // Fold missed cycles once; do not replay old triggers or seek the audio.
            if ( _event.SharedMusicOutput && decision.Value.IsLoop
                && _event.TransitionMarkers.Count == 0 && _regions.Count == 0
                && _event.LoopRegions.Count == 1
                && _event.LoopRegions[0].EndMode == LoopEndMode.Fixed
                && _event.LoopRegions[0].Conditions.Conditions.Count == 0 )
            {
                var length = decision.Value.ExecutionSample - decision.Value.DestinationSample;
                if ( length > 0 ) overshoot %= length;
            }
			start = decision.Value.DestinationSample;
			end = checked(start + overshoot);
			jumps++;
			if ( decision.Value.IsLoop )
				executedLoopRegionIds.Add( decision.Value.SourceId );
			_pendingRegion = Guid.Empty;
			_pendingExecution = -1;
		}

		TransitionGain = 1f;
        if ( _fadeInStart >= 0 && _fadeInSamples > 0 )
            TransitionGain = (float)Math.Clamp( (end - _fadeInStart) / _fadeInSamples, 0d, 1d );
        if ( _pendingRegion != Guid.Empty && _pendingExecution > _fadeOutStart && end >= _fadeOutStart )
            TransitionGain = MathF.Min( TransitionGain, (float)Math.Clamp(
                (double)(_pendingExecution - end) / (_pendingExecution - _fadeOutStart), 0d, 1d ) );
        _previousSample = end;
        return new AudioTimelineEvaluationResult( end, jumps, false, executedLoopRegionIds );
	}

	private TimelineDecision? FindDecision(
		long start,
		long end,
		IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters,
		Guid completedContentLoopId,
		long contentCompletionSample )
	{
		if ( _pendingRegion != Guid.Empty )
		{
			TransitionRegion region = null;
			for ( var i = 0; i < _regions.Count; i++ )
			{
				if ( _regions[i].Id == _pendingRegion )
				{
					region = _regions[i];
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
		for ( var i = 0; i < _regions.Count; i++ )
		{
			var candidate = _regions[i];
            if ( _pendingRegion != Guid.Empty ) continue;
			if ( end < candidate.Start.ToSamples( _event.TimelineSampleRate )
				|| start >= candidate.End.ToSamples( _event.TimelineSampleRate )
				|| !Matches( candidate.Conditions, parameters ) )
				continue;
			if ( selectedRegion is null || candidate.Priority > selectedRegion.Priority )
				selectedRegion = candidate;
		}
		if ( selectedRegion is not null )
		{
			var requestSample = Math.Max( start, selectedRegion.Start.ToSamples( _event.TimelineSampleRate ) );
            var execution = Quantize( requestSample, selectedRegion.Quantization );
			if ( execution > end )
			{
				if ( execution <= selectedRegion.End.ToSamples( _event.TimelineSampleRate ) )
				{
					_pendingRegion = selectedRegion.Id;
					_pendingExecution = execution;
                    var fadeSamples = selectedRegion.FadeOutBeats * _event.SecondsPerBeatAt(
                        (double)requestSample / _event.TimelineSampleRate ) * _event.TimelineSampleRate;
                    _fadeOutStart = Math.Max( requestSample, execution - (long)Math.Round( fadeSamples ) );
				}
			}
			else if ( execution <= selectedRegion.End.ToSamples( _event.TimelineSampleRate ) )
				return DestinationDecision( execution, selectedRegion.DestinationMarkerId, selectedRegion.Id );
		}

		foreach ( var loop in _event.LoopRegions )
		{
			var authoredEnd = loop.End.ToSamples( _event.TimelineSampleRate );
			var loopEnd = loop.EndMode == LoopEndMode.ContentEnd
				&& loop.Id == completedContentLoopId
				&& contentCompletionSample >= loop.Start.ToSamples( _event.TimelineSampleRate )
				&& contentCompletionSample < authoredEnd
					? contentCompletionSample
					: authoredEnd;
			if ( start < loopEnd && end >= loopEnd && Matches( loop.Conditions, parameters ) )
				return new TimelineDecision( loopEnd, loop.Start.ToSamples( _event.TimelineSampleRate ), loop.Id, true );
		}
		return null;
	}

	internal static bool MatchesLoopConditions(
		LoopRegion loop,
		IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters )
	{
		ArgumentNullException.ThrowIfNull( loop );
		return Matches( loop.Conditions, parameters );
	}

	private TimelineDecision DestinationDecision( long execution, Guid destinationId, Guid sourceId )
		=> new( execution, _destinations[destinationId], sourceId, false );

	private long Quantize( long sample, TransitionQuantization quantization )
	{
		if ( quantization == TransitionQuantization.Immediate )
			return sample;
		var grid = _event.BeatGridAt( (double)sample / _event.TimelineSampleRate );
        var samplesPerBeat = _event.SecondsPerBeatAt( (double)sample / _event.TimelineSampleRate ) * _event.TimelineSampleRate;
        var beatsPerBar = grid?.BeatsPerBar ?? _event.TempoMap.BeatsPerBar;
        var quantum = samplesPerBeat * (quantization switch {
            TransitionQuantization.NextBar => beatsPerBar,
            TransitionQuantization.NextPhrase => beatsPerBar * (grid?.BarsPerPhrase ?? 4),
            _ => 1 });
        var origin = grid?.Start.ToSamples( _event.TimelineSampleRate ) ?? 0;
        // Sample rounding must not turn a beat boundary into the previous beat.
        return checked(origin + (long)Math.Round(
            (Math.Floor( (sample - origin + .5d) / quantum ) + 1d) * quantum, MidpointRounding.AwayFromZero ));
	}

	internal static bool Matches( ParameterConditionSet set, IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters )
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
