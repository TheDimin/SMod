namespace Smod;

[AssetType( Name = "Audio Event", Extension = "audevt", Category = "Audio", Flags = AssetTypeFlags.NoEmbedding )]
public sealed partial class AudioEvent : GameResource
{
 [Property, InlineEditor] public List<AudioSourceDefinition> Sources { get; set; } = new();
 internal AudioSourceDefinition Source(AudioInstrument instrument) => AudioSourceRuntime.Resolve(this,instrument);
	[Property, Title( "Sustain Until Stopped" ), Description( "Keep this retained process event active without playing voices. Its owner must stop it before release, including on cancellation or scene cleanup." )]
	public bool SustainUntilStopped { get; set; }

	[Property, Title("Shared Music Output"), Description("Use one sample clock for continuous backing, short timeline clips, and action overlays. Requires one grid from zero and a full-song loop. Enable only after validating the shared event path.")]
	public bool SharedMusicOutput { get; set; }

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

	internal SoundEvent CreatePlaybackEvent( AudioSourceDefinition sound, SoundEventTrack track )
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
	[Property, Hide] public Guid Id { get; set; } = Guid.NewGuid();

 [Property] public AudioTransitionRole TransitionRole {get;set;}
	[Property, InlineEditor] public AudioTone Tone { get; set; }
    [Property, InlineEditor] public AudioOverlayRule Overlay {get;set;}
    [Property] public bool MusicLead { get; set; }
    [Property] public List<float> MusicVariationGains { get; set; } = new();
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
 [Property, InlineEditor, Group("Effects"), Description("Optional echo for local, non-shared sound playback. Settings apply to new voices.")]
 public AudioEchoEffect Echo {get;set;}
 [Property, InlineEditor, Description("Parent control for a continuous child music track. Changes gain on complete phrases without moving or restarting its source.")]
 public AudioMusicLayerControl LayerControl { get; set; }
	[Property, Hide] public Guid Id { get; set; } = Guid.NewGuid();

	[Property, Title( "Name" )] public string Name { get; set; }
	[Property, InlineEditor, Title( "Sub-graph Choices" ), Description( "Use child Audio Events instead of a Sound Event. One eligible child is retained until this instrument ends or its choice becomes ineligible. Matching parent parameters are inherited while active." )]
	public List<AudioSubEventChoice> SubEvents { get; set; } = new();
 [Property, Description("Keep the active child source position across parent loop and phase jumps. Song selection still follows parent conditions.")]
 public bool RetainSubEventPositionOnTimelineJump { get; set; }
 [Property, Range(0,10), Description("Retain the outgoing child for this fade when no child choice is eligible, such as a parent receipt cue. Zero stops immediately.")]
 public float SubEventExitFadeSeconds { get; set; }
	[Property, Title( "Sub-graph Selection" )]
	public SoundTrackSelectionMode SubEventSelection { get; set; } = SoundTrackSelectionMode.RandomExclusive;
	[Property, Title( "Sub-graph Change Boundary" ), Description( "When the selected child becomes ineligible, switch on this boundary of its own beat grid. Initial playback starts immediately. A missing grid switches immediately." )]
	public TransitionQuantization SubEventQuantization { get; set; } = TransitionQuantization.Immediate;
	[Property, Title("Loop Sub-graph Selection"), Description("The parent repeats this instrument at each full song end and selects another eligible child. A single eligible child repeats. Children cannot select siblings or change the parent timeline.")] public bool RotateSubEventsAtEnd { get; set; }
 [Property, Range(0,16)] public float SubEventTransitionBeats {get;set;}
	[System.Text.Json.Serialization.JsonIgnore] public bool IsSubEvent => SubEvents?.Count > 0;

    [Property] public List<int> OverlayBars {get;set;} = new();
    [Property, Range(0,300), Description("Shared phrase body length in seconds. Zero reserves the full trim. A shorter body permits one following phrase while the retained source tail finishes. Trim End still includes the tail. Requires a shared-output overlay.")]
    public float OverlayBodySeconds {get;set;}
    [Property, Description("Allow only the recorded release tail to cross this composition loop. The complete body must fit before the loop end. Requires a positive verified body duration; does not permit cross-graph commands.")]
    public bool AllowReleaseTailAcrossLoop {get;set;}
    [Property, Title("Compound Hits")] public List<AudioCompoundHit> CompoundHits {get;set;} = new();

    [Property, Range(0,64), Title("Shared Music Wait (Beats)"), Description("Maximum shared-clock wait from request to phrase entry. Zero allows any future entry. Source loading uses this same budget; paused music does not consume it.")]
    public int MaximumMusicWaitBeats {get;set;}


	[Property, InlineEditor, Title( "Start Trigger Conditions" ), Description( "Play this sound once when all conditions become true. Start Time is a delay from that trigger. A false condition rearms it; the current passage keeps its tail. Requires a non-shared parent and an explicit trim end." )]
	public List<AudioParameterCondition> StartTriggerConditions { get; set; } = new();
 [Property, Description("Optional delay after a start trigger, in seconds. When set, Start Time is only its authored timeline position. Null preserves the older Start Time-as-delay behavior.")]
 public float? TriggerDelaySeconds { get; set; }
	[Property, InlineEditor, Title( "Stop Trigger Conditions" ), Description( "Optional conditions that cancel the triggered passage without marking it completed. Use after a cancellation fade ends. A new start-condition entry rearms it." )]
	public List<AudioParameterCondition> StopTriggerConditions { get; set; } = new();

 [Property] public Guid SourceId { get; set; }

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
	[Property, Hide] public Guid Id { get; set; } = Guid.NewGuid();

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
	[Property, Hide] public Guid Id { get; set; } = Guid.NewGuid();

	[Property]
	public float Input { get; set; }

	[Property]
	public float Output { get; set; } = 1f;
}
