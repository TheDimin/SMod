namespace Smod;

public sealed partial class AudioEventInstance
{
	private sealed class InstrumentTriggerState
	{
		internal bool Matched, Started, Completed, Cancelled;
		internal float Start;
	}

	private readonly Dictionary<AudioInstrument, InstrumentTriggerState> _instrumentTriggers = new();

	private bool UpdateInstrumentTriggers()
	{
		var changed = false;
		foreach ( var track in _description.Resource.Tracks.Prepend( _description.Resource.MasterTrack ) )
		foreach ( var instrument in track.Instruments.Where( i => i.StartTriggerConditions.Count > 0 ) )
		{
			if ( !_instrumentTriggers.TryGetValue( instrument, out var state ) )
				_instrumentTriggers[instrument] = state = new();
			var matches = AudioEventEvaluator.Matches( instrument.StartTriggerConditions, _parameters );
			if ( matches && !state.Matched )
			{
				foreach ( var voice in _voices.Where( v => v.Instrument == instrument ).ToArray() )
				{
					voice.CancelScheduledStart();
					voice.CancelScheduledStop();
					voice.Voice.Stop( 0 );
					_voices.Remove( voice );
				}
				_selectedTracks.Remove( instrument );
				state.Started = true;
				state.Completed = false;
				state.Cancelled = false;
				state.Start = CurrentTimelinePosition + (instrument.TriggerDelaySeconds ?? instrument.StartTime);
				changed = true;
			}
			state.Matched = matches;
			var active = _voices.FirstOrDefault( v => v.Instrument == instrument );
			if ( state.Started && !state.Completed && !state.Cancelled && instrument.StopTriggerConditions.Count > 0
				&& AudioEventEvaluator.Matches(instrument.StopTriggerConditions, _parameters) )
			{
				active?.CancelScheduledStart(); active?.CancelScheduledStop(); active?.Voice.Stop(0);
				state.Cancelled = true; changed = true;
			}
			// Native source time includes pitch changes. A wall-clock duration would cut a bent note short.
			if ( !_paused && state.Started && !state.Completed && !state.Cancelled && active is { Pending: false }
				&& (!active.Voice.IsPlaying || active.Voice.Time >= ResolveTrimEnd( active )) )
			{
				active.Voice.Stop( 0 );
				state.Completed = true;
				changed = true;
			}
		}
		return changed;
	}

	private bool TriggerAllowsPlayback( AudioInstrument instrument ) => instrument.StartTriggerConditions.Count == 0
		|| _instrumentTriggers.TryGetValue( instrument, out var state ) && state.Started && !state.Completed && !state.Cancelled;

	private bool WaitingForEntryInstrument( AudioSubEventChoice choice ) => choice.EntryAfterInstrument != Guid.Empty
		&& _instrumentTriggers.Any( pair => pair.Key.Id == choice.EntryAfterInstrument && pair.Value.Started && !pair.Value.Completed );

	private bool EntryInstrumentCompleted( AudioSubEventChoice choice ) => choice.EntryAfterInstrument != Guid.Empty
		&& _instrumentTriggers.Any( pair => pair.Key.Id == choice.EntryAfterInstrument && pair.Value.Started && pair.Value.Completed );

	private string InstrumentTriggerReason( AudioInstrument instrument )
	{
		if ( !_instrumentTriggers.TryGetValue( instrument, out var state ) ) return null;
		return !state.Started ? "Waiting for start trigger conditions."
			: state.Cancelled ? "Triggered passage cancelled; waiting for a new condition entry."
			: state.Completed ? "Triggered passage completed; waiting for a new condition entry."
			: "Parent start trigger: playing this passage once from its source start.";
	}

	private string EntryInstrumentWaitReason( AudioInstrument instrument )
	{
		if ( !_pendingSubEvents.TryGetValue( instrument, out var pending ) || !WaitingForEntryInstrument( pending.Choice ) ) return null;
		var source = _instrumentTriggers.Keys.First( i => i.Id == pending.Choice.EntryAfterInstrument );
		return $"Waiting for parent instrument '{source.Name}' ({source.Id:N}) to finish before starting the prepared song.";
	}
}

public static partial class AudioEventValidator
{
	private static void ValidateInstrumentTriggers( AudioEvent resource, string path,
		IReadOnlyDictionary<string, AudioParameter> parameters, AudioEventValidationResult result )
	{
		var tracks = (resource.Tracks ?? new()).Prepend( resource.MasterTrack ).Where( t => t is not null ).ToArray();
		var all = tracks.SelectMany( t => t.Instruments ?? new() ).Where( i => i is not null ).ToArray();
		foreach ( var track in tracks )
		foreach ( var instrument in track.Instruments ?? new() )
		{
			if ( instrument is null ) continue;
			if(instrument.TriggerDelaySeconds is {} delay && (!float.IsFinite(delay)||delay<0||delay>300||instrument.StartTriggerConditions is not {Count:>0}))result.Error(path,"Trigger Delay requires start conditions and 0 to 300 finite seconds.");
            if ( instrument.StopTriggerConditions is null ) result.Error(path, "Stop trigger conditions cannot be null.");
			else { ValidateConditions(instrument.StopTriggerConditions, path + "/StopTriggerConditions", parameters, result);
				if(instrument.StopTriggerConditions.Count > 0 && instrument.StartTriggerConditions is not {Count:>0}) result.Error(path, "Stop trigger conditions require a start trigger."); }
			if ( instrument.StartTriggerConditions is null )
				result.Error( path, "Start trigger conditions cannot be null." );
			else if ( instrument.StartTriggerConditions.Count > 0 )
			{
				ValidateConditions( instrument.StartTriggerConditions, path + "/StartTriggerConditions", parameters, result );
				if ( resource.SharedMusicOutput || instrument.IsSubEvent || track.Overlay?.Enabled == true )
					result.Error( path, "Start triggers require ordinary sound instruments in a non-shared parent event." );
				if ( instrument.TrimEnd <= instrument.TrimStart )
					result.Error( path, "A triggered passage needs a finite explicit trim end after its trim start." );
				if ( instrument.Conditions?.Count > 0 || track.Conditions?.Count > 0 || resource.MasterTrack?.Conditions?.Count > 0 )
					result.Error( path, "Use start trigger conditions and gain automation for a triggered passage; playback conditions can cut its completion short." );
			}
			foreach ( var choice in instrument.SubEvents ?? new() )
			{
				if ( choice is null || choice.EntryAfterInstrument == Guid.Empty ) continue;
				var sources = all.Where( i => i.Id == choice.EntryAfterInstrument ).ToArray();
				if ( sources.Length != 1 || sources[0].StartTriggerConditions is not { Count: > 0 } || sources[0].IsSubEvent )
					result.Error( path, "Entry After Instrument must identify one triggered sound instrument in this same event." );
			}
		}
	}
}
