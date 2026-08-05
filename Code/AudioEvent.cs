namespace Smod;

[AssetType( Name = "Audio Event", Extension = "audevt", Category = "Audio", Flags = AssetTypeFlags.NoEmbedding )]
public sealed partial class AudioEvent : GameResource
{
	[Property]
	public bool UI { get; set; }

	[Property]
	public int Decibels { get; set; } = 70;

	[Property]
	public bool OcclusionEnabled { get; set; } = true;

	[Property]
	public bool ReverbEnabled { get; set; } = true;

	[Property]
	public bool AirAbsorption { get; set; } = true;

	[Property]
	public bool DistanceAttenuation { get; set; } = true;

	[Property]
	public float Distance { get; set; } = 15000f;

	[Property]
	public Curve Falloff { get; set; } = new SoundEvent().Falloff;

	[Property]
	public Sandbox.Audio.MixerHandle DefaultMixer { get; set; }

	[Property, Range( 8000, 192000 ), Title( "Timeline Sample Rate" )]
	public int TimelineSampleRate { get; set; } = 48000;

	[Property, Range( 0f, 3600f ), Title( "Timeline Duration" )]
	public float TimelineDuration { get; set; }

	[Property, InlineEditor]
	public AudioTempoMap TempoMap { get; set; } = new();

	[Property, InlineEditor]
	public List<DestinationMarker> DestinationMarkers { get; set; } = new();

	[Property, InlineEditor]
	public List<TransitionMarker> TransitionMarkers { get; set; } = new();

	[Property, InlineEditor]
	public List<TransitionRegion> TransitionRegions { get; set; } = new();

	[Property, InlineEditor]
	public List<LoopRegion> LoopRegions { get; set; } = new();

	[Property, Range( 1, 64 ), Title( "Maximum Timeline Jumps Per Update" )]
	public int MaximumTimelineJumpsPerUpdate { get; set; } = 8;

	[Property, InlineEditor, Title( "Master Track" )]
	public AudioTrack MasterTrack { get; set; } = new() { Name = "Master" };

	[Property, InlineEditor]
	public List<AudioTrack> Tracks { get; set; } = new();

	[Property, Title( "Parameter Resources" )]
	public List<AudioParameter> Parameters { get; set; } = new();

	[Property, Range( 0f, 10f ), Title( "Fade Out Duration" )]
	public float FadeOutDuration { get; set; } = 0.1f;

	internal SoundEvent CreatePlaybackEvent( SoundEventResource sound, SoundEventTrack track )
	{
		ArgumentNullException.ThrowIfNull( sound );
		ArgumentNullException.ThrowIfNull( track );
#pragma warning disable CS0618
		return new SoundEvent
		{
			UI = UI,
			Volume = sound.Volume,
			Pitch = sound.Pitch,
			Decibels = Decibels,
			SelectionMode = SoundEvent.SoundSelectionMode.Random,
			Sounds = new List<SoundFile> { track.Sound },
			OcclusionEnabled = OcclusionEnabled,
			ReverbEnabled = ReverbEnabled,
			AirAbsorption = AirAbsorption,
			DistanceAttenuation = DistanceAttenuation,
			Distance = Distance,
			Falloff = Falloff,
			DefaultMixer = DefaultMixer
		};
#pragma warning restore CS0618
	}
}

public sealed class AudioTrack
{
	[Property, Hide, Range( 72f, 320f )]
	public float TimelineHeight { get; set; } = 72f;

	[Property]
	public string Name { get; set; }

	[Property, Range( 0f, 4f )]
	public float Volume { get; set; } = 1f;

	[Property, Range( 0.01f, 4f )]
	public float Pitch { get; set; } = 1f;

	[Property, InlineEditor]
	public List<AudioParameterCondition> Conditions { get; set; } = new();

	[Property, InlineEditor]
	public List<AudioInstrument> Instruments { get; set; } = new();

	[Property, InlineEditor]
	public List<AudioAutomation> Automations { get; set; } = new();
}

public sealed class AudioInstrument
{
	[Property, Title( "Sound Event" )]
	public SoundEventResource Sound { get; set; }

	[Property, Group( "Debug" ), Order( 1000 ), Range( 0f, 300f ), Title( "Start Time" ), Description( "Timeline start time in seconds." )]
	public float StartTime { get; set; }

	[Property, Group( "Debug" ), Order( 1000 ), Range( 0f, 300f ), Title( "Trim Start" ), Description( "Source time in seconds where playback begins." )]
	public float TrimStart { get; set; }

	[Property, Group( "Debug" ), Order( 1000 ), Range( 0f, 300f ), Title( "Trim End" ), Description( "Source time in seconds where playback stops. Zero uses the end of the selected sound." )]
	public float TrimEnd { get; set; }

	[Property, Group( "Debug" ), Order( 1000 ), Range( 0f, 300f ), Title( "Fade In" ), Description( "Seconds used to fade the instrument from silence to its authored volume." )]
	public float FadeInDuration { get; set; }

	[Property, Group( "Debug" ), Order( 1000 ), Range( 0f, 1f ), Title( "Fade In Curve" ), Description( "Normalized gain of the fade-in curve's middle control point." )]
	public float FadeInCurveMidpoint { get; set; } = 0.5f;

	[Property, Group( "Debug" ), Order( 1000 ), Range( 0f, 300f ), Title( "Fade Out" ), Description( "Seconds used to fade the instrument from its authored volume to silence." )]
	public float FadeOutDuration { get; set; }

	[Property, Group( "Debug" ), Order( 1000 ), Range( 0f, 1f ), Title( "Fade Out Curve" ), Description( "Normalized gain of the fade-out curve's middle control point." )]
	public float FadeOutCurveMidpoint { get; set; } = 0.5f;

	[Property, InlineEditor]
	public List<AudioParameterCondition> Conditions { get; set; } = new();

	[Property, InlineEditor]
	public List<AudioAutomation> Automations { get; set; } = new();
}

[AssetType( Name = "Sound Event", Extension = "sndevt", Category = "Audio", Flags = AssetTypeFlags.NoEmbedding )]
public sealed class SoundEventResource : GameResource
{
	[Property]
	public RangedFloat Volume { get; set; } = new( 1f );

	[Property]
	public RangedFloat Pitch { get; set; } = new( 1f );

	[Property]
	public SoundTrackSelectionMode SelectionMode { get; set; } = SoundTrackSelectionMode.Random;

	[Property, InlineEditor, Title( "Tracks" )]
	public List<SoundEventTrack> Tracks { get; set; } = new();

	private SoundEventTrack _lastSelectedTrack;

	internal SoundEventTrack SelectTrack( IReadOnlyList<SoundEventTrack> eligibleTracks )
	{
		ArgumentNullException.ThrowIfNull( eligibleTracks );
		if ( eligibleTracks.Count == 0 )
			throw new ArgumentException( "At least one eligible sound track is required.", nameof( eligibleTracks ) );

		SoundEventTrack selected;
		if ( SelectionMode == SoundTrackSelectionMode.Random || eligibleTracks.Count == 1 )
		{
			selected = eligibleTracks[Random.Shared.Next( eligibleTracks.Count )];
		}
		else
		{
			var candidates = eligibleTracks
				.Where( track => !ReferenceEquals( track, _lastSelectedTrack ) )
				.ToArray();
			selected = candidates.Length > 0
				? candidates[Random.Shared.Next( candidates.Length )]
				: eligibleTracks[Random.Shared.Next( eligibleTracks.Count )];
		}

		_lastSelectedTrack = selected;
		return selected;
	}
}

public enum SoundTrackSelectionMode
{
	Random,
	RandomExclusive
}

public sealed class SoundEventTrack
{
	[Property]
	public SoundFile Sound { get; set; }

	[Property, Range( 0f, 300f ), Title( "Offset" ), Description( "Source time in seconds where this track begins playback." )]
	public float Offset { get; set; }

	[Property, Range( 0f, 300f ), Title( "Length" ), Description( "Maximum playback length in seconds. Zero plays to the end of the clip." )]
	public float Length { get; set; }

	[Property, InlineEditor]
	public List<AudioParameterCondition> Conditions { get; set; } = new();
}

public enum AudioParameterType
{
	Continuous,
	Discrete,
	Labeled
}

[AssetType( Name = "Audio Parameter", Extension = "audparam", Category = "Audio", Flags = AssetTypeFlags.NoEmbedding )]
public sealed class AudioParameter : GameResource
{
	[Property]
	public string Name { get; set; }

	[Property]
	public AudioParameterType Type { get; set; }

	[Property, HideIf( nameof( Type ), AudioParameterType.Labeled )]
	public float Minimum { get; set; }

	[Property, HideIf( nameof( Type ), AudioParameterType.Labeled )]
	public float Maximum { get; set; } = 1f;

	[Property, HideIf( nameof( Type ), AudioParameterType.Labeled )]
	public float DefaultValue { get; set; }

	[Property, ShowIf( nameof( Type ), AudioParameterType.Labeled ), AudioParameterLabel]
	public string DefaultLabel { get; set; }

	[Property, ShowIf( nameof( Type ), AudioParameterType.Labeled )]
	public List<string> Labels { get; set; } = new();
}

/// <summary>Uses the owning or referenced audio parameter's labels as inspector choices.</summary>
[AttributeUsage( AttributeTargets.Property )]
public sealed class AudioParameterLabelAttribute : Attribute
{
	public AudioParameterLabelAttribute( string parameterProperty = null )
	{
		ParameterProperty = parameterProperty;
	}

	public string ParameterProperty { get; }
}

public sealed class AudioParameterCondition
{
	[Property]
	public AudioParameter Parameter { get; set; }

	[Property, Title( "Invert" ), Description( "Match when the configured label or numeric range does not match." )]
	public bool Invert { get; set; }

	public bool IsLabeled => Parameter?.Type == AudioParameterType.Labeled;

	[Property, ShowIf( nameof( IsLabeled ), false )]
	public float Minimum { get; set; }

	[Property, ShowIf( nameof( IsLabeled ), false )]
	public float Maximum { get; set; } = 1f;

	[Property, ShowIf( nameof( IsLabeled ), true ), AudioParameterLabel( nameof( Parameter ) )]
	public string Label { get; set; }
}

public enum AudioAutomationTarget
{
	Volume,
	Pitch
}

public sealed class AudioAutomation
{
	[Property]
	public AudioParameter Parameter { get; set; }

	[Property]
	public AudioAutomationTarget Target { get; set; }

	[Property, InlineEditor]
	public List<AudioAutomationPoint> Points { get; set; } = new();

	internal float Evaluate( float input )
	{
		if ( Points.Count == 0 )
			return 1f;
		if ( input <= Points[0].Input )
			return Points[0].Output;

		for ( var i = 1; i < Points.Count; i++ )
		{
			var next = Points[i];
			if ( input > next.Input )
				continue;

			var previous = Points[i - 1];
			var span = next.Input - previous.Input;
			if ( span <= 0f )
				return next.Output;
			var fraction = (input - previous.Input) / span;
			return previous.Output + (next.Output - previous.Output) * fraction;
		}

		return Points[^1].Output;
	}
}

public sealed class AudioAutomationPoint
{
	[Property]
	public float Input { get; set; }

	[Property]
	public float Output { get; set; } = 1f;
}
