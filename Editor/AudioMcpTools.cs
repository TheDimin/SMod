using System;
using System.Linq;
using System.Threading.Tasks;
using Smod;
using Smod.Editor;

namespace Editor.Mcp;

[McpToolset( "audio", "Inspect and validate authored AudioEvent resources." )]
public static class AudioMcpTools
{
	/// <summary>Open or focus an Audio Event editor and measure a warm waveform UI rebuild. Preserves unsaved values and writes no assets.</summary>
	[McpTool( "check_waveform_reload" )]
	public static async Task<object> CheckWaveformReload( string resourcePath )
	{
		var asset = AssetSystem.FindByPath( resourcePath );
		if ( asset is null || !IAssetEditor.OpenInEditor( asset, out var editor ) || editor is not AudioEventEditorWindow window )
			throw new ArgumentException( "An Audio Event editor asset is required." );
		return await window.CheckWaveformReloadAsync();
	}

    /// <summary>Reload one saved Smod resource into its cached editor object. Use after creating dependencies when compilation alone leaves defaults cached. Replaces unsaved in-memory values for this resource only; writes no file. Validate the parent event afterward.</summary>
    [McpTool("reload_saved_audio_resource")]
    public static object ReloadSaved(string resourcePath)
    {
        var asset=AssetSystem.FindByPath(resourcePath);
        var resource=asset?.LoadResource() as Sandbox.GameResource;
        if(resource is not (AudioEvent or AudioParameter))throw new ArgumentException("An Audio Event or Audio Parameter resource is required.");
        resource.LoadFromJson(System.IO.File.ReadAllText(asset.AbsolutePath));
        return new {Path=asset.RelativePath,Reloaded=true};
    }

	/// <summary>Detects silence-separated regions in one sound file using the same analysis as the AudioEventEditor slicer.</summary>
	[McpTool.ReadOnly( "detect_sound_slices" )]
	public static async Task<object> DetectSoundSlices(
		string resourcePath,
		float thresholdDecibels = -40f,
		float minimumSilence = 0.08f )
	{
		if ( string.IsNullOrWhiteSpace( resourcePath ) )
			throw new ArgumentException( "Sound resource path is required.", nameof( resourcePath ) );

		var sound = SoundFile.Load( resourcePath );
		if ( !sound.IsValid() || !await sound.LoadAsync() )
			throw new InvalidOperationException( $"Sound file '{resourcePath}' could not be loaded." );

		var samples = await sound.GetSamplesInterleavedAsync() ?? Array.Empty<short>();
		var slices = AudioClipSliceDetector.Detect(
			samples,
			Math.Max( 1, sound.Channels ),
			sound.Duration,
			thresholdDecibels,
			minimumSilence,
			padding: 0.025f,
			minimumClipLength: 0.06f );
		return new
		{
			path = resourcePath,
			duration = sound.Duration,
			channels = sound.Channels,
			sliceCount = slices.Count,
			slices = slices.Select( ( slice, index ) => new
			{
				index = index + 1,
				offset = slice.Offset,
				length = slice.Length,
				end = slice.End
			} ).ToArray()
		};
	}

	/// <summary>Validates every AudioEvent resource and returns authored paths with actionable issues.</summary>
	[McpTool.ReadOnly( "validate_events" )]
	public static object ValidateEvents()
	{
		var events = ResourceLibrary.GetAll<AudioEvent>()
			.Where( audioEvent => audioEvent is not null )
			.Select( audioEvent =>
			{
				var validation = AudioEventValidator.Validate( audioEvent );
				return new
				{
					path = audioEvent.ResourcePath,
					isValid = validation.IsValid,
					issues = validation.Issues.Select( issue => new { issue.Path, issue.Message } ).ToArray()
				};
			})
			.OrderBy( result => result.path )
			.ToArray();

		return new
		{
			eventCount = events.Length,
			invalidEventCount = events.Count( audioEvent => !audioEvent.isValid ),
			events
		};
	}

	/// <summary>Returns the authored timeline, parameter bindings, native sounds, derived durations, and validation issues for one AudioEvent.</summary>
	[McpTool.ReadOnly( "inspect_event" )]
	public static object InspectEvent( string resourcePath )
	{
		if ( string.IsNullOrWhiteSpace( resourcePath ) )
			throw new ArgumentException( "Audio event resource path is required.", nameof( resourcePath ) );

		var audioEvent = ResourceLibrary.Get<AudioEvent>( resourcePath );
		if ( audioEvent is null )
			return new { path = resourcePath, found = false, issues = new[] { "Audio event resource could not be loaded." } };

		var validation = AudioEventValidator.Validate( audioEvent );
		var tracks = (audioEvent.MasterTrack is null ? Array.Empty<InspectedTrack>() : new[] { InspectTrack( audioEvent, audioEvent.MasterTrack, true, audioEvent.BeatGrids?.FirstOrDefault()?.BeatsPerMinute ?? 120d ) })
			.Concat( audioEvent.Tracks?.Where( track => track is not null ).Select( track => InspectTrack( audioEvent, track, false, audioEvent.BeatGrids?.FirstOrDefault()?.BeatsPerMinute ?? 120d ) ) ?? Enumerable.Empty<InspectedTrack>() )
			.ToArray();

		return new
		{
			path = audioEvent.ResourcePath,
			found = true,
			isValid = validation.IsValid,
			derivedDuration = tracks.Select( track => track.Duration ).DefaultIfEmpty().Max(),
			fadeOutDuration = audioEvent.FadeOutDuration,
			sustainUntilStopped = audioEvent.SustainUntilStopped,
			playback = new
			{
				audioEvent.UI,
				audioEvent.Decibels,
				audioEvent.OcclusionEnabled,
				audioEvent.ReverbEnabled,
				audioEvent.AirAbsorption,
				audioEvent.DistanceAttenuation,
				audioEvent.Distance,
				defaultMixer = audioEvent.DefaultMixer.ToString()
			},
			timelineSampleRate = audioEvent.TimelineSampleRate,
			timelineDuration = audioEvent.TimelineDuration,
			tempo = new { audioEvent.TempoMap?.BeatsPerMinute, audioEvent.TempoMap?.BeatsPerBar },
			beatGrids = audioEvent.BeatGrids?.Select( grid => new {
                grid.Name, startSeconds = grid.Start.Seconds, endSeconds = grid.End.Seconds,
                grid.BeatsPerMinute, grid.BeatsPerBar, grid.BarsPerPhrase } ).ToArray(),
            destinationMarkers = audioEvent.DestinationMarkers?.Select( marker => new
			{
				marker.Id,
				marker.Name,
				positionSeconds = marker.Position.Seconds,
				positionSample = marker.Position.ToSamples( audioEvent.TimelineSampleRate ),
				referencedBy = audioEvent.TransitionMarkers.Where( transition => transition.DestinationMarkerId == marker.Id ).Select( transition => transition.Id )
					.Concat( audioEvent.TransitionRegions.Where( transition => transition.DestinationMarkerId == marker.Id ).Select( transition => transition.Id ) )
					.ToArray()
			} ).ToArray(),
			transitionMarkers = audioEvent.TransitionMarkers?.Select( marker => new
			{
				marker.Id,
				marker.Name,
				positionSeconds = marker.Position.Seconds,
				marker.DestinationMarkerId,
				marker.Priority,
				conditionCount = marker.Conditions?.Conditions?.Count ?? 0
			} ).ToArray(),
			transitionRegions = audioEvent.TransitionRegions?.Select( region => new
			{
				region.Id,
				region.Name,
				startSeconds = region.Start.Seconds,
				endSeconds = region.End.Seconds,
				region.DestinationMarkerId,
				quantization = region.Quantization.ToString(),
                region.FadeOutBeats, region.FadeInBeats,
				region.Priority,
				conditionCount = region.Conditions?.Conditions?.Count ?? 0
			} ).ToArray(),
			loopRegions = audioEvent.LoopRegions?.Select( region => new
			{
				region.Id,
				region.Name,
				startSeconds = region.Start.Seconds,
				endSeconds = region.End.Seconds,
				conditionCount = region.Conditions?.Conditions?.Count ?? 0
			} ).ToArray(),
			sources = audioEvent.Sources?.Where( source => source is not null ).Select( source => new
			{
				id = source.Id,
				volume = source.Volume,
				pitch = source.Pitch,
				selectionMode = source.SelectionMode.ToString(),
				tracks = source.Tracks?.Where( track => track is not null ).Select( track => new
				{
					sound = track.Sound?.ResourcePath,
						offset = track.Offset,
						length = track.Length,
						conditions = track.Conditions?.Count ?? 0
					} ).ToArray() ?? Array.Empty<object>()
			} ).ToArray() ?? Array.Empty<object>(),
			parameters = audioEvent.Parameters?.Where( parameter => parameter is not null ).Select( parameter => new InspectedParameter(
				parameter.ResourcePath,
				parameter.Name,
				parameter.Type.ToString(),
				parameter.Type == AudioParameterType.Labeled ? null : parameter.Minimum,
				parameter.Type == AudioParameterType.Labeled ? null : parameter.Maximum,
				parameter.Type == AudioParameterType.Labeled ? null : parameter.DefaultValue,
				parameter.DefaultLabel,
				parameter.Labels?.ToArray() ?? Array.Empty<string>() ) ).ToArray() ?? Array.Empty<InspectedParameter>(),
			tracks,
			issues = validation.Issues.Select( issue => new { issue.Path, issue.Message } ).ToArray()
		};
	}

	private static InspectedTrack InspectTrack( AudioEvent graph, AudioTrack track, bool isMaster, double beatsPerMinute )
	{
		var instruments = track.Instruments?.Where( instrument => instrument is not null ).Select( instrument =>
		{
			var source = AudioSourceAuthoring.Resolve( graph, instrument );
			var duration = EstimateDuration( graph, instrument, beatsPerMinute );
			return new InspectedInstrument(
				instrument.SourceId,
				source?.Volume.ToString(),
				source?.Pitch.ToString(),
				source?.SelectionMode.ToString(),
				source?.Tracks?.Where( track => track?.Sound.IsValid() == true ).Select( track => track.Sound.ResourcePath ).ToArray() ?? Array.Empty<string>(),
				instrument.StartTime,
				instrument.TrimStart,
				instrument.TrimEnd,
                instrument.MaximumMusicWaitBeats,
                instrument.OverlayBars?.ToArray() ?? Array.Empty<int>(),
				duration,
				instrument.StartTime + duration,
				instrument.IsSubEvent ? instrument.SubEvents.Count : source?.Tracks?.Count ?? 0,
				instrument.Conditions?.Where( condition => condition is not null ).Select( condition => new InspectedCondition( condition.Parameter?.Name, condition.Parameter?.ResourcePath, condition.Invert, condition.Minimum, condition.Maximum, condition.Label ) ).ToArray() ?? Array.Empty<InspectedCondition>(),
				instrument.Automations?.Where( automation => automation is not null ).Select( InspectAutomation ).ToArray() ?? Array.Empty<InspectedAutomation>(),
				instrument.SubEventSelection.ToString(),
				instrument.SubEventQuantization.ToString(),
                instrument.RotateSubEventsAtEnd, instrument.SubEventTransitionBeats,
				instrument.SubEvents?.Select( choice => new InspectedSubEvent( choice?.Event?.ResourcePath,
					choice?.Conditions?.Select( condition => new InspectedCondition( condition.Parameter?.Name, condition.Parameter?.ResourcePath, condition.Invert, condition.Minimum, condition.Maximum, condition.Label ) ).ToArray() ?? Array.Empty<InspectedCondition>(), choice?.ExitEvent?.ResourcePath, choice?.ResumePlayback ?? false, choice?.EntryQuantization?.ToString(), choice?.EntryFadeBeats, choice?.EntryCrossfadeSeconds ?? 0, choice?.EarlyEntryConditions?.Select(c=>new InspectedCondition(c.Parameter?.Name,c.Parameter?.ResourcePath,c.Invert,c.Minimum,c.Maximum,c.Label)).ToArray() ?? Array.Empty<InspectedCondition>() ) ).ToArray() ?? Array.Empty<InspectedSubEvent>() );
		}).ToArray() ?? Array.Empty<InspectedInstrument>();

		return new InspectedTrack(
			track.Name,
			isMaster,
			track.Volume,
			track.Pitch,
			instruments.Select( instrument => instrument.EndTime ).DefaultIfEmpty().Max(),
			track.Conditions?.Where( condition => condition is not null ).Select( condition => new InspectedCondition( condition.Parameter?.Name, condition.Parameter?.ResourcePath, condition.Invert, condition.Minimum, condition.Maximum, condition.Label ) ).ToArray() ?? Array.Empty<InspectedCondition>(),
			track.Automations?.Where( automation => automation is not null ).Select( InspectAutomation ).ToArray() ?? Array.Empty<InspectedAutomation>(),
			instruments );
	}

	private static InspectedAutomation InspectAutomation( AudioAutomation automation )
	{
		return new InspectedAutomation(
			automation.Parameter?.Name,
			automation.Parameter?.ResourcePath,
			automation.Target.ToString(),
			automation.Points?.Where( point => point is not null ).Select( point => new InspectedAutomationPoint( point.Input, point.Output ) ).ToArray() ?? Array.Empty<InspectedAutomationPoint>() );
	}

	private static float EstimateDuration( AudioEvent graph, AudioInstrument instrument, double beatsPerMinute )
	{
		if ( instrument?.IsSubEvent == true || instrument?.CompoundHits is { Count: > 0 } ) return AudioTimelineView.EstimateDuration( instrument, beatsPerMinute );
		var source = AudioSourceAuthoring.Resolve( graph, instrument );
		if ( source?.Tracks is null || source.Tracks.Count == 0 )
			return 0f;
		var sourceDuration = source.Tracks
			.Where( track => track?.Sound.IsValid() == true && track.Sound.IsLoaded )
			.Select( track => track.Length > 0f ? track.Length : MathF.Max( 0f, track.Sound.Duration - track.Offset ) )
			.DefaultIfEmpty()
			.Max();
		var trimEnd = instrument.TrimEnd > 0f ? MathF.Min( instrument.TrimEnd, sourceDuration ) : sourceDuration;
		return MathF.Max( 0f, trimEnd - MathF.Min( instrument.TrimStart, trimEnd ) );
	}

	private sealed record InspectedTrack(
		string Name,
		bool IsMaster,
		float Volume,
		float Pitch,
		float Duration,
		InspectedCondition[] Conditions,
		InspectedAutomation[] Automations,
		InspectedInstrument[] Instruments );

	private sealed record InspectedParameter(
		string ResourcePath,
		string Name,
		string Type,
		float? Minimum,
		float? Maximum,
		float? DefaultValue,
		string DefaultLabel,
		string[] Labels );

	private sealed record InspectedInstrument(
		Guid SourceId,
		string SourceVolume,
		string SourcePitch,
		string SourceSelectionMode,
		string[] SoundFiles,
		float StartTime,
		float TrimStart,
		float TrimEnd,
        int MaximumMusicWaitBeats,
        int[] OverlayBars,
		float Duration,
		float EndTime,
		int VariationCount,
		InspectedCondition[] Conditions,
		InspectedAutomation[] Automations, string SubEventSelection, string SubEventQuantization, bool RotateSubEventsAtEnd, float SubEventTransitionBeats, InspectedSubEvent[] SubEvents );

	private sealed record InspectedSubEvent( string Event, InspectedCondition[] Conditions, string ExitEvent, bool ResumePlayback, string EntryQuantization, float? EntryFadeBeats, float EntryCrossfadeSeconds, InspectedCondition[] EarlyEntryConditions );
	private sealed record InspectedCondition( string Parameter, string ParameterResource, bool Invert, float Minimum, float Maximum, string Label );
	private sealed record InspectedAutomation( string Parameter, string ParameterResource, string Target, InspectedAutomationPoint[] Points );
	private sealed record InspectedAutomationPoint( float Input, float Output );
}
