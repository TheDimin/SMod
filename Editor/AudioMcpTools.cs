using System;
using System.Linq;
using System.Threading.Tasks;
using Smod;

namespace Editor.Mcp;

[McpToolset( "audio", "Inspect and validate authored AudioEvent resources." )]
public static class AudioMcpTools
{
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
		var tracks = (audioEvent.MasterTrack is null ? Array.Empty<InspectedTrack>() : new[] { InspectTrack( audioEvent.MasterTrack, true ) })
			.Concat( audioEvent.Tracks?.Where( track => track is not null ).Select( track => InspectTrack( track, false ) ) ?? Enumerable.Empty<InspectedTrack>() )
			.ToArray();

		return new
		{
			path = audioEvent.ResourcePath,
			found = true,
			isValid = validation.IsValid,
			derivedDuration = tracks.Select( track => track.Duration ).DefaultIfEmpty().Max(),
			fadeOutDuration = audioEvent.FadeOutDuration,
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

	private static InspectedTrack InspectTrack( AudioTrack track, bool isMaster )
	{
		var instruments = track.Instruments?.Where( instrument => instrument is not null ).Select( instrument =>
		{
			var duration = EstimateDuration( instrument );
			return new InspectedInstrument(
				instrument.Sound?.ResourcePath,
				instrument.Sound?.EmbeddedResource is not null,
				instrument.Sound?.Tracks?.Where( track => track?.Sound.IsValid() == true ).Select( track => track.Sound.ResourcePath ).ToArray() ?? Array.Empty<string>(),
				instrument.StartTime,
				instrument.TrimStart,
				instrument.TrimEnd,
				duration,
				instrument.StartTime + duration,
				instrument.Sound?.Tracks?.Count ?? 0,
				instrument.Conditions?.Where( condition => condition is not null ).Select( condition => new InspectedCondition( condition.Parameter?.Name, condition.Parameter?.ResourcePath, condition.Invert, condition.Minimum, condition.Maximum, condition.Label ) ).ToArray() ?? Array.Empty<InspectedCondition>(),
				instrument.Automations?.Where( automation => automation is not null ).Select( InspectAutomation ).ToArray() ?? Array.Empty<InspectedAutomation>() );
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

	private static float EstimateDuration( AudioInstrument instrument )
	{
		var sound = instrument?.Sound;
		if ( sound?.Tracks is null || sound.Tracks.Count == 0 )
			return 0f;
		var sourceDuration = sound.Tracks
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
		string SoundEvent,
		bool IsEmbedded,
		string[] SoundFiles,
		float StartTime,
		float TrimStart,
		float TrimEnd,
		float Duration,
		float EndTime,
		int VariationCount,
		InspectedCondition[] Conditions,
		InspectedAutomation[] Automations );

	private sealed record InspectedCondition( string Parameter, string ParameterResource, bool Invert, float Minimum, float Maximum, string Label );
	private sealed record InspectedAutomation( string Parameter, string ParameterResource, string Target, InspectedAutomationPoint[] Points );
	private sealed record InspectedAutomationPoint( float Input, float Output );
}
