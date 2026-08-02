# Smod audio guide

Smod is a standalone s&box audio library. It provides FMOD-shaped authored resources,
runtime event instances, a Sound Point component, a timeline editor, and
read-only MCP inspection and validation tools over s&box `SoundEvent` playback.
This is the detailed guide for agents working on the library or on the audio
assets that consume it.

## Start here

Use the smallest workflow that matches the job:

1. Find or create authored resources in the consumer project. Smod does not require a fixed asset-root layout.
2. Open `.audevt` resources in the custom Audio Event editor and author the
   source, tracks, parameters, and timeline there.
3. Validate the event with the `audio.validate_events` MCP tool when the editor
   is available. Use `audio.inspect_event` for one event and
   `audio.detect_sound_slices` when preparing variations from a long source.
4. Wire playback through `SoundPointComponent` for a scene-authored sound, or
   through `AudioSystem` for gameplay-triggered playback.
5. If code refers to an event, use an authored `AudioEvent` property or load it
   through `AudioSystem.GetEvent(...)`; keep any generated lookup code in the consumer project.

The runtime library is in `Code/smod.csproj`. The focused test source is
`Tests/AudioEventTests.cs`; the test project is supplied by the host or consumer.

## Resource model

Keep the three authored resource types separate.

| Resource | Extension | Owns |
| --- | --- | --- |
| `AudioEvent` | `.audevt` | Shared playback settings, master track, authored tracks and instruments, parameters, automation, timeline markers/regions/loops, and event fade-out. |
| `SoundEventResource` | `.sndevt` | One source-variation set: ranged volume and pitch, `Random` or `RandomExclusive` selection, and the source track list. |
| `AudioParameter` | `.audparam` | A named continuous, discrete, or labeled control with its range/default or label set. |

An `AudioInstrument` references one `SoundEventResource`. Each
`SoundEventTrack` then points at a `SoundFile` and can define a source `Offset`,
`Length`, and condition. The event copies its shared playback settings into
the native s&box `SoundEvent` for each voice. Do not add a second nested native
sound playlist to `SoundEventResource`; its `Tracks` list is the sole source
variation list.

### Event tracks and instruments

`AudioEvent.MasterTrack` is required and gates the whole event. Additional
`AudioTrack` objects run concurrently when their conditions match. A track can
contain multiple instruments, each with its own condition, start time, trim
range, fade-in/out, and automation.

Conditions on a collection are combined with AND semantics. Conditions can be
placed on the master track, a regular track, an instrument, or a source track.
Source-track conditions filter the eligible variations before the selection
mode chooses one. A parameter change during active playback rebuilds the
eligibility/gain plan; unaffected voices are kept when possible.

Track and instrument automation can target `Volume` or `Pitch`. Automation is
numeric only, uses at least two strictly increasing points, and is evaluated
as a multiplier. Labeled parameters may select content but cannot drive
numeric automation.

`AudioInstrument.StartTime`, `TrimStart`, and `TrimEnd` are in seconds. The
source track's `Offset` is added to the instrument trim start. A zero trim end
or source-track length means "use the available source end." Fade durations
are also in seconds.

### Parameters

- `Continuous` parameters accept any finite value inside `Minimum` and
  `Maximum`.
- `Discrete` parameters use whole-number bounds, defaults, conditions, and
  runtime values.
- `Labeled` parameters use a declared `DefaultLabel` and lower-case labels.

Parameter names are stable runtime identifiers. Gameplay sets them by name,
so renaming one requires updating every caller and every authored reference.
Conditions and automation must reference an `AudioParameter` resource declared
in the event's `Parameters` collection; matching names on a separate resource
are not enough.

## Authoring workflow

Smod does not require a particular asset-root layout. Consumer projects may
organize `.audevt`, `.audparam`, `.sndevt`, and source audio in whatever stable
folders match their content pipeline; keep authored resource paths stable once
runtime code or scene data refers to them.

Open an `.audevt` in the Audio Event editor. The important editor surfaces are
the Timeline, Sound Files, Inspector, Parameters, and Validation docks. The
toolbar supports adding tracks and parameters, creating a parameter resource,
adding destination markers, transition markers, transition regions, and loop
regions, plus playback, seeking, repeat, and track audition mute/solo.

Use the Sound Files dock to add a source at the playhead or to add a sliced
multi-instrument. Use the Inspector to edit a selected event, track,
instrument, source variation, parameter, or timeline control. Save authored
changes after validation is clean.

The editor's validation panel is backed by `AudioEventValidator`. It should be
treated as the authoring gate, not as optional diagnostics.

## Runtime playback

For a one-shot or gameplay-owned sound, the normal sequence is:

```csharp
using Smod;

void PlayImpact( AudioEvent audioEvent, Vector3 position )
{
    if ( Application.IsDedicatedServer )
        return;

    var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
    instance.Attributes3D = new Audio3DAttributes( position );
    instance.Start();
    instance.Release();
}
```

`AudioSystem.GetEvent( AudioEvent )` validates immediately. The string overload
loads an `AudioEvent` resource, and `RequireEventResource` throws when the path
is missing. Use a resource property when the object is already authored; use a
stable path or generated accessor when code owns the lookup.

Set parameters before `Start` when choosing the initial content:

```csharp
var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
instance.SetParameterByNameWithLabel( "Action", "critical" );
instance.SetParameterByName( "Intensity", 0.8f );
instance.Attributes3D = new Audio3DAttributes( position );
instance.Start();
instance.Release();
```

Use `SetParameterByName` for continuous/discrete parameters and
`SetParameterByNameWithLabel` for labeled parameters. Invalid names, labels,
ranges, or discrete fractions throw; do not hide those errors with broad
fallbacks.

For an instance that remains under gameplay control, keep the reference and
do not release it until control is complete:

```csharp
var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
instance.Attributes3D = new Audio3DAttributes( position );
instance.Start();

// Later:
instance.Stop( AudioStopMode.AllowFadeout );
instance.Release();
```

`Stop( Immediate )` cancels pending starts and stops voices immediately.
`Stop( AllowFadeout )` uses the event's `FadeOutDuration`. `Start` on a
non-stopped instance first stops the old playback and starts the event again.
`Release` makes the instance invalid to the caller; it does not replace
`Stop`, and already scheduled fire-and-forget work may still finish. After
release, do not read or mutate the instance.

`Attributes3D` updates the position of all active voices and switches playback
to positional mode. Without it, playback is local/listener-oriented. The
attribute vectors must be finite, and forward/up cannot be zero vectors.
`Volume`, `Pitch`, `Paused`, `TimelinePosition`,
`PlaybackState`, `TimelineDebugSnapshot`, and
`InstrumentPlaybackStates` are available for advanced control or inspection.

## Sound Point workflow

Use `SoundPointComponent` when the sound belongs to a GameObject and should be
configured in the scene:

1. Add `Sound Point` under the Audio group.
2. Assign its `Audio Event` property.
3. Leave `Play On Start` enabled for an ambient or persistent point, or disable
   it and call the component's `Play` button/method from gameplay.
4. Edit the generated Event Parameters rows for numeric or labeled values.

The component synchronizes its parameter rows from the event, preserves
compatible values by parameter name, follows the GameObject world position,
and applies changed parameters live. Changing the event while playing replaces
the instance and resumes when active. Disabling the component stops with the
event fade-out and resumes when re-enabled; destroying it stops immediately.
`Play` requires an assigned event and fails loudly if it is missing.

## Event lookup in consumer projects

Smod does not include a project-specific event source generator or a generated
`AudioEvents` class. Keep event lookup in the consumer integration layer.
Use an authored `AudioEvent` property when possible, or load a resource by
the consumer's stable path:

```csharp
var instance = AudioSystem.GetEvent( "audio/events/player/roll.audevt" ).CreateInstance();
```

If a consumer project generates strongly typed accessors, those accessors and
generated files belong to that consumer project. Do not add them to Smod or
edit generated output by hand.

## Timeline control

Timeline positions use `TimelineTime` seconds and are converted to the event's
`TimelineSampleRate` (normally 48 kHz). Timeline controls are authored through
the event's:

- `DestinationMarkers`, which provide named jump destinations.
- `TransitionMarkers`, which jump when crossed and can be conditioned.
- `TransitionRegions`, which choose a destination while the playhead is in a
  region and can execute immediately, on the next beat, or on the next bar.
- `LoopRegions`, which loop a range with `Fixed` or `ContentEnd` behavior.

Beat/bar quantization requires a valid `AudioTempoMap`. Loop regions must be
strictly ordered and must not overlap or nest. Destination IDs must be unique,
non-empty, and referenced by existing markers. Keep
`MaximumTimelineJumpsPerUpdate` high enough for the authored control graph but
finite; exceeding it produces a warning and a timeline debug flag.

## Validation and failure diagnosis

`AudioEventValidator.Validate( audioEvent )` returns an
`AudioEventValidationResult`. Call `AssertValid()` when invalid authored data
must stop execution. A valid event requires, at minimum:

- One master track and at least one instrument.
- Non-null collections and unique, trimmed track names.
- Sound resources with at least one non-null sound track.
- Declared parameters with valid ranges/defaults or valid lower-case labels.
- Conditions and automation bound to parameters declared by the event.
- Ordered automation points and valid gain, trim, fade, and source ranges.
- Valid timeline references, ranges, IDs, quantization prerequisites, and
  non-overlapping loops.

When an event is silent, check the following in order:

1. The runtime is not a dedicated server.
2. The event passes validation and has a valid source `SoundFile`.
3. The master, track, instrument, and source conditions match the current
   parameter values.
4. Parameter names and labels match the authored resources exactly enough for
   the runtime API, with stable lower-case labels.
5. The instance was not released before it needed further control.
6. A delayed instrument has reached its `StartTime` and its trim range is not
   already exhausted.

Use `audio.inspect_event` to inspect the derived duration, parameter bindings,
track/instrument plan, source files, timeline controls, and validation issues.
Use `audio.validate_events` for a project-wide report. Both are read-only.
Use `audio.detect_sound_slices` before creating variations from a file with
silence-separated clips; it reports offsets and lengths without authoring the
event for you.

## Working boundaries

- Keep Smod reusable. Do not add player facades, gameplay archetypes, emitter
  schemas, or a second bespoke audio model to the library.
- Keep authored parameter, track, event, and label names stable because gameplay
  and scene properties refer to them.
- Put shared native playback settings on `AudioEvent`; put source variation
  behavior on `SoundEventResource`.
- Prefer one meaningful parameterized event over many near-duplicate cue
  resources.
- Add validation and focused executable tests for new serialized capabilities.
- For runtime or API changes, run the focused audio test command from
  `Libraries/Audio/Agent.md`. For resource-only authoring, use the editor and
  MCP validation workflow rather than adding resource-inspection tests.
