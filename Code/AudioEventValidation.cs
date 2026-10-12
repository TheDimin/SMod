namespace Smod;

public sealed record AudioEventValidationIssue( string Path, string Message );

public sealed class AudioEventValidationResult
{
	private readonly List<AudioEventValidationIssue> _issues = new();

	public IReadOnlyList<AudioEventValidationIssue> Issues => _issues;
	public bool IsValid => _issues.Count == 0;

	internal void Error( string path, string message )
	{
		_issues.Add( new AudioEventValidationIssue( path, message ) );
	}

	public void AssertValid()
	{
		if ( IsValid )
			return;

		throw new InvalidOperationException(
			$"Audio event validation failed:\n{string.Join( "\n", _issues.Select( issue => $"{issue.Path}: {issue.Message}" ) )}" );
	}
}

public static partial class AudioEventValidator
{
	public static AudioEventValidationResult Validate( AudioEvent audioEvent ) => ValidateGraph( audioEvent, new HashSet<AudioEvent>(), 0 );

	private static AudioEventValidationResult ValidateGraph( AudioEvent audioEvent, HashSet<AudioEvent> ancestors, int depth )
	{
		if ( audioEvent is not null && (depth >= 16 || !ancestors.Add( audioEvent )) )
		{
			var invalid = new AudioEventValidationResult();
			invalid.Error( "SubEvents", "Child event cycle or nesting deeper than 16 events." );
			return invalid;
		}
		var result = new AudioEventValidationResult();
		if ( audioEvent is null )
		{
			result.Error( "Event", "Audio event is required." );
			return result;
		}

		var eventPath = string.IsNullOrWhiteSpace( audioEvent.ResourcePath ) ? "AudioEvent" : audioEvent.ResourcePath;
		if ( audioEvent.MasterTrack is null )
			result.Error( $"{eventPath}/MasterTrack", "Every audio event requires one master track." );
		if ( audioEvent.Tracks is null )
			result.Error( $"{eventPath}/Tracks", "Track collection cannot be null." );
		if ( audioEvent.Parameters is null )
			result.Error( $"{eventPath}/Parameters", "Parameter collection cannot be null." );
		if ( !float.IsFinite( audioEvent.FadeOutDuration ) || audioEvent.FadeOutDuration < 0f )
			result.Error( $"{eventPath}/FadeOutDuration", "Fade-out duration must be finite and non-negative." );
		if ( !float.IsFinite( audioEvent.Distance ) || audioEvent.Distance < 0f )
			result.Error( $"{eventPath}/Distance", "Audio event distance must be finite and non-negative." );
		if ( audioEvent.TimelineSampleRate < 8000 || audioEvent.TimelineSampleRate > 192000 )
			result.Error( $"{eventPath}/TimelineSampleRate", "Timeline sample rate must be between 8000 and 192000 Hz." );
		if ( !float.IsFinite( audioEvent.TimelineDuration ) || audioEvent.TimelineDuration < 0f )
			result.Error( $"{eventPath}/TimelineDuration", "Timeline duration must be finite and non-negative." );
		if ( audioEvent.MaximumTimelineJumpsPerUpdate < 1 )
			result.Error( $"{eventPath}/MaximumTimelineJumpsPerUpdate", "Maximum timeline jumps must be at least one." );

		var parameters = ValidateParameters( audioEvent, eventPath, result );
        try { AudioSourceRuntime.Prepare(audioEvent,parameters); }
        catch (Exception error) { result.Error(eventPath+"/Sources",error.Message); }
		var trackNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var instrumentCount = 0;
		if ( audioEvent.MasterTrack is not null )
			instrumentCount += ValidateTrack( audioEvent, audioEvent.MasterTrack, $"{eventPath}/MasterTrack", trackNames, parameters, result );
		if ( audioEvent.Tracks is not null )
		{
			for ( var i = 0; i < audioEvent.Tracks.Count; i++ )
			{
				var track = audioEvent.Tracks[i];
				if ( track is null )
				{
					result.Error( $"{eventPath}/Tracks[{i}]", "Track cannot be null." );
					continue;
				}
				instrumentCount += ValidateTrack( audioEvent, track, $"{eventPath}/Tracks[{i}]", trackNames, parameters, result );
			}
		}

		if ( instrumentCount == 0 && !audioEvent.SustainUntilStopped )
			result.Error( eventPath, "Audio event requires at least one instrument." );
		ValidateTimelineControl( audioEvent, eventPath, parameters, result );
        ValidateMusic(audioEvent,eventPath,parameters,result);
        if(audioEvent.SharedMusicOutput && result.IsValid)
        {
            try
            {
                var grid=AudioSharedMusicDefinition.RequireGrid(audioEvent);
                var defaults=audioEvent.Parameters.ToDictionary(p=>p,p=>p.Type==AudioParameterType.Labeled
                    ? new AudioParameterValue(0,p.DefaultLabel,true) : new AudioParameterValue(p.DefaultValue,null,false));
                AudioSharedMusicDefinition.RequireBacking(audioEvent,grid,defaults);
            }
            catch(InvalidOperationException error){result.Error($"{eventPath}/SharedMusicOutput",error.Message);}
        }
        ValidateOverlays(audioEvent,eventPath,parameters,result);
        ValidateLayerControls(audioEvent,eventPath,parameters,result);
        ValidateInstrumentTriggers(audioEvent,eventPath,parameters,result);
		foreach ( var instrument in (audioEvent.Tracks ?? new()).Prepend( audioEvent.MasterTrack ).Where( track => track is not null )
			.SelectMany( track => track.Instruments ?? new() ).Where( instrument => instrument is not null && instrument.IsSubEvent ) )
		foreach ( var choice in instrument.SubEvents.Where( choice => choice?.Event is not null ) )
        foreach(var childEvent in new[]{choice.Event,choice.ExitEvent}.Where(e=>e is not null))
		{
			foreach ( var issue in ValidateGraph( childEvent, ancestors, depth + 1 ).Issues )
				result.Error( $"{eventPath}/SubEvents/{issue.Path}", issue.Message );
			foreach ( var childParameter in childEvent.Parameters?.Where( parameter => parameter is not null ) ?? Enumerable.Empty<AudioParameter>() )
			{
				if ( !parameters.TryGetValue( childParameter.Name ?? "", out var parentParameter ) )
                {
                    result.Error( $"{eventPath}/SubEvents/{childParameter.Name}", "Child input must be declared by the direct parent event." );
                    continue;
                }
				if ( childParameter.Type != parentParameter.Type
					|| (childParameter.Type != AudioParameterType.Labeled && (childParameter.Minimum > parentParameter.Minimum || childParameter.Maximum < parentParameter.Maximum))
					|| (childParameter.Type == AudioParameterType.Labeled && (parentParameter.Labels ?? new()).Except( childParameter.Labels ?? new() ).Any()) )
					result.Error( $"{eventPath}/SubEvents/{childParameter.Name}", "Inherited child parameter must accept the parent type and its full value range." );
			}
		}
		ancestors.Remove( audioEvent );
		return result;
	}

	private static void ValidateTimelineControl(
		AudioEvent audioEvent,
		string eventPath,
		IReadOnlyDictionary<string, AudioParameter> parameters,
		AudioEventValidationResult result )
	{
		if ( audioEvent.DestinationMarkers is null || audioEvent.TransitionMarkers is null
			|| audioEvent.TransitionRegions is null || audioEvent.LoopRegions is null )
		{
			result.Error( $"{eventPath}/TimelineControl", "Timeline-control collections cannot be null." );
			return;
		}

		if ( audioEvent.BeatGrids is null )
            result.Error( $"{eventPath}/BeatGrids", "Beat grid collection cannot be null." );
        else
        {
            var gridNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
            for ( var i = 0; i < audioEvent.BeatGrids.Count; i++ )
            {
                var grid = audioEvent.BeatGrids[i];
                var path = $"{eventPath}/BeatGrids[{i}]";
                if ( grid is null || !grid.IsValid ) { result.Error( path, "Beat grid requires a finite positive tempo, meter, phrase length, and ordered range." ); continue; }
                if ( string.IsNullOrWhiteSpace( grid.Name ) || !gridNames.Add( grid.Name ) )
                    result.Error( path, "Beat grid names must be non-empty and unique." );
                if ( audioEvent.TimelineDuration > 0 && grid.End.Seconds > audioEvent.TimelineDuration + .0001 )
                    result.Error( path, "Beat grid is outside the event timeline." );
                if ( audioEvent.BeatGrids.Take( i ).Any( other => other is not null
                    && other.Start.Seconds < grid.End.Seconds && grid.Start.Seconds < other.End.Seconds ) )
                    result.Error( path, "Beat grids must not overlap." );
            }
        }

        var durationSamples = audioEvent.TimelineDuration > 0f
			? checked((long)Math.Round( audioEvent.TimelineDuration * audioEvent.TimelineSampleRate ))
			: long.MaxValue;
		var ids = new HashSet<Guid>();
		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var destinations = new HashSet<Guid>();
		for ( var i = 0; i < audioEvent.DestinationMarkers.Count; i++ )
		{
			var marker = audioEvent.DestinationMarkers[i];
			var path = $"{eventPath}/DestinationMarkers[{i}]";
			if ( marker is null ) { result.Error( path, "Destination marker cannot be null." ); continue; }
			ValidateControlId( marker.Id, path, ids, result );
			if ( string.IsNullOrWhiteSpace( marker.Name ) || !names.Add( marker.Name ) )
				result.Error( $"{path}/Name", $"Destination marker name '{marker.Name}' must be non-empty and unique." );
			if ( marker.Position.ToSamples( audioEvent.TimelineSampleRate ) > durationSamples )
				result.Error( $"{path}/Position", "Destination marker is outside the event timeline." );
			destinations.Add( marker.Id );
		}

		for ( var i = 0; i < audioEvent.TransitionMarkers.Count; i++ )
		{
			var marker = audioEvent.TransitionMarkers[i];
			var path = $"{eventPath}/TransitionMarkers[{i}]";
			if ( marker is null ) { result.Error( path, "Transition marker cannot be null." ); continue; }
			ValidateControlId( marker.Id, path, ids, result );
			ValidateDestination( marker.DestinationMarkerId, $"{path}/DestinationMarkerId", destinations, result );
			ValidateTimelineConditions( marker.Conditions, $"{path}/Conditions", parameters, result );
			if ( marker.Position.ToSamples( audioEvent.TimelineSampleRate ) > durationSamples )
				result.Error( $"{path}/Position", "Transition marker is outside the event timeline." );
		}

		for ( var i = 0; i < audioEvent.TransitionRegions.Count; i++ )
		{
			var region = audioEvent.TransitionRegions[i];
			var path = $"{eventPath}/TransitionRegions[{i}]";
			if ( region is null ) { result.Error( path, "Transition region cannot be null." ); continue; }
			ValidateControlId( region.Id, path, ids, result );
			ValidateRange( region.Start, region.End, path, durationSamples, audioEvent.TimelineSampleRate, result );
			ValidateDestination( region.DestinationMarkerId, $"{path}/DestinationMarkerId", destinations, result );
			ValidateTimelineConditions( region.Conditions, $"{path}/Conditions", parameters, result );
			var grid = audioEvent.BeatGridAt( region.Start.Seconds );
            if ( !Enum.IsDefined( region.Quantization ) )
                result.Error( $"{path}/Quantization", "Unknown transition quantization." );
            if ( region.Quantization != TransitionQuantization.Immediate
                && (grid is null ? !(audioEvent.TempoMap?.IsValid ?? false) : grid.End.Seconds + .0001 < region.End.Seconds) )
                result.Error( $"{path}/Quantization", "Quantization requires one beat grid covering the region, or a valid global tempo map." );
            if ( !float.IsFinite( region.FadeOutBeats ) || region.FadeOutBeats < 0
                || (region.FadeOutBeats > 0 && region.Quantization == TransitionQuantization.Immediate) )
                result.Error( $"{path}/FadeOutBeats", "Fade-out beats must be finite, non-negative, and use a quantized transition." );
            var target = audioEvent.DestinationMarkers.FirstOrDefault( x => x?.Id == region.DestinationMarkerId );
            if ( !float.IsFinite( region.FadeInBeats ) || region.FadeInBeats < 0
                || (region.FadeInBeats > 0 && (target is null || audioEvent.SecondsPerBeatAt( target.Position.Seconds ) <= 0)) )
                result.Error( $"{path}/FadeInBeats", "Fade-in beats require a finite non-negative value and a destination tempo." );
		}

		for ( var i = 0; i < audioEvent.LoopRegions.Count; i++ )
		{
			var loop = audioEvent.LoopRegions[i];
			var path = $"{eventPath}/LoopRegions[{i}]";
			if ( loop is null ) { result.Error( path, "Loop region cannot be null." ); continue; }
			ValidateControlId( loop.Id, path, ids, result );
			ValidateRange( loop.Start, loop.End, path, durationSamples, audioEvent.TimelineSampleRate, result );
			ValidateTimelineConditions( loop.Conditions, $"{path}/Conditions", parameters, result );
			if ( !Enum.IsDefined( loop.EndMode ) )
				result.Error( $"{path}/EndMode", $"Unknown loop end mode '{loop.EndMode}'." );
			for ( var previous = 0; previous < i; previous++ )
			{
				var other = audioEvent.LoopRegions[previous];
				if ( other is not null && loop.Start.Seconds < other.End.Seconds && other.Start.Seconds < loop.End.Seconds )
					result.Error( path, $"Loop region overlaps LoopRegions[{previous}]. Overlapping and nested loops are not supported." );
			}
		}
	}

	private static void ValidateControlId( Guid id, string path, HashSet<Guid> ids, AudioEventValidationResult result )
	{
		if ( id == Guid.Empty )
			result.Error( $"{path}/Id", "Timeline-control ID cannot be empty." );
		else if ( !ids.Add( id ) )
			result.Error( $"{path}/Id", $"Duplicate timeline-control ID '{id}'." );
	}

	private static void ValidateDestination( Guid id, string path, HashSet<Guid> destinations, AudioEventValidationResult result )
	{
		if ( !destinations.Contains( id ) )
			result.Error( path, $"Destination marker '{id}' does not exist in this event. Go To markers cannot cross sub-graph boundaries." );
	}

	private static void ValidateRange( TimelineTime start, TimelineTime end, string path, long duration, int sampleRate, AudioEventValidationResult result )
	{
		var startSample = start.ToSamples( sampleRate );
		var endSample = end.ToSamples( sampleRate );
		if ( endSample <= startSample )
			result.Error( path, "Timeline region end must be after its start." );
		if ( endSample > duration )
			result.Error( path, "Timeline region is outside the event timeline." );
	}

	private static void ValidateTimelineConditions(
		ParameterConditionSet set,
		string path,
		IReadOnlyDictionary<string, AudioParameter> parameters,
		AudioEventValidationResult result )
	{
		if ( set?.Conditions is null ) { result.Error( path, "Condition set cannot be null." ); return; }
		for ( var i = 0; i < set.Conditions.Count; i++ )
		{
			var condition = set.Conditions[i];
			var conditionPath = $"{path}[{i}]";
			if ( condition?.Parameter is null || !parameters.Values.Any( parameter => ReferenceEquals( parameter, condition.Parameter ) ) )
			{
				result.Error( $"{conditionPath}/Parameter", "Condition must reference a parameter used by this AudioEvent." );
				continue;
			}
			if ( condition.Parameter.Type == AudioParameterType.Labeled )
			{
				if ( condition.Comparison is not (AudioParameterComparison.Equal or AudioParameterComparison.NotEqual) )
					result.Error( $"{conditionPath}/Comparison", "Labeled parameters support only equality comparisons." );
				if (!condition.Parameter.Labels.Contains( condition.Label, StringComparer.Ordinal ))
					result.Error( $"{conditionPath}/Label", $"Label '{condition.Label}' is not declared by parameter '{condition.Parameter.Name}'." );
			}
			else if ( !float.IsFinite( condition.Value ) || !float.IsFinite( condition.Maximum )
				|| (condition.Comparison is AudioParameterComparison.InsideRange or AudioParameterComparison.OutsideRange
					&& condition.Maximum < condition.Value) )
				result.Error( conditionPath, "Numeric condition values must be finite and ranges must be ordered." );
		}
	}

	private static Dictionary<string, AudioParameter> ValidateParameters(
		AudioEvent audioEvent,
		string eventPath,
		AudioEventValidationResult result )
	{
		var parameters = new Dictionary<string, AudioParameter>( StringComparer.OrdinalIgnoreCase );
		if ( audioEvent.Parameters is null )
			return parameters;

		for ( var i = 0; i < audioEvent.Parameters.Count; i++ )
		{
			var parameter = audioEvent.Parameters[i];
			var path = $"{eventPath}/Parameters[{i}]";
			if ( parameter is null )
			{
				result.Error( path, "Parameter cannot be null." );
				continue;
			}

			if ( string.IsNullOrWhiteSpace( parameter.Name ) || parameter.Name != parameter.Name.Trim() )
			{
				result.Error( $"{path}/Name", "Parameter name must be non-empty and trimmed." );
				continue;
			}
			if ( !parameters.TryAdd( parameter.Name, parameter ) )
				result.Error( $"{path}/Name", $"Duplicate parameter '{parameter.Name}'." );

			if ( parameter.Type == AudioParameterType.Labeled )
			{
				ValidateLabels( parameter, path, result );
				continue;
			}

			if ( !float.IsFinite( parameter.Minimum ) || !float.IsFinite( parameter.Maximum ) || parameter.Maximum < parameter.Minimum )
				result.Error( path, "Numeric parameter range must be finite and ordered." );
			if ( !float.IsFinite( parameter.DefaultValue ) || parameter.DefaultValue < parameter.Minimum || parameter.DefaultValue > parameter.Maximum )
				result.Error( $"{path}/DefaultValue", "Default value must be finite and inside the parameter range." );
			if ( parameter.Type == AudioParameterType.Discrete
				&& (!IsIntegral( parameter.Minimum ) || !IsIntegral( parameter.Maximum ) || !IsIntegral( parameter.DefaultValue )) )
				result.Error( path, "Discrete parameter bounds and default must be whole numbers." );
		}

		return parameters;
	}

	private static void ValidateLabels( AudioParameter parameter, string path, AudioEventValidationResult result )
	{
		if ( parameter.Labels is null || parameter.Labels.Count == 0 )
		{
			result.Error( $"{path}/Labels", "Labeled parameter requires at least one label." );
			return;
		}

		var labels = new HashSet<string>( StringComparer.Ordinal );
		for ( var i = 0; i < parameter.Labels.Count; i++ )
		{
			var label = parameter.Labels[i];
			if ( string.IsNullOrWhiteSpace( label ) || label != label.Trim() )
				result.Error( $"{path}/Labels[{i}]", "Label must be non-empty and trimmed." );
			else if ( label != label.ToLowerInvariant() )
				result.Error( $"{path}/Labels[{i}]", $"Label '{label}' must be lower-case." );
			else if ( !labels.Add( label ) )
				result.Error( $"{path}/Labels[{i}]", $"Duplicate label '{label}'." );
		}

		if ( string.IsNullOrWhiteSpace( parameter.DefaultLabel ) || !labels.Contains( parameter.DefaultLabel ) )
			result.Error( $"{path}/DefaultLabel", "Default label must reference a declared label." );
	}

	private static int ValidateTrack(
        AudioEvent audioEvent,
		AudioTrack track,
		string path,
		HashSet<string> trackNames,
		IReadOnlyDictionary<string, AudioParameter> parameters,
		AudioEventValidationResult result )
	{
		if ( string.IsNullOrWhiteSpace( track.Name ) || track.Name != track.Name.Trim() )
			result.Error( $"{path}/Name", "Track name must be non-empty and trimmed." );
		else if ( !trackNames.Add( track.Name ) )
			result.Error( $"{path}/Name", $"Duplicate track name '{track.Name}'." );
		if(!Enum.IsDefined(track.TransitionRole))result.Error(path,"Unknown transition stem role.");
		ValidateGain( track.Volume, track.Pitch, path, result );
		if (track.Tone is { IsValid: false }) result.Error($"{path}/Tone", "Bass gain must be -18 to 6 dB and frequency 40 to 500 Hz; both must be finite.");
		ValidateConditions( track.Conditions, $"{path}/Conditions", parameters, result );
		ValidateAutomations( track.Automations, $"{path}/Automations", parameters, result );
		if ( track.Instruments is null )
		{
			result.Error( $"{path}/Instruments", "Instrument collection cannot be null." );
			return 0;
		}

		var count = 0;
		for ( var i = 0; i < track.Instruments.Count; i++ )
		{
			var instrument = track.Instruments[i];
			var instrumentPath = $"{path}/Instruments[{i}]";
			if ( instrument is null )
			{
				result.Error( instrumentPath, "Instrument cannot be null." );
				continue;
			}

			count++;
            if(track.Overlay is not null && instrument.Echo is not null)result.Error(instrumentPath+"/Echo","Echo is not supported on overlay phrase tracks.");
			ValidateInstrument( audioEvent, instrument, instrumentPath, parameters, result );
		}
		return count;
	}

	private static void ValidateInstrument(
        AudioEvent audioEvent,
		AudioInstrument instrument,
		string path,
		IReadOnlyDictionary<string, AudioParameter> parameters,
		AudioEventValidationResult result )
	{

		if(instrument.Echo is {} echo && (!echo.IsValid || !audioEvent.UI || audioEvent.SharedMusicOutput != echo.ReleaseOnly || instrument.IsSubEvent || instrument.LayerControl is not null))
            result.Error(path+"/Echo","Echo requires valid settings and a local source instrument. Shared stems require ReleaseOnly; native sources require it off.");
		if(!float.IsFinite(instrument.SubEventExitFadeSeconds)||instrument.SubEventExitFadeSeconds<0||instrument.SubEventExitFadeSeconds>10)result.Error(path,"Sub-event exit fade must be 0 to 10 seconds.");
        if(!instrument.IsSubEvent && (instrument.SubEventExitFadeSeconds>0 || instrument.RetainSubEventPositionOnTimelineJump))result.Error(path,"Child retention controls require a sub-event instrument.");
		if ( instrument.IsSubEvent )
		{
			if ( instrument.SourceId!=Guid.Empty ) result.Error( path, "A sub-graph node cannot reference source data." );
			if(!float.IsFinite(instrument.SubEventTransitionBeats) || instrument.SubEventTransitionBeats<0 || instrument.SubEventTransitionBeats>16)result.Error(path,"Sub-event transition beats must be from 0 to 16.");
            if ( !Enum.IsDefined( instrument.SubEventQuantization ) ) result.Error( path, "Unknown sub-graph change boundary." );
			if ( !Enum.IsDefined( instrument.SubEventSelection ) ) result.Error( path, "Unknown sub-graph selection mode." );
			if ( instrument.SubEvents.Count > 64 ) result.Error( path, "A sub-graph instrument supports up to 64 choices." );
			for ( var i = 0; i < instrument.SubEvents.Count; i++ )
			{
				var choice = instrument.SubEvents[i];
				if ( choice?.Event is null ) result.Error( $"{path}/SubEvents[{i}]", "A child Audio Event is required." );
				if ( choice is not null ) ValidateConditions( choice.Conditions, $"{path}/SubEvents[{i}]/Conditions", parameters, result );
                if(choice?.ExitEvent is {} exit && (!exit.SharedMusicOutput || exit.TimelineDuration<=0))result.Error(path,"Exit event must have shared music output and a positive duration; the parent plays its grid once.");
                if(choice is not null && (!float.IsFinite(choice.ExitEventOverlapSeconds) || choice.ExitEventOverlapSeconds<0 || choice.ExitEventOverlapSeconds>0 && (choice.ExitEvent is null || choice.ExitEventOverlapSeconds>=choice.ExitEvent.TimelineDuration)))result.Error(path,"Exit event overlap must be non-negative and shorter than its exit passage.");
                if(choice?.EntryQuantization is {} entry && !Enum.IsDefined(entry))result.Error(path,"Unknown child entry boundary.");
                if(choice is not null) {
                 ValidateConditions(choice.EarlyEntryConditions,$"{path}/SubEvents[{i}]/EarlyEntryConditions",parameters,result);
                 if(!float.IsFinite(choice.EntryCrossfadeSeconds) || choice.EntryCrossfadeSeconds<0 || choice.EntryCrossfadeSeconds>10)result.Error(path,"Child entry crossfade seconds must be from 0 to 10.");
                }
                if(choice?.EntryFadeBeats is {} fade && (!float.IsFinite(fade) || fade<0 || fade>16))result.Error(path,"Child entry fade beats must be from 0 to 16.");
			}
		}
		else if ( instrument.LayerControl is not null ) { }
		else if ( instrument.SourceId==Guid.Empty )
			result.Error( $"{path}/SourceId", "A sound instrument requires a graph-local source ID." );
		else
			{
            try { _ = audioEvent.Source(instrument); }
            catch(Exception error) { result.Error(path,error.Message); }
            }
		if ( !float.IsFinite( instrument.StartTime ) || instrument.StartTime < 0f )
			result.Error( $"{path}/StartTime", "Instrument start time must be finite and non-negative." );
		if(!float.IsFinite(instrument.OverlayBodySeconds) || instrument.OverlayBodySeconds<0
            || instrument.OverlayBodySeconds>0 && (instrument.OverlayBodySeconds<.001f || instrument.TrimEnd<=instrument.TrimStart || instrument.OverlayBodySeconds>instrument.TrimEnd-instrument.TrimStart))
            result.Error($"{path}/OverlayBodySeconds", "Phrase body must be zero or a finite duration within the explicit source trim, at least 0.001 seconds.");
		if(instrument.MaximumMusicWaitBeats<0 || instrument.MaximumMusicWaitBeats>64)
			result.Error($"{path}/MaximumMusicWaitBeats", "Shared music wait must be between 0 and 64 beats.");
		if ( !float.IsFinite( instrument.TrimStart ) || instrument.TrimStart < 0f )
			result.Error( $"{path}/TrimStart", "Instrument trim start must be finite and non-negative." );
		if ( !float.IsFinite( instrument.TrimEnd ) || instrument.TrimEnd < 0f )
			result.Error( $"{path}/TrimEnd", "Instrument trim end must be finite and non-negative." );
		else if ( instrument.TrimEnd > 0f && instrument.TrimEnd <= instrument.TrimStart )
			result.Error( $"{path}/TrimEnd", "Instrument trim end must be greater than trim start, or zero to use the source end." );
		if ( !float.IsFinite( instrument.FadeInDuration ) || instrument.FadeInDuration < 0f )
			result.Error( $"{path}/FadeInDuration", "Instrument fade-in duration must be finite and non-negative." );
		if ( !float.IsFinite( instrument.FadeInCurveMidpoint ) || instrument.FadeInCurveMidpoint < 0f || instrument.FadeInCurveMidpoint > 1f )
			result.Error( $"{path}/FadeInCurveMidpoint", "Instrument fade-in curve midpoint must be finite and between zero and one." );
		if ( !float.IsFinite( instrument.FadeOutDuration ) || instrument.FadeOutDuration < 0f )
			result.Error( $"{path}/FadeOutDuration", "Instrument fade-out duration must be finite and non-negative." );
		if ( !float.IsFinite( instrument.FadeOutCurveMidpoint ) || instrument.FadeOutCurveMidpoint < 0f || instrument.FadeOutCurveMidpoint > 1f )
			result.Error( $"{path}/FadeOutCurveMidpoint", "Instrument fade-out curve midpoint must be finite and between zero and one." );

		ValidateConditions( instrument.Conditions, $"{path}/Conditions", parameters, result );
		ValidateAutomations( instrument.Automations, $"{path}/Automations", parameters, result );
	}

	internal static void ValidateSoundEvent(
		AudioSourceDefinition soundEvent,
		string path,
		IReadOnlyDictionary<string, AudioParameter> parameters,
		AudioEventValidationResult result )
	{
		if ( soundEvent.SelectionMode is not (SoundTrackSelectionMode.Random or SoundTrackSelectionMode.RandomExclusive) )
			result.Error( $"{path}/SelectionMode", "Source selection mode is invalid." );
		if ( !float.IsFinite( soundEvent.Volume.Min ) || !float.IsFinite( soundEvent.Volume.Max )
			|| soundEvent.Volume.Min < 0f || soundEvent.Volume.Max < soundEvent.Volume.Min )
			result.Error( $"{path}/Volume", "Source volume range must be finite, ordered, and non-negative." );
		if ( !float.IsFinite( soundEvent.Pitch.Min ) || !float.IsFinite( soundEvent.Pitch.Max )
			|| soundEvent.Pitch.Min <= 0f || soundEvent.Pitch.Max < soundEvent.Pitch.Min )
			result.Error( $"{path}/Pitch", "Source pitch range must be finite, ordered, and greater than zero." );
		if ( soundEvent.Tracks is null || soundEvent.Tracks.Count == 0 )
		{
			result.Error( $"{path}/Tracks", "Source requires at least one track." );
			return;
		}

		for ( var i = 0; i < soundEvent.Tracks.Count; i++ )
		{
			var track = soundEvent.Tracks[i];
			var trackPath = $"{path}/Tracks[{i}]";
			if ( track is null )
			{
				result.Error( trackPath, "Sound track cannot be null." );
				continue;
			}
			if ( track.Sound is null )
				result.Error( $"{trackPath}/Sound", "Sound track requires a sound file." );
			if ( !float.IsFinite( track.Offset ) || track.Offset < 0f )
				result.Error( $"{trackPath}/Offset", "Sound track offset must be finite and non-negative." );
			if ( !float.IsFinite( track.Length ) || track.Length < 0f )
				result.Error( $"{trackPath}/Length", "Sound track length must be finite and non-negative." );
			ValidateConditions( track.Conditions, $"{trackPath}/Conditions", parameters, result );
		}
	}

	private static void ValidateConditions(
		IReadOnlyList<AudioParameterCondition> conditions,
		string path,
		IReadOnlyDictionary<string, AudioParameter> parameters,
		AudioEventValidationResult result )
	{
		if ( conditions is null )
		{
			result.Error( path, "Condition collection cannot be null." );
			return;
		}

		for ( var i = 0; i < conditions.Count; i++ )
		{
			var condition = conditions[i];
			var conditionPath = $"{path}[{i}]";
			if ( condition is null )
			{
				result.Error( conditionPath, "Condition cannot be null." );
				continue;
			}
			var parameter = condition.Parameter;
			if ( parameter is null || !parameters.Values.Any( candidate => ReferenceEquals( candidate, parameter ) ) )
			{
				result.Error( $"{conditionPath}/Parameter", "Condition must reference a parameter used by this AudioEvent." );
				continue;
			}

			if ( parameter.Type == AudioParameterType.Labeled )
			{
				if ( string.IsNullOrWhiteSpace( condition.Label ) || !parameter.Labels.Contains( condition.Label, StringComparer.Ordinal ) )
					result.Error( $"{conditionPath}/Label", $"Label '{condition.Label}' is not declared by parameter '{parameter.Name}'." );
			}
			else if ( !float.IsFinite( condition.Minimum ) || !float.IsFinite( condition.Maximum ) || condition.Maximum < condition.Minimum )
			{
				result.Error( conditionPath, "Numeric condition range must be finite and ordered." );
			}
			else
			{
				if ( condition.Minimum < parameter.Minimum || condition.Maximum > parameter.Maximum )
					result.Error( conditionPath, $"Condition must stay within parameter '{parameter.Name}' range {parameter.Minimum} to {parameter.Maximum}." );
				if ( parameter.Type == AudioParameterType.Discrete
					&& (MathF.Abs( condition.Minimum - MathF.Round( condition.Minimum ) ) > 0.0001f
						|| MathF.Abs( condition.Maximum - MathF.Round( condition.Maximum ) ) > 0.0001f) )
					result.Error( conditionPath, $"Discrete parameter '{parameter.Name}' requires whole-number condition bounds." );
			}
		}
	}

	private static void ValidateAutomations(
		IReadOnlyList<AudioAutomation> automations,
		string path,
		IReadOnlyDictionary<string, AudioParameter> parameters,
		AudioEventValidationResult result )
	{
		if ( automations is null )
		{
			result.Error( path, "Automation collection cannot be null." );
			return;
		}

		var bindings = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		for ( var i = 0; i < automations.Count; i++ )
		{
			var automation = automations[i];
			var automationPath = $"{path}[{i}]";
			if ( automation is null )
			{
				result.Error( automationPath, "Automation cannot be null." );
				continue;
			}
			var parameter = automation.Parameter;
			if ( parameter is null || !parameters.Values.Any( candidate => ReferenceEquals( candidate, parameter ) ) )
			{
				result.Error( $"{automationPath}/Parameter", "Automation must reference a parameter used by this AudioEvent." );
				continue;
			}
			if ( parameter.Type == AudioParameterType.Labeled )
				result.Error( $"{automationPath}/Parameter", "Labeled parameters cannot drive numeric automation in v1." );
			if ( !bindings.Add( $"{automation.Target}:{parameter.ResourcePath ?? parameter.Name}" ) )
				result.Error( automationPath, $"Duplicate {automation.Target} automation for '{parameter.Name}'." );
			if ( automation.Points is null || automation.Points.Count < 2 )
			{
				result.Error( $"{automationPath}/Points", "Automation requires at least two points." );
				continue;
			}

			var previousInput = float.NegativeInfinity;
			for ( var pointIndex = 0; pointIndex < automation.Points.Count; pointIndex++ )
			{
				var point = automation.Points[pointIndex];
				var pointPath = $"{automationPath}/Points[{pointIndex}]";
				if ( point is null || !float.IsFinite( point.Input ) || !float.IsFinite( point.Output ) )
				{
					result.Error( pointPath, "Automation point values must be finite." );
					continue;
				}
				if ( point.Input <= previousInput )
					result.Error( pointPath, "Automation point inputs must be strictly increasing." );
				if ( point.Input < parameter.Minimum || point.Input > parameter.Maximum )
					result.Error( pointPath, "Automation input must be inside the parameter range." );
				if ( point.Output < 0f || (automation.Target == AudioAutomationTarget.Pitch && point.Output <= 0f) )
					result.Error( pointPath, $"{automation.Target} automation output is outside its valid range." );
				previousInput = point.Input;
			}
		}
	}

	private static void ValidateGain( float volume, float pitch, string path, AudioEventValidationResult result )
	{
		if ( !float.IsFinite( volume ) || volume < 0f )
			result.Error( $"{path}/Volume", "Volume must be finite and non-negative." );
		if ( !float.IsFinite( pitch ) || pitch <= 0f )
			result.Error( $"{path}/Pitch", "Pitch must be finite and greater than zero." );
	}

	private static bool IsIntegral( float value )
	{
		return MathF.Abs( value - MathF.Round( value ) ) <= 0.0001f;
	}
}

