# Smod audio guide

Smod is a standalone s&box audio library. It provides FMOD-shaped authored resources,
runtime event instances, a Sound Point component, a timeline editor, and
read-only MCP inspection and validation tools over s&box `SoundEvent` playback.
This is the detailed guide for agents working on the library or on the audio
assets that consume it.

## Optional editor data

Inspecting a graph must not create optional features. The native inline object inspector can instantiate null class values. Use the existing `AudioOptionalControlWidget` for optional reference-type controls; creation must require the author to select Add. This covers playlists, tone, overlay rules, and layer controls. Extend `check_audio_inspector_nulls` when adding another optional field. Verify that an absent value stays null, existing values remain unchanged, and graph serialization is unchanged after inspection.

Playback-plan inspection must not advance source variation history. A plan without a playback selector makes a temporary source choice only. Actual playback supplies its selection owner. Source variation history now belongs to `AudioSourceSelectionHistory`, keyed weakly by source definition identity. This preserves repeat avoidance across successive one-shot instances without storing playback state on the resource or retaining unloaded definitions. Retained instruments still keep their selected variation until retriggered. Exclusive selection scans eligible entries without allocating a candidate array. Preserve the intended repeat-avoidance scope for successive one-shot sounds when changing source storage.

## Graph-owned source data

Gameplay uses `AudioSystem.TryPlay` for optional one-shot audio. It contains failures in creation, configuration, playback, and cleanup, and reports a failed resource once. `AudioPlaybackGuard` contains failures for retained channels and disables the failed channel. Cleanup still runs after a failure. Graph authoring and validation APIs remain strict so authors can find invalid content.

Explicit live audition refresh applies source volume and pitch changes to retained ordinary voices without restarting their source position. Ranged values keep their selected position instead of rolling a new value on each edit. Shared music still requires fixed unit pitch.

Store source variation data in `AudioEvent.Sources`. Each `AudioInstrument.SourceId` addresses one source in that same graph. Multiple clips may share a source entry. A sub-graph remains a separate AudioEvent resource and cannot use a parent's source ID. Source IDs must be unique among all element IDs in their graph.

The source definition is plain data, not a GameResource. It contains volume and pitch ranges, selection mode, and conditioned SoundFile variations with offsets and lengths. There is no `.sndevt` compatibility path. Do not create a temporary resource to adapt source data for playback.

Validation prepares graph-local source indexes. Voice creation retains the selected source and variation; continuous gain updates use that retained data. Selection history belongs to a separate runtime owner with weak source identity keys. Structural source-reference changes require validation and a new playback instance; supported gain audition edits remain live.

Source authoring uses the graph's undo and save transaction. Get Path identifies a source by graph path and source GUID. Project migration status and measured performance belong in the active project's `Design/Audio`; this schema description is not evidence that a project's conversion or benchmark passed.

## Graph input contract

An audio event is a reusable graph. The same resource can run alone or as a child. When used as a child, every declared input must exist on its direct parent, with a compatible type and full value range. This also applies to an exit graph and every edge of a nested graph. Missing inputs are validation errors; the child must not silently use defaults instead of inherited world state.

Current binding matches names without case sensitivity. Each child instance resolves its parent input references once when created. Subsequent updates copy changed values through those stored references. Connections and current values belong to the instance; resource definitions are not changed. Changes to input declarations require a new instance. Gain and other supported audition controls remain live.

Retained shared music voices evaluate current conditions and mix values directly through `EvaluateSelectedVoice`. Gain updates must not rebuild a whole graph plan or reselect their sources. The returned mix is a readonly value struct; continuous slot arrays and phrase ownership remain separate.

This is one prepared connection table, not a compiled runtime graph. Parameter resource migration, complete immutable playback arrays, build compilation, and before/after performance measurements remain separate work. Do not convert all authoring classes to structs or claim a speed gain without measurement.

## Parent music flow and fixed source controls

Use `AudioInstrument.LayerControl` on the retained parent to control a direct shared-output child. `SubEventInstrumentId` identifies the parent's selector. `ChildEvent` plus `ChildTrackId` identifies the source role; IDs can be copied between different event files, so do not assume a track GUID is globally unique.

`SustainLayer` changes a continuous track's gain without seeking or restarting it. A rising trigger counter starts a held level. Extra requests coalesce into one pending extension at a complete phrase boundary. `PhraseBeats`, `MaximumPhrases`, and `RestBeats` use the active child's grid. The first hold includes at least one complete phrase, plus any partial phrase before the next boundary. Attack and release are seconds. Idle and active gain are multipliers; 2 doubles the target track gain. Use measured peak margin when tuning them.

Live parent phrase controls include the latest request decision in their reason text. Inspect that decision when a counter changes without a voice: conditions, rest, group occupancy, chain limits, and missing timely compatible entries can reject it. The last decision is bounded per bank; it is not an event queue or a session log.

`TriggerPhraseBank` sends parent requests to a child overlay with `ParentControlled=true`. The child bank keeps source ranges, compatible entry bars, chain limit, and recorded release tails. Its local trigger and activity counters do not start it. The controller's sustain-only settings do not change the bank's rules. Gameplay still sends world facts only.

A control's Start Time and positive Trim End define its parent timeline window (duration in seconds for this control type). Its conditions must also match. Build and service loops can therefore own separate control lanes. Live diagnostics show the target voice gain and the reason for activity; they do not move authored blocks or measure acoustic loudness.

Control track volume, volume automation, and editor audition mute also affect the child target. This applies to continuous layers and triggered phrase banks. Parent master gain is applied once through the child instance, not again through a control placed on the master track. These controls change playback gain; they do not edit the child resource.

For the parent flow, `RetainSubEventPositionOnTimelineJump` keeps a song at its current source position across parent loops/jumps. Song eligibility and rotation still belong to its parent selector. `SubEventExitFadeSeconds` retains the outgoing song when no choice remains, for example while a direct parent cue plays. A child cannot jump to a parent or sibling marker.

For triggered parent cues, `TriggerDelaySeconds=0` gives immediate entry after the trigger even when the cue is displayed later on the timeline. Null retains the older behavior, where Start Time is the trigger delay. Native completion still follows source playback time, including pitch changes.

These rules and source references are authored data. Sustained control prepares an immutable rule when it binds to a child; it does not compose PCM or rewrite the timeline. This is not yet evidence of a separate build-time playback-plan compiler or a packaged-build performance result.

Use `check_audio_scenario` for parent control/phase checks. The single shared-event PCM check covers a child decoder and its local overlays; it does not prove the parent action route. `check_audio_timeline_zoom` tests block placement under simulated live start times and restores the view. Keep that UI evidence separate from actual gameplay and listening.

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

The runtime library is in `Code/smod.csproj`. The focused tests are in `Tests/AudioEventTests.cs` and `Tests/AudioEventBeatTests.cs`.
Use `Tests/Smod.Tests.csproj` or the consumer test host with the `AudioEventTests` filter.

## Project source-file rules

Read the active project's `Design/Audio/README.md` for source, capture, processing, and licensing constraints. These are project rules, not Smod restrictions.

## Build audio compression

Choose the audio budget and import settings from the active project's `Design/Audio`. Native compression changes compiled output without rewriting original source bytes. Verify the current MCP schema before changing import metadata.

Keep stereo, sample rate, loop, trim, gain, and normalization settings unchanged.
After compilation, run `audio_build.inspect_audio_build_size` for a fresh
publisher manifest. Total all packaged `.vsnd_c` sizes, verify source hashes,
and check loaded music and critical cues. A metadata change alone does not prove
the build became smaller. Refresh the publisher file list to discard its old
size calculation. Compression changes require listening checks, especially for
short transients and synchronized stem ranges. Use
`audio_compression.check_compressed_sound_timing` with the compiled asset path
and original source WAV path to check decoded frame count and delay in
signal-bearing windows. The check writes no audio. A null window delay means
silence could not provide a timing measurement. A passing result covers the
measured windows; it does not prove listening quality or native start timing.

## Resource model

Keep the three authored resource types separate.

| Resource | Extension | Owns |
| --- | --- | --- |
| `AudioEvent` | `.audevt` | Shared playback settings, master track, authored tracks and instruments, parameters, automation, timeline markers/regions/loops, and event fade-out. |
| `AudioSourceDefinition` | Stored in `.audevt` | Plain source-variation data: permanent local ID, ranged volume and pitch, selection mode, and source tracks. |
| `AudioParameter` | `.audparam` | A named continuous, discrete, or labeled control with its range/default or label set. |

An `AudioInstrument.SourceId` references one local entry in `AudioEvent.Sources`, or the instrument selects a child
`AudioEvent` from `SubEvents`. See **Sub-graphs** below. Each
`SoundEventTrack` then points at a `SoundFile` and can define a source `Offset`,
`Length`, and condition. The event copies its shared playback settings into
the native s&box `SoundEvent` for each voice. Do not add a second nested native
sound playlist to `AudioSourceDefinition`; its `Tracks` list is the sole source
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

### Sub-graphs

Use a sub-graph instrument to keep a large event small. The parent timeline shows
one block. Its `SubEvents` list contains one or more child `.audevt` resources.
Each child is a complete event with its own tracks, conditions, automation,
parameters, beat grid, loops, and phrase banks. Author either `Sound` or
`SubEvents` on an instrument. Use `Name` for a clear block label.

Right-click a timeline track and choose **Add Sub-graph Instrument**. Select one
or more existing Audio Events. The inspector edits choice conditions and
`SubEventSelection`. The block menu can add another choice, open a child asset,
or **Open Active Sub-graph**. Use the normal duplicate, move, trim, and fade
controls. An untrimmed child can loop until its parent block is stopped or its
conditions no longer match. Its displayed length uses the child timeline length;
this does not limit a looping child's lifetime.

Choice conditions use the parent's parameters. `RandomExclusive` avoids the last
eligible choice used by this parent instance, including after another phase.
Selection stays fixed while the current choice remains eligible unless `RotateSubEventsAtEnd` is enabled. With this option, the parent selects a different eligible child at the outgoing child's full `TimelineDuration` boundary. If only one choice is eligible, it keeps looping. Gain, pitch, and intensity updates do not select another child. Each parent instance has its own selection history. Leave this option off for process sub-graphs that must keep the same child.

The child receives the current values of matching parent parameter names before
it starts. Later changes, including counter values used as triggers, are passed
while it is active. Declare the parameter resources used by each child. Prefer
the same shared `.audparam` resources. A matching child parameter must accept the
parent's type and full range or label set. Child-only parameters retain their
defaults. A pending future block creates no sound voices and receives current
values when it starts; it does not replay past counter changes.

The child starts at its own beginning when its block becomes eligible. Explicit
parent seeks use the child's local position relative to the block start. Parent
pause, volume, pitch, position, track mute, trim stop, and event stop apply to its
descendants. Each child owns its native sound settings, mixer and tone controls.
Put phrase banks inside a child; parent overlay source slots remain sound-file
instruments. Shared PCM backing preparation applies to each song child, not to
the parent selector. Cycles, more than 64 choices per block, and nesting deeper
than 16 events fail validation.

`SubEventQuantization` selects Immediate, NextBeat, NextBar, or NextPhrase for a
change between choices. The outgoing child's grid supplies the boundary. The
incoming child starts at its own beginning. Pending selection is cancelled or
replaced when world state changes again. Pause holds the wait. A child without a
valid grid changes immediately. This schedules native playback on an editor or
game update at the boundary; it is not sample-accurate mixing or proof of a good
musical transition. Keep song lengths and grids in the children and listen to
phase transitions before release.

Read the active project's `Design/Audio` for event paths, world parameter mappings, and authoring scripts.

For live checks, attach the parent in the existing editor's LIVE selector. The
block shows its selected child and gain layer. Its tooltip reports the maximum
active child voice gain before mixer/spatial attenuation, not measured loudness.
**Open Active Sub-graph** attaches the exact child instance, including when the
same child asset has several active instances. Its normal timeline and parameter
panel show inherited values and active instruments. **Open parent event** returns
to the parent instance. The same navigation works from an authoring preview.

Validate the full graph with `audio.inspect_event`. Use
`audio_subgraphs.check_music_subgraphs` for an isolated muted music lifetime check
and `audio_subgraphs.check_subgraph_editor` for live parent/child attachment.
The latter opens the existing event windows, stops its test instance, and leaves
the parent in authoring mode. Neither tool changes the game scene or writes audio.

### Silent retained process events

Set `SustainUntilStopped=true` when a process event must remain active without
normal sound voices, for example when it owns requests on a shared music
session. This permits an event with no instruments. The default is false, so
existing events keep their normal completion rules.

Retain the instance after `Start()`. Use its parameters for process facts.
Call `Stop()` on completion, cancellation, object removal, or scene cleanup,
then call `Release()`. Release rejects a sustained event that is still starting
or playing. It does not stop the event for you. Stop cancels pending music
requests; active shared-music voices use their bounded release. Pause holds the
process timeline. Restart cancels the old request owner and creates a new one.

This option supplies process lifetime only. It does not select a phrase or bind
the event to a music session. The audio engine must provide that binding.
`audio.inspect_event` reports `sustainUntilStopped` for editor checks.

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

### One event for one process

Use one `.audevt` for a process with start, loop, and finish phases. Keep one
instance for the full process. Gameplay sends a parameter change when the
process must finish; the event owns the sound selection and timeline change.
Use separate events for sounds that have independent playback lifetimes.

1. Put the phase clips on named tracks in one event. Set each instrument's
   timeline start, source trim, fades, and gain. Use graph-owned source definitions
   for slices and random pitch or volume ranges.
2. Declare a discrete `Finish` parameter with range 0–1 and default 0.
3. Add a loop region for the working phase, with the condition `Finish == 0`.
   Use `ContentEnd` when the loop must follow the source playback end.
4. Add a destination marker at the finish section. Add an immediate transition
   region over the start and loop sections, with condition `Finish == 1`,
   pointing to that marker. Put the destination outside the transition region
   so the transition cannot trigger itself again.
5. Start the event once. Retain the instance and call
   `instance.SetParameterByName( "Finish", 1 )` at the required gameplay time.
   Keep it under the consumer's audio owner for playback updates, volume, mute,
   and release. Stop it on cancellation, reset, disable, or destroy.
6. Validate the resource. Check that one instance plays the start, repeats the
   loop until triggered, then plays the finish and stops. Also check an early
   finish trigger, mute, volume, and cancellation. Resource validation alone
   does not prove live timing.

Choose parameter names and transition conditions to match the process. The
`Finish` control above is a complete example, not a required name for all events.
Keep phase timing, fades, and source variation in the resource. Gameplay owns
the time at which it sends a trigger.

### Editor steps

Smod does not require a particular asset-root layout. Consumer projects may
organize `.audevt`, `.audparam`, and source audio in whatever stable
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

Beat/bar/phrase quantization requires a covering beat grid or a valid `AudioTempoMap`. Loop regions must be
strictly ordered and must not overlap or nest. Destination IDs must be unique,
non-empty, and referenced by existing markers. Keep
`MaximumTimelineJumpsPerUpdate` high enough for the authored control graph but
finite; exceeding it produces a warning and a timeline debug flag.

### Music grids and transitions on a beat

For music, define a named `AudioBeatGrid` for each timeline section. Set its
start, end, BPM, beats per bar, and bars per phrase. The start is bar 1, beat 1.
Grids must not overlap. They can have different tempos. The event Inspector
exposes the Beat Grids list. The transition Inspector exposes quantization and
fade lengths; tempo analysis updates the section grid when one covers the region.

Use `grid.TimeAt(bar, beat)` to place controls from musical positions.
Both arguments start at 1; the beat can be fractional. For example, at 100 BPM,
`TimeAt(5)` is the end of a four-bar loop in 4/4 time:

```csharp
var grid = new AudioBeatGrid {
    Name = "Service", Start = new(0), End = new(9.6),
    BeatsPerMinute = 100, BeatsPerBar = 4, BarsPerPhrase = 4
};
audioEvent.BeatGrids.Add(grid);
var phraseEnd = grid.TimeAt(5);
```

1. Put each section's instruments at its grid start. Add a loop over the section
   and a destination marker on its first beat.
2. Add a transition region over the source section. Point it to the destination
   and condition it on the requested music parameter. Select `NextBeat`,
   `NextBar`, or `NextPhrase`. A transition at a loop end takes priority over
   the loop. Global `TempoMap` remains supported when no section grid applies;
   its default phrase is four bars.
3. Set `FadeOutBeats` and `FadeInBeats` when a transition needs fades. The
   source tempo controls fade-out; the destination tempo controls fade-in.
   Zero makes a direct change on the boundary. A late request shortens fade-out
   to the time left before that boundary. These are sequential fades around
   the jump, not overlapping mixes. The user's volume, mute, and pitch remain
   independent.
4. Gameplay sets the requested parameter on the retained event instance. Keep
   the source tracks eligible while the request is pending. A condition that
   removes the source track would stop it before the beat. Poll playback as
   usual so the event processes its timeline and gain envelope. The current
   backend applies these changes on playback updates, with timeline overshoot
   preserved; this is not an audio-thread sample-accurate scheduler.
5. Check the start of each section, repeated loops, a mid-phrase request, a
   request close to the boundary, a cancelled request, pause, mute, and stop.
   Check that exactly the intended voices play after the transition.

Beat alignment does not establish harmonic compatibility. Author entry and exit
points from the source music. Keep estimated beat, chord, or key information
marked as estimated until it has been checked by listening. Use a transition
marker at a specific musical position when a change must follow a particular
chord. Use a region when any next beat, bar, or phrase is suitable.

## Validation and failure diagnosis

`AudioEventValidator.Validate( audioEvent )` returns an
`AudioEventValidationResult`. Call `AssertValid()` when invalid authored data
must stop execution. A valid event requires, at minimum:

- One master track and at least one instrument, except an explicit `SustainUntilStopped` process event.
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
  behavior in graph-owned `AudioSourceDefinition` data.
- Prefer one meaningful parameterized event over many near-duplicate cue
  resources.
- Add validation and focused executable tests for new serialized capabilities.
- For runtime or API changes, run the focused audio test command from
  `Libraries/Audio/Agent.md`. For resource-only authoring, use the editor and
  MCP validation workflow rather than adding resource-inspection tests.

### Audio-owned music selection

Gameplay sends facts such as world phase and workload. It must not choose a song, track, arrangement, variation, or transition time. Do not expose a Song parameter or put playlist logic in gameplay code.

Author `AudioEvent.MusicPlaylist`: declare its world-state `ContextParameter`, section destination markers, eligibility `Conditions`, and preferred `EntryConditions`. Each section starts its own beat grid. The audio engine selects eligible sections, avoids immediate repeats, and holds the current composition until its end. A context change replaces a pending choice. If no section is eligible, the current section remains the fallback. Set `RotateAtEnd=false` for a section that must repeat.

Set `MusicVariationGains` on each track. All gain lists must match `VariationCount`. The engine chooses a different balance each `VariationBars` source bars and blends for `VariationBlendBeats`. Set `MusicLead` on lead tracks to reduce them across the last and first bar of a song change. `TransitionFadeBeats` provides a short click fade at the boundary. Backing and drums retain the beat. Native scheduling runs on playback updates, not the audio thread.

Keep source stems from one composition aligned. Matching BPM and estimated key do not prove that stems from different songs fit together. Verify downbeats, chord changes, endings, and pickups by listening before calling a transition seamless. See the `smod-audio` skill for the authoring and validation workflow.

### Live overlay in the audio event editor

Open the `.audevt` asset during play mode. In its toolbar, change **Preview / authoring** to a **LIVE [instance ID]** entry. Only instances of the open event are listed. The existing timeline playhead follows the selected instance; instrument blocks highlight live playback. A translucent fill rises with voice gain on a -60 to 0 dB scale; silent and paused nodes have no fill, and gain above 1 is yellow. The waveform stays visible beneath the fill. Hover a block for its live state, voice gain, source time, selected source file, and pitch. The existing Parameters panel replaces preview controls with **LIVE** values from gameplay.

The time display shows the current section and pending section. Normal mouse-wheel input zooms the timeline without changing the vertical scroll position. **Ctrl+wheel** scrolls vertically without changing zoom. **Follow** controls timeline scrolling. **Freeze live view** holds the display without pausing game audio. **Copy live report** copies the selected snapshot as JSON for feedback. Select **Preview / authoring** to detach and restore preview controls. Preview transport and seeking cannot control the game-owned instance. Closing the asset editor does not stop the selected live instance.

Voice gain excludes mixer and spatial attenuation; it is not a loudness meter. The overlay observes playback without advancing it. `AudioLiveDiagnostics.CaptureActive()` and `capture_audio_instances` expose detached main-thread snapshots. `open_audio_live_monitor` now opens the asset editor with the overlay attached; there is no separate monitor window or menu.

### Movable music phrases

Shared overlays support QuantizationBeats down to 0.125. SchedulingLeadMilliseconds sets the post-preparation safety margin, 12-1000 ms, default 60. It does not include loading or device latency. Use shorter margins only for brief sounds after native timing checks. Exact chain deadlines remain unchanged.

`CooldownBeats` accepts finite values of at least 0.25 beats. An active phrase still owns its voice-group slot; a short cooldown does not cut its source or force another voice.

Shared-output trigger-only phrase banks can set `MaximumChainPhrases` (1 disables, maximum 8) and `ChainRestBeats` (at least 1). Success counter increases reserve at most the remaining chain capacity. `OverlayBodySeconds` defines the exact following phrase entry; the original source tail remains audible. The next source must permit that destination bar. Preparation that misses this exact sample is rejected instead of moving the continuation to a later beat. At most two phrase voices coexist. Ordinary compound-hit instruments cannot use chaining.

A chain overrides ordinary queue/cooldown controls: it accepts bounded extensions while its phrase is active, then requires the authored rest after the final complete source tail. All overlays in the same voice group observe that rest. Services during rest are discarded. Activity does not extend a chain; use a separate activity rule. Pause freezes the output clock; stop, seek and state withdrawal clear pending work through the normal ownership path. Live voice reasons identify each admitted continuation and its position in the chain.

A track with `Overlay.Enabled=true` is a phrase bank. Keep it false for ordinary timeline tracks; the editor can create an empty inline rule. Its instruments do not play at their authored `StartTime`; that position only arranges the bank in the editor. `TrimStart` and `TrimEnd` select a source phrase. `OverlayBars` lists allowed destination bars, numbered from one within the active beat grid. An empty list permits every bar. Check harmony and phrase endings before using that option.

Gameplay can send a factual counter such as `ServiceCount`. The event rule detects a change in `TriggerParameter`, then chooses a phrase on an allowed bar. `ActivityConditions` can request phrases during sustained activity. `Conditions` restrict the world state. `BeatGridName` can restrict the bank to one backing. The engine also requires the authored BPM to match the active grid. It permits one phrase at a time per `VoiceGroup`, coalesces repeated triggers, applies a beat cooldown, and avoids an immediate repeat when another candidate is available. A drop in activity does not cut the current phrase short. Pause, stop, and the instance volume also control the phrase.

Keep continuous backing stems aligned. Movable phrases are separate source trims with explicit destination rules. Same BPM and estimated key alone do not establish chord compatibility. Timing is driven by playback updates; a missed boundary is skipped. The live editor shows the playing phrase at its actual start position and gives its source time and gain in the tooltip.


For interaction drum sets, use a separate `VoiceGroup`, `QuantizationBeats=1`, and a factual interaction counter. Zero quantization uses a whole bar. Each instrument holds one complete recorded fill, not a random list of hits. Set `QueueWhilePlaying=false` and `IgnoreTriggersDuringCooldown=true` to prevent rapid actions from creating delayed fills. Empty activity conditions do not request playback. Continuous quiet drum patterns can remain on the normal timeline beneath the fills.

### Track tone controls

Select a track in the Audio Event editor and open **Tone** in its Inspector.
**Bass Gain (dB)** controls a low shelf from -18 to +6 dB. Zero is neutral.
**Bass Frequency (Hz)** sets its corner from 40 to 500 Hz. Start at 180 Hz
when a combined chords and bass stem has too much low-frequency energy.
Use a negative gain to reduce the bass without lowering the whole stem.
Positive gain needs output headroom; this filter is not a limiter.

The master track applies its shelf to every voice. A regular track adds its
own shelf. Both normal timeline voices and movable overlays use these settings.
The processor uses the native 44.1 kHz mix rate and separate state for each
channel and listener. Settings settle over approximately 20 ms. Changes do not
seek or restart a voice. A neutral event does not create an effect mixer.
An effect mixer retains the original output parent and ends after native
playback, including a fade-out. Release does not remove the effect early.

Keep tone settings in the event, not in gameplay song selectors or edited
source files. Tone controls affect native event voices; the experimental shared
PCM session does not yet apply them. The node gain fill is still a voice-gain
indicator, not a post-EQ loudness meter. Validate the resource, compare with
zero gain at the same master volume, and listen before approving a setting.


### Reload new resource dependencies

Create parameter resources before the parent event. Store source definitions inside the graph. If native
compilation succeeds but inspection still shows cached default values, use
`audio.reload_saved_audio_resource` on the dependency, then on its parent.
This reload replaces the selected resource's in-memory values with its saved
JSON; use it only when those saved values are intended. It does not save a
scene or write audio. Re-run `audio.inspect_event` and require no validation
issues before using the event.


### Shared-clock phrase entry bars

The shared PCM runtime's `PrepareAndScheduleAsync` reads the instrument's
`OverlayBars` before source loading. Non-empty lists permit entry only at the
start of those one-based bars. The entry must also match beat quantization,
and the complete phrase must fit before the composition end. If no entry
remains in this loop, the scheduler uses the next permitted entry in the next
loop. It does not substitute bar 1, trim the phrase, or enter later within a
permitted bar. An empty list retains unrestricted beat entry for material such
as drums. Invalid or incompatible entries cannot produce a fallback phrase.

These rules protect an authored placement decision. They do not establish
harmonic compatibility or replace listening review. The legacy main-event
overlay path still has its own scheduler; this shared-clock behavior is not
proof that the production event uses that path yet.


### Shared-clock response wait limit

Set an instrument's `MaximumMusicWaitBeats` to limit the time from request to
phrase entry in `PrepareAndScheduleAsync`. Values are 0-64; zero leaves the
wait unlimited. Choose a short limit for action responses. Source loading uses
the same budget. Pause holds the shared clock and therefore holds the deadline.
If no compatible entry meets the deadline, the request returns no schedule.
It does not enter on a wrong bar or restart the wait after decoding. Existing
phrases are unaffected. MCP event inspection includes this limit and entry bars.

This limit applies to shared-clock admission. It does not change the legacy
main-event overlay scheduler or define the duration of an admitted phrase.

### Shared continuous stem mixing (internal runtime)

The shared PCM session can own 1–16 continuous stereo stems. Each stem must
have the same frame count and use 44,100 Hz source data. The runtime snapshots
source ranges and tone settings before loading. It prepares the stems within
its configured memory budget before it starts the native output. A missing,
over-budget, or misaligned stem prevents startup; do not start partial music.

`AudioMusicRuntimeSession.SetBackingGains` accepts one gain per stem. Gains are
0–1 and change smoothly over up to 20 ms. A silent stem keeps the shared source
position. Pause holds both source position and gain ramps. Gain publication is
atomic for a complete stereo block. This control does not select a song.
The audio engine must map authored track automation to these gains; gameplay
must continue to send world facts only. Production event binding remains pending.

`AudioMusicRuntimeSession.StartEventBackingAsync` reads continuous parts from
an event-selected grid. It uses the existing condition and automation evaluator.
Preparation includes inactive parts at zero gain. Only full-grid parts enter
this backing binding; short phrases and overlay banks stay separate. Sources
remain fixed for the composition. `UpdateEventParameters` updates gains from
world facts. A source that becomes ineligible is silent, not replaced.

Shared continuous backing currently requires fixed unit pitch and fixed source
gain. Use authored volume automation for instrument variation. Unsupported pitch,
random source gain, or gain outside 0–1 is rejected. The normal event instance
lifecycle does not yet call this internal binding; do not claim production
shared-clock playback from this API alone.

`AudioMusicEventPlayback` owns the asynchronous shared-backing lifetime for an
event. Start replaces the prior generation. Stop and dispose detach ownership
before loading can complete. World parameter dictionaries are copied. Changes
during loading are applied before output is unmuted. Pause and volume settings
persist through loading. The owner forwards controls to the runtime session;
it does not choose a song or introduce gameplay track parameters.

This owner is not yet called by normal `AudioEventInstance.Start`. The remaining
connection must replace the matching native backing voices, use the shared
sample clock for timeline position, handle phase exits, and preserve editor
track diagnostics. Do not enable it on the production resource before those
paths are implemented and verified.

The event owner uses a bounded 128 MiB preparation budget. The accepted gameplay
backing keeps 54,190,080 bytes of prepared PCM. Its next full stem also needs
native decode output and preparation space; a 64 MiB limit cannot prepare both
parts. The budget bounds managed preparation arrays, not all native decoder
memory. It is separate from packaged audio size. Do not raise it without a
source and temporary-memory calculation.

Use `music_event_backing_check` with `gridName=Service` or `Build` to check the
actual authored continuous parts through the event owner. Output stays muted.
It reports source count, preparation time, memory, clock, pause/resume and cleanup.
It writes no audio files. It needs an active listener for clock checks, and it
does not prove listening quality or production event-instance integration.

### Open the editor and browse events

Use **Tools > Audio Event Editor** in the main editor menu. The **Audio Events** dock lists `.audevt` assets under the current project's Assets folder. Search by event name or path, then double-click a row to open that event. The normal asset editor routing reuses an already open event window. Use the Audio Event editor's **View** menu to reopen the dock if it is closed. Listing events does not load sound samples or scan external source folders.

### Musical handoff between song sub-graphs

Gameplay supplies world facts. The project policy in `Design/Audio` determines which facts affect arrangement, phase eligibility, and full-song rotation. Keep those authoring decisions in the event.

Use the active project's `Design/Audio` for concrete transition settings and accepted rotation behavior.

Set each child track's `TransitionRole`: `Lead` releases over the final transition window; `Backing` releases over its second half; `Rhythm` holds until a short click-prevention ramp at the boundary. On entry, rhythm starts promptly, backing starts quietly then grows over the configured beats, and lead grows over twice that time. Incoming timing uses the new song's BPM. The normal intensity gain remains a multiplier. The live voice gain view includes these envelopes, including overlay voices.

The roles are authored metadata. Mixed pitched/percussion stems use Backing, not Rhythm. This is an arrangement transition without overlapping songs or changing source pitch. It does not invent a ii-V-I or pivot chord from unknown harmony. A true harmonic bridge needs verified chord and source-region metadata. Source files must remain unchanged. See docs/audio-jazz-transitions.md for research and validation.

### MCP authoring workflow

Use the `audio_authoring` toolset instead of direct JSON file writes for normal event edits. These tools operate on the native editor and preserve undo history.

1. `inspect_audio_authoring(resourcePath)` returns the editable serialized state, current revision, disk hash, validation and dirty flag. It opens the event without reloading over unsaved work.
2. `edit_audio_authoring(resourcePath, expectedRevision, editsJson, title, previewOnly)` validates a detached candidate, checks graph cycles, and applies a named undo step. Set previewOnly for a reviewable before/after result. Inspect first for exact field paths. The tool does not save.
3. `undo_audio_authoring(resourcePath, redo)` uses the same editor undo/redo controls and returns a fresh revision.
4. `inspect_audio_graph(resourcePath)` visits each child once and returns the complete event states and revision tokens, dependencies first, plus root graph validation.
5. `save_audio_graph_verified(resourcePath, revisionsJson)` validates the graph and checks every revision before writing. Supply an object mapping each returned event Path to Revision. Children are saved before their parent. Every saved event is loaded into a detached resource and compared with the current editor state. Success requires all comparisons to pass. Partial failures report verified paths and the failed path; multi-file writes are sequential, not an atomic filesystem transaction. This tool saves event resources only, not unrelated sound/parameter edits or audio files.

Patch example (`editsJson` contains this JSON array):

```json
[
  {"op":"replace","path":"/Tracks/0/Volume","value":0.7},
  {"op":"replace","path":"/Tracks/0/TransitionRole","value":"Backing"},
  {"op":"replace","path":"/MasterTrack/Instruments/0/SubEventTransitionBeats","value":4}
]
```

`replace` is the default operation. Array `add` accepts an index or `-` to append. `remove` removes an array entry or property. Maximum 128 operations. The whole patch is prepared off the live resource; invalid paths or invalid resulting events do not partly mutate the live event. Re-inspect after a revision conflict. Never suppress the conflict with an automatic stale retry.

### Reusable scenarios and live reports

`check_audio_scenario(resourcePath, stepsJson)` creates one temporary muted instance, runs at most 32 steps and 20 seconds of total waits, records snapshots, and always stops/releases the instance. Numeric and labeled parameters are supported. Expected song identity is compared with the preceding step. Supported step fields: parameters, seekSeconds, seekToSongEndLeadSeconds, paused, waitSeconds, expectSongUnchanged, expectSongChanged, expectState, expectPaused. The song-end seek uses the active child's grid, so tests do not need a hard-coded song duration. The active game is not changed and no audio is exported.

```json
[
  {"parameters":{"Intensity":0},"expectSongUnchanged":true},
  {"parameters":{"Intensity":1},"expectSongUnchanged":true},
  {"seekToSongEndLeadSeconds":0.2,"expectSongUnchanged":true},
  {"waitSeconds":0.35,"expectSongChanged":true}
]
```

Use separate scenarios for phase change, state reversal, and pause. Check Passed and Cleaned; do not interpret a completed call as a passing check. Parameter or resource errors are failures and still release the temporary instance. Tests and muted scenarios do not establish listening quality.

`inspect_audio_live(instanceId, resourcePath)` filters active instances and includes descendants. It reports current song/section, parameters, beat/bar position, pending wait time, handoff Entry/Exit phase, and per-voice native, event, instance, instrument-envelope, music, and handoff gain factors. Final Gain remains the actual observed voice gain before mixer/spatial effects. Parent sub-event Gain is an aggregate diagnostic, not a measured audio peak or a single native voice. `capture_audio_instances` remains available for a compact inventory. The existing editor live overlay remains the visual interface.

Validation: 190 focused audio tests passed. Native checks verified preview without mutation, undo, stale revision rejection, six-event save/readback, invalid gain and cycle rejection, intensity stability, phase change, song-end rotation, pause, reversal, and temporary-instance cleanup. Evidence is under `.validation/audio-authoring-*`.


### Shared-clock check with song sub-graphs

`music_event_backing_check` accepts `resourcePath` for a song child and
`gridName` for its grid. Use `inspect_audio_graph` to find both. With an empty
path, Service selects Some Trumpet Jazz and Build selects Coffee Time. The
root event no longer contains these grids. These checks prepare an isolated,
muted shared-session candidate; they do not prove that gameplay uses that
backend. Require clock advance, pause/resume and cleanup results separately.
A preparation-budget rejection must be resolved before that song can use the
shared session. Source files remain unchanged.

Use `music_shared_process_check` to test two temporary retained process events
on one muted song session. It reports shared sample boundaries, renderer
admission, independent cancellation, active phrase count and cleanup. It uses
existing source ranges and writes no audio. These results are not an audible
onset measurement or proof of gameplay binding.


## Exact element links (Get Path)

Right-click an item in the Audio Event editor and select **Get Path**. Paste the link with your change request. Clips (including sub-graph instruments), tracks, markers, regions, automation curves, and points use a short resource path plus a permanent GUID:

`smod:audio/kitchen/events/coffee.audevt#5da224c716114ffcb0863dbe16cf91e8`

The prefix is `smod:`. Names, settings, timeline positions, and list indices are not part of the identity. Moving a clip between tracks or sorting automation points keeps the link valid. Native Duplicate and MCP add/copy operations give new elements fresh IDs. Do not edit IDs. Deleting an element removes its target; undo restores its ID. Moving or renaming the event file requires a new resource path. Unsaved new elements still require a normal save to survive discarding or restarting the editor.

Old version-zero events receive repeatable IDs through the resource JSON upgrader. The next normal save writes them into the `.audevt` file. Existing control IDs are preserved. Copying a link does not write audio files or save unrelated edits.

For a pasted link:

1. Discover `resolve_audio_path` through MCP `search_tools`. Pass the complete link as `link`. Use `focus: false` to inspect only, or the default `true` to select and frame the target. Focus does not start audio or move the playback cursor.
2. Use the returned `ResourcePath`, `Pointer`, `Kind`, `Id`, `Element`, `Revision`, and `Dirty` values. These come from current editor state, including unsaved edits. Resolve again before each edit because the current JSON pointer can change while the GUID stays fixed. Child events have their own resource paths.
3. Apply the requested change with `edit_audio_authoring`, using the current pointer and revision. Follow **MCP authoring workflow** for validation, save, and read-back. A link alone is not an instruction to edit.
4. Missing or duplicate IDs fail explicitly. Do not guess by name or position. If the target was deleted, ask which replacement the user means.

The timeline background uses the event path and an optional `#t=12.5` position. Sound-library links use `smod:asset:`, `smod:external:`, or `smod:package:` plus the escaped address. Their resolver only returns that address; it does not import, download, or read external files. These are pasteable editor addresses, not registered operating-system URL shortcuts.

The resolver still accepts old `smod://element/v1?...` links. Old GUID links remain usable. Old index-and-revision links can still become stale after edits; obtain a new Get Path link to use permanent identity.


## Shared music event output (staged)

`AudioEvent.SharedMusicOutput` connects a retained event instance to the shared
PCM output. This path supports continuous stems and short timeline clips in one
song grid from zero, with one unconditional full-song loop. Up to 128 timeline
slots share decoded source ranges. Clip entry, exit, and instrument fades use
sample positions; they do not wait for a frame update. Action overlays also use
this clock. The event selects an eligible phrase from world facts, prepares its
source, and schedules a future beat permitted by its destination bars. One
phrase at a time can reserve each voice group. The current song continues
while the phrase loads; an unready phrase never starts late outside the grid.

Queued activity phrases are cancelled when their activity is withdrawn.
Stopped or replaced event generations cannot admit a late source completion.
Pause holds the backing and phrases together. Audition mute and current mix
values reach active phrase owners. Their gain ramps and authored fades run in
the sample renderer. A rejected preparation waits at least one beat before an
activity retry. Before a project migration, verify its song set, parent handoffs,
renderer cost, and real gameplay calls. Record migration status in `Design/Audio`.

The instance owns loading, copied world parameters, output volume, pause, seek,
audition mute, normal loop position, and authored stop fades. A natural loop
keeps the PCM clock running. An explicit seek moves that clock. Stop the event
before release. Shared output requires local playback, unit pitch, and output
volume from zero to one. Failed startup is available as `SharedMusicError`.

Live source rows report the selected files. Shared gain values currently show
the requested mix gain; they are not a measurement of signal level or the
sample ramp within the current audio block. Do not use them as output metering.

`music_shared_event_check` checks a complete temporary Some Trumpet Jazz graph
through the actual event instance. It changes `InteractionCount`, checks that
an action phrase starts on a beat, and checks source rows, pause, seek, fade,
and cleanup. It captures in memory and silences its temporary mixer. It does
not save assets or change gameplay. This proves the event parameter path for
that graph, not the real interaction call site or listening quality.


### Prepared shared-song changes (staged)

When the current song and all eligible alternatives use shared output, the
parent prepares one next child per instrument during the last phrase. The
replacement stays paused. A ready replacement can enter at a full-song end.
If preparation is late, the current song keeps its full loop and the replacement
waits for the next full-song end. Readiness must leave enough time for the
authored exit fade. Apply the project policy to the choice conditions.

A world-state phase change prepares its selected shared child before it arms
the beat wait. Shared children use their sample clock for that wait. Reversing
world state, removing the instrument, or stopping the parent releases unused
preparation. Do not treat preparation as a guarantee of sample-accurate
cross-song output: activation still runs on an event update. Native transition
capture and project listening review remain separate evidence gates for migration. A mixed list of shared and legacy output choices still uses
the legacy song-end path; keep a shared-song playlist consistent.


Use `music_shared_parent_check` for a short native parent check. The default
checks Some Trumpet Jazz to Coffee Time on a world-phase change. Set
`songEnd=true` to check rotation to Mellow Hello after a temporary near-end
seek. Both modes use complete unsaved copies and silence only their temporary
outputs. Require `LoadingObserved`, `OldRetainedWhileLoading`,
`ReplacementPaused`, `Changed`, `NewClockAdvanced`, `BothHadSignal`,
`MixersRemoved`, zero `ResidentAfterStop`, and `Passed`. The paused check also
requires the replacement sample clock to stay at zero before selection.
This verifies loading and lifetime. It does not measure a cross-song gap or
prove listening quality. The native modes passed with two sessions each;
saved music events still require the remaining production checks.


Parent-to-child control only: the parent sub-graph instrument owns song selection and its repetition. Loop Sub-graph Selection is the editor name for RotateSubEventsAtEnd. A child owns only its local arrangement. It cannot select a sibling, write parent parameters, or move the parent timeline. Go To markers and transition regions must target a destination in the same event, even when the destination GUID exists in another graph. Parent inspection of child playback status is permitted; it is not a child command. Internal backing loops keep sound continuous while the parent waits for a ready replacement. Never use a cross-graph Go To as a loop or song-change mechanism.


## Live preview controls

Select the exact LIVE instance in the Audio Event editor. Use **M** to mute a
track, **S** to solo one or more tracks, and **Clear mute / solo** to restore the
mix. These controls affect only that instance. Open its active child to control
that song's tracks. The editor maps tracks by their permanent IDs.

Live audition does not save the resource, restart voices, seek the song, or
change gameplay parameters. Detach, select another instance, or close the window
to remove the editor's overrides. The previous audition mute state is restored.
Freeze live view freezes the display only. Parameters remain read-only in LIVE
mode; gameplay continues to set them. Use authoring preview for timeline and
parameter experiments.

The temporary controller is compiled into the editor assembly. It updates the
existing instance mute path only when a control changes. It adds no build-time
control polling or alternate runtime mixing path. Active legacy overlays and
shared output both receive the mute update.

Focused coverage: live instance isolation, mute/solo masks, clear, restoration,
release-before-close, shared mix update, and no voice restart. Native window
coverage is part of `check_subgraph_editor`: inspect MuteApplied, SoloApplied,
ClearRestored, and DetachRestored along with parent/child attachment results.


## Music continuity

Use continuous source stems for the backing and rhythm bed when the music pack
has no verified modular loops. Keep their source position on the song clock.
Control intensity through gain. A short fade at each slice cannot restore a
removed note or cymbal decay. Quiet waveform sections are not automatically
valid musical loops.

Gameplay accents need complete phrases with valid entry bars and a natural
ending. Scheduling length and release-tail length can differ; preserve the
onset grid and the tail separately when the runtime supports that contract.
A crossfade requires musically valid overlap. Check consecutive and alternating
phrases in the mix. Keep parent selection and child-local arrangement separate.
See the smod-audio phrase-continuity reference for the full authoring procedure
and FMOD sources.

### One Audio Event editor window

Tools > Audio Event Editor, asset opens, element links, and sub-graph navigation use one shared window. Opening an event changes the timeline in that window. Unsaved edits remain available when you return to an event. Save includes the retained dirty resources, and closing the window asks about all unsaved changes.

Switching events stops the previous editor preview and restores live mute and solo controls. Gameplay playback continues. Old duplicate windows are consolidated when the shared window is opened. If two windows contain different unsaved drafts for the same resource, both remain open until the conflict is resolved; neither draft is discarded automatically.


### Project shared-clock rollout

Project rollout notes belong in the active project's `Design/Audio`, separate from this library contract.

The scenario tool updates its temporary instance during waits, including outside play mode. Use a full preparation phrase before an end-of-song test. A cold source that misses its safe fade window may wait for the next complete song boundary.

Use the project's dated evidence for rollout status. A library guide does not establish current listening approval.


`music_shared_output_check(updateRate: 30|60|120)` checks complete preloaded phrase samples with a slower main-thread observation cadence. It returns the requested rate and measured maximum interval. It does not set game FPS or prove cold-source streaming behavior. Require sample comparisons and mixer/source cleanup, not just a successful tool call.


### Phrase body and retained tail

Shared-output overlay instruments expose `OverlayBodySeconds`. Zero keeps the existing full-trim reservation. A positive value declares the body duration inside the explicit source trim; `TrimEnd` still includes the complete retained decay. It must be finite, at least 0.001 seconds, and no longer than the trim. Validation rejects it on ordinary timeline tracks and non-shared events.

After the body and authored cooldown end, the group may schedule a following phrase on its permitted beat. The first source continues to its trim end. Each group permits at most two active or preparing slots: one body and one retained tail. A third phrase waits or is discarded according to the existing queue rule. Stop, seek, and owner cancellation remove all retained phrases. Mute applies to both.

The scheduler still requires the full source range, including the tail, to fit inside the composition. This does not yet allow tails across song changes. It does not move an entry off its legal bar or guarantee immediate adjacency when preparation starts late. Source and harmony review are required before setting a shorter body; do not label a following note as a decay. The PCM range remains unchanged by the body setting.

The current change has unit coverage for group admission, retained-tail lifetime, the two-slot limit, cleanup, invalid authoring, and exact stereo overlap samples. Native output and live-display checks for overlapping instances of the same clip remain to be completed before enabling this on production phrase banks.


Live voice snapshots include `InstrumentId` and `IsReleaseTail`. Shared overlays report every active or pending instance, including repeated use of one authored clip. The timeline binds these rows by permanent instrument ID instead of array index. For an overlapping clip, its hover tooltip lists each body/tail, state, source position, and gain. The block fill uses the sum of voice gains, not measured output loudness. Other instruments keep their own rows when an overlap starts or ends.


`music_shared_tail_check` validates two uses of one existing source range in a temporary shared song. Its mixer is silenced after capture, and all samples remain in memory. The result reports separate live rows, retained-tail visibility, distinct starts, maximum simultaneous voices, overlapping frame count, exact summed-sample error, and cleanup. Require Passed and zero retained bytes. This check does not approve the musical body/tail markers or alter production events.


Project-specific audit scripts and saved results belong in `Design/Audio`. Use the native package manifest for download-size claims.


`music_shared_tail_lifecycle_check` runs two overlapping source phrases through pause, mute, resume, and seek in an isolated native session. Require two visible voices, a held paused clock, both gains muted, clock advance on resume, cleared voices on seek, silent post-seek output after unmute, mixer removal, and zero retained bytes. No files or game scenes are changed.


### IDs returned by an authoring add

`edit_audio_authoring` assigns fresh IDs to added tracks, instruments, and other element subtrees. It also remaps marker references inside that subtree. IDs in a proposed patch or `previewOnly` result are not the saved element addresses. After applying an add, inspect the returned state or read the saved event, then resolve the new ID before sharing its `smod:` link. Retained elements keep their IDs. An authoring planner must not use the proposed ID alone to detect whether its first add has already run. Refuse ambiguous matches instead of adding another copy.

### Saved agent edits and the close prompt

Finish agent authoring with a verified graph save and read back the saved state.
Do not leave an applied patch as an unsaved editor draft. Before editing, inspect
all unsaved resources and preserve unrelated user edits.

The close prompt lists unsaved audio resources. It clears stale dirty flags when
the editor content already matches the current file. Save checks the original
saved state against disk and stops if it would overwrite a newer external edit.
Discard restores the current disk content, not the snapshot from when the window
opened. Genuine local drafts still require a save or discard choice.

### Optional inspector fields

Playlist, tone, and overlay are optional objects. Their audio control widgets
must not create a default object when a user opens or inspects a resource. Use
an explicit Add action. The engine generic object control otherwise creates null
reference values while it builds the inspector, which can enable an empty
playlist and prevent all music from starting.

Do not rely on `System.Diagnostics.CodeAnalysis.AllowNull` on these C# properties:
the native regression found the attribute on the setter parameter, while the
engine inspector checks the property. Use the audio-specific control factory.
Run `check_audio_inspector_nulls` after changes to these controls. It uses temporary
objects, checks absent and existing values, and includes an unprotected native
control that must reproduce auto-creation. It writes no assets or audio files.


### Apply a set of world facts

Use `AudioEventInstance.SetParametersByName(IReadOnlyDictionary<string, float>)`
when one gameplay update supplies several numeric facts. It validates all
parameter names, types and ranges before changing values, then reevaluates
playback once. Invalid input changes no values. Labeled parameters use the
existing label API. Single-value setters remain available for isolated changes.

KitchenAudio sends phase, intensity, service, interaction and coffee facts
in one batch. Parent-to-child inheritance already copies matching values
before one reevaluation. A new service trigger can therefore be considered
alongside current activity, rather than losing to the first partial setter.
This does not preempt an already scheduled or playing phrase.


Scenario numeric parameter maps use the same batch API as gameplay. Labels
are applied individually before the numeric batch. Parameters are applied
before a step's seek; use a separate earlier seek step when testing new
counter triggers, because seeking clears pending requests.


### Compound phrase preparation (staged)

`music_compound_prepare_check` prepares a three-hit set from an existing
source range without playback or audio exports. Require exact beat offsets,
worker-thread preparation, cache reuse, budget rejection and zero resident
bytes after close. The source bank caches complete compounds with their
source ranges under one memory budget. This check does not approve the
source cut as a complete drum hit. Event authoring and playback admission
for compound phrases remain under implementation.


`music_compound_playback_check` verifies internal compound scheduling in a
muted native session: full preparation before admission, one shared voice,
common-owner cancellation, rejection of a cancelled owner and zero residency
after stop. The runtime takes tempo from its destination grid. Authored
compound instruments and production interaction resources remain pending.


Shared-output overlay instruments can now define `CompoundHits` (one to
three entries). Each hit has `SourceOffset`, positive `Length`, `BeatOffset`
(0–16), and `Gain` (0–1). It uses the instrument's selected Sound Event
source; SourceOffset is relative to that source track's offset. Ordinary
trims are replaced by the hit ranges. The destination song supplies tempo.
The instrument envelope covers the complete set. A separate body duration
is not supported for compounds. Empty CompoundHits keeps normal playback.

The runtime prepares every hit and the complete set before one admission,
with one owner and group cooldown. Live diagnostics report full set duration.
Authoring must still verify complete hit decays. Editor waveform/hit-position
presentation and saved production-event playback checks remain pending.


`music_compound_event_check` tests an unsaved, muted Audio Event through the
real parameter trigger and shared output. It checks startup, trigger admission,
the full three-hit duration, phrase completion, and instance cleanup. The native
check passed with no new console warnings or errors. This proves the event path;
it does not approve the source cuts or the musical result for gameplay.


Short compound overlays prepare with event backing before its clock starts.
Startup preparation is bounded to 16 choices, at most two seconds of source
ranges per choice and beat offsets through four. It uses the session source
bank and memory limit. Budget rejection keeps backing playback available;
other choices retain preparation on demand. Stop cancels startup preparation.
The scheduler reserves 60 ms before a safe entry. Preloading does not
mean every action can enter the immediately following beat.

## Music track gain

Track volume uses linear gain: 0 is silent, 1 preserves the level, and 2 is about +6 dB. Shared backing and phrases support boosts above 1, including live changes. Gains must be finite and nonnegative. Player master volume and fade envelopes remain 0 to 1. The floating-point mixer does not clamp individual tracks; allow headroom in the combined output to avoid clipping.

## Action timing and phase return

QuantizationBeats supports fractions down to 0.25. At 100 BPM a quarter beat
is 150 ms. Shared music reserves 60 ms scheduling lead; source preparation and
gameplay updates can add delay. Prewarm short compound responses and measure
actual action-to-entry time. Restricted OverlayBars are for compatible lead
destinations, not prompt handling feedback. Live voice Reason reports the
trigger fact or activity facts that admitted a shared phrase.

Set ResumePlayback on a song sub-event choice to keep its local position when
leaving that phase. The bookmark belongs to the current parent instance; it is
not saved to disk or shared across players. Returning to that choice starts its
shared stems at the saved position. A new parent instance starts fresh. Entry
fade timing is relative to the resumed position. Build music can remain a
separate choice. Do not use intensity to select another song.

If authored schema fields are missing after code edits, audio_library_status
reports the actual loaded Smod types and project path. Only for confirmed stale
code, audio_library_refresh uses the editor compile path. Verify status again
before editing and saving; compile success alone is not proof of loaded types.


## Complete exit passages

A song choice can reference an `ExitEvent`. On a phase change, the parent loads
both the passage and destination while the old song continues. At the selected
entry boundary it replaces the old song with the passage. The passage plays one
full shared-output grid, then the prepared destination starts. Initial playback
and full-song rotation do not use ExitEvent. Set `ExitEventOverlapSeconds` on the
parent song choice to start the destination before the passage ends. Zero keeps
the full wait. A positive value must be shorter than the passage. The remaining
source plays to its exact final sample without an extra crossfade envelope;
pause, mute, and stop still apply. Choose the overlap from a verified musical
ending, not just a silence threshold. The passage cannot choose a song or
write parent facts. Matching parent parameters pass down as with other children.

The passage requires Shared Music Output and a valid full grid. The parent limits
all its native output at the exact final sample so an editor stall cannot repeat
its opening. Its authored source must contain its complete ending; this limit
cannot restore a cut note. A changed destination updates the prepared child,
without restarting the passage. If that replacement is late, the passage ends
once and remains silent until it is ready. Pause holds the passage clock.

The existing live overlay shows the active passage as the parent's child. MCP
reports Handoff `Exit passage`, remaining passage seconds, destination facts,
and a voice Reason. The parent context menu can open the exit event. Graph
inspection, cycle validation and graph save include exit references. Duplicate
instruments retain exit, resume and entry settings.

Read the active project's `Design/Audio/README.md` for project policy and examples.


For time-sensitive lead overlays, set a finite MaximumMusicWaitBeats. Candidate
entry is checked before a phrase occupies its group, and source preparation also
uses that deadline. If no candidate can enter in time, the pending action is
cleared. Continuing activity can try again; a one-time action needs a new counter
change. QueueWhilePlaying and cooldown rules can drop busy actions separately.
Keep an immediate, non-pitched response for important interactions when a melodic
lead has restricted entry bars. Do not remove harmonic restrictions to hide delay.


In gameplay, publish action facts at the action. Batch counters that represent
one action, such as service and handling. Use a slower scan for occupancy and
smooth intensity. An activity scan can add delay before the audio scheduler even
receives a coffee start. Verify facts before a subsequent game tick, then measure
the native output separately. Compare actual source levels before choosing gains;
equal gain values do not make quiet and loud recordings equally audible.

### Ready entry and song overlap

`AudioSubEventChoice.EarlyEntryConditions` is an optional alternative to `Conditions`. Every condition in either list must match. An empty early list does not enable early entry. Use a world fact such as a ready countdown. The same choice remains active when normal phase conditions become true, so the song does not restart at gameplay start. A cancelled early entry skips that song's `ExitEvent` and returns directly to the eligible phase.

`EntryCrossfadeSeconds` keeps the outgoing shared song alive while the incoming song starts. Zero keeps sequential behavior. A positive value uses the outgoing audio sample clock for a smooth volume fade. `EntryFadeBeats` controls the incoming song separately. The next beat does not wait for the whole overlap duration. Retained outgoing children remain owned, visible in live diagnostics, and affected by pause, mute, stop and seek. A new replacement clears an older overlap to bound resource use.

For a pitch bend on an existing native clip, automate `Pitch` and `Volume` on its retained track. Do not jump or retrigger that clip. Pitch 1 is unchanged; 1.12 raises playback speed and pitch; 0.78 lowers both. Shared music stems require unit pitch. Master volume and pitch automation apply once to the master instruments and once to each child track.

Read the active project's `Design/Audio/README.md` for project policy and examples.

## Session level measurements

Use `audio_session_start` after playback starts. It returns immediately and captures the current editor audio session until `audio_session_stop`, a mixer replacement, or a configured limit. The default interval is one second; supported values are 0.25-10 seconds. Duration is 1-240 minutes (default 120), with a 512 MiB file limit. These are recording limits, not a required test duration. Use the project review plan for the duration of a check.

The tools write JSONL statistics under `.validation/audio-sessions`, never audio samples. `audio_session_status` gives the last completed front-stereo window. `audio_session_mark` records a named moment or volume change. Stop and flush before calling `audio_session_report` or paged `audio_session_read`. The report supports `fromSeconds` and `toSeconds` for comparisons, and lists instruments and gain ranges observed within its loudest, quietest and stereo-cancellation windows. Follow its exact `smod:` element links before tuning.

Measurements include sample peak and RMS in dBFS, clipping counts, invalid samples, mono loss and stereo correlation. Zero dBFS is sample full scale. RMS is the unweighted average signal level; it is not LUFS. True peaks between samples are not measured. Channel -1 is the front stereo pair; channels 0-7 are measured separately. The meter observes the last master processor input and applies the master mixer and `Sound.MasterVolume` gains to its measurements only. It never changes the signal. This is an estimate before engine hard limiting and Windows/device output, not sound pressure at the speakers. Multiple listeners remain separate: never add their dB values or present one listener as the final sum.

Instrument/world snapshots are sampled every 100 ms; each carries instance IDs, element IDs, selected sources, parameters, gain factors and reasons. Inactive/stopped rows are omitted; limits are 128 instances and 256 rows per instance. Very short sounds can fall between snapshots. A correlated instrument is a candidate cause, not an isolated measured source. Record actual elapsed times and maximum snapshot gaps. Silent periods without audio callbacks and the final partial audio window are excluded from energy averages. A complete footer means the file closed, not that the entire intended session was observed; read its stop reason and dropped-window counters.

The recorder is editor-only. It detaches only its own processor, and releases stopped processor/session objects. No meter runs in a published game. Do not clear another tool's processors or stop the user's play session for measurement. The mix thread does bounded arithmetic and queues at most 256 windows; JSON/file work stays out of that callback.

Use the active project's `Design/Audio` for game-specific mix tools, volume defaults, saved preferences, and listening reference settings.
## Parent instrument completion triggers

An ordinary sound instrument can use `StartTriggerConditions` to play once when all listed conditions become true. An empty list keeps normal timeline behavior. `StartTime` is a delay from the trigger. A false condition rearms the trigger but lets the current sound finish. A new true entry replaces that instrument's previous triggered voice. Optional `StopTriggerConditions` cancel the voice without marking it complete; use them after a cancellation envelope reaches silence.

Set a sub-graph choice's `EntryAfterInstrument` to the permanent GUID of a triggered sound in the same event. The parent prepares the destination while that sound plays and retains the outgoing child. When the source reaches its explicit trim end, the parent admits the prepared destination on the next update, without an additional beat wait. Native source time accounts for pitch changes. If the destination is still loading, the outgoing song remains until it is ready. This is update-driven admission, not a sample-accurate cross-mixer splice. When no trigger has fired in this parent session, normal selection applies.

The trigger sound, its conditions, pitch/gain automation, and destination gate all belong to the parent. No child writes parent state. Completion remains latched until a new trigger. Changing world conditions can cancel a pending destination. Pause holds playback; mute changes gain without cancelling completion. Stop releases owned voices. Explicit seek resets the trigger and starts a fresh passage if its conditions currently match.

This first implementation supports finite ordinary sound instruments in non-shared parents, including the Master track. It does not support triggered sub-graphs or shared-output overlay slots. Use a positive explicit trim range, unconditional playback admission, and automation for gain changes. Validation rejects foreign or ambiguous GUIDs. The live overlay's Reason field states which parent instrument a song awaits; sound rows show waiting, playing, completed, or cancelled trigger state. `inspect_audio_authoring` exposes all controls.

### Scenario seeks with retained children

In `check_audio_scenario`, `seekSeconds` moves the temporary parent. `seekToSongEndLeadSeconds` seeks the temporary active child to its grid end minus the given lead. It must not advance the parent as a substitute: a retained child intentionally ignores parent timeline jumps. The report includes `SongEndSeek` with the child ID, original/requested child position, and parent positions. Reject negative, non-finite, and out-of-grid leads. Inspect the subsequent child snapshots to prove the song actually crossed its ending. One eligible song looping does not prove rotation between compositions.

### Release tails at a composition loop

`AllowReleaseTailAcrossLoop` is an explicit instrument option for shared-output sound phrases. It requires positive `OverlayBodySeconds`. The complete body must fit before the composition loop ends; the full source range, envelope and tail still play on the shared clock afterward. False preserves full-clip boundary admission. It does not widen allowed bars, permit a partial body, or allow cross-graph commands. Verify the released notes against the loop's incoming harmony. A later change to another composition needs its own handoff review.

### Native source audition check

`check_audio_source_audition(sourcePath)` uses an existing imported sound at least two seconds long. It creates a temporary muted event, waits for native source progress, pauses it, changes the graph-local source gain and pitch, refreshes audition state, and restores the values. It reports control values, paused source-clock continuity, and cleanup. It writes no assets and does not modify user previews. It does not measure audible output level or verify an inspector mouse interaction.


## Instrument echo effect

Local (`UI=true`) non-shared source instruments support optional `Echo` under Effects. Use the optional Add control; inspecting an absent effect must leave it null. Native source voices use Echo with ReleaseOnly=false. Shared-output stems use ReleaseOnly=true, as described below. Overlay phrase tracks, sub-graph selectors, spatial events, and parent layer-control nodes cannot own this effect; validation rejects those cases.

`DelaySeconds` is the repeat interval (0.02-0.5 seconds), `WetGain` is the first-repeat amplitude (0-0.6), and `Feedback` is the retained fraction per repeat (0-0.75). The dry signal is unchanged. `TailSeconds` bounds output after the source stops (0.1-4 seconds), with a final 150 ms fade. These settings are captured when a voice starts; editing them affects the next voice, not a live delay buffer.

Normal source/trim completion releases the tail without delaying instrument completion triggers. Explicit event stop cancels tails, and event pause and zero-volume/mute suppress them. Tail routes use a silent in-memory carrier, then destroy their private mixer; no audio file is written. A five-second unpaused watchdog removes a route if native callbacks cease. Existing source and track tone processing stays ahead of echo.

Use `check_audio_echo(sourcePath, sourceOffset)` for a short muted native source-stop/tail/cleanup check. It measures PCM before its private mixer mute, not speaker level or listening quality. `check_audio_inspector_nulls` includes optional Echo creation and value retention.


### Sustain release curve

`AudioMusicLayerControl.ReleaseCurve` selects LinearAmplitude or SmoothDecibels. LinearAmplitude changes the amplitude multiplier at a constant rate. SmoothDecibels interpolates between the starting and idle levels in decibels, with zero slope at both ends. `ReleaseSeconds` is the complete release duration. A zero idle gain uses a -80 dB calculation floor and reaches exact zero at the end. This is a gain envelope, not reverb and not a recorded phrase ending.

The release follows the child clock and the actual phrase boundary, including updates that arrive late. Pause holds that clock. A new admitted request during release starts its attack from the current gain. The source is not restarted or moved. Project choices belong in Design/Audio.


### Buffered echo for shared stem release

Set the child source instrument's `Echo.ReleaseOnly=true`. Set its parent sustain control's idle gain to zero. A release-only instrument closes its source contribution when the parent's held play period ends; it does not extend a source fade into the next note. Its short stereo buffer then repeats with falling level for `TailSeconds`. The output uses a 5 ms transition from the last sample into the echo and a final 150 ms fade. This is echo, not a recorded ending or diffuse reverb.

Buffers are allocated during backing preparation, not during sample rendering. Active dry audio remains unchanged. Closed input cannot feed the next source note into the buffer. Retrigger clears the old tail and records the newly active part. Seek clears history; pause holds it. Mute/disabled parent control or track conditions clear the tail independently of its source gain. Stop cancels shared output. The base stem and common source clock remain intact.

`check_audio_music_release_echo` uses one second of an existing source in a muted native route and returns tail and post-tail sample peaks. Its source preparation budget is 64 MiB. The post-tail threshold is 1e-7 amplitude (-140 dBFS); native output can contain tiny floating-point residue. DSP tests separately require exact zero after expiry and reject injection of a following note. The tool writes no audio.

## Native compressed playback regression checks

Do not issue a native seek when the requested time already equals the voice's time. In particular, a new voice already starts at zero. The engine can apply a queued seek before its decoder-readiness check. An explicit SoundFile.LoadAsync call is not equivalent to decoder readiness.

For first-use regressions, use a separate fresh client. Editor playback can hide loading faults. The opt-in `start_audio_client_probe` MCP tool creates a temporary networked probe only in a running host; it writes silent playback results to the client log. Remove it with `remove_audio_client_probe`. Source paths are supplied by the caller; no project source list is built into the library. Compare normal Smod playback, explicit preload, and direct native playback. Check both nonzero samples and absence of MP3 warnings. Use `check_audio_native_seek` to verify that real seeks and rewinds still work. No probe runs in normal gameplay unless its temporary component is explicitly created.

## Local development paths

Set `FACEPUNCH_ENGINE` (or the MSBuild `SboxRoot` property) to your s&box installation when building from the command line. Set `SMOD_AUDIO_SOURCE_ROOT` before starting the editor to enable the optional external audio library. If it is unset, the external library is empty. Keep local paths and generated build output out of commits.


### External source import through MCP

`import_audio_source(sourcePath, bitrate)` uses the same byte-preserving import path as the editor source library. The file must be within the configured `SMOD_AUDIO_SOURCE_ROOT` and be WAV or OGG. It reuses identical imports, enables native compression (128-256 kbps), compiles, and checks native load before returning the registered sound path. It does not alter the original file or add an instrument to an event. Use revision-checked authoring tools to connect the returned source. Configure source roots in the host project; the library does not choose a project folder.
