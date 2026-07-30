# Smod Audio

Smod Audio is a deliberately small, FMOD-shaped event layer over s&box
`SoundEvent` playback. An `AudioEvent` (`.audevt`) owns its timeline and
instruments plus the shared UI, spatial, attenuation, falloff, loudness, and
mixer settings used by every voice. Each instrument references a
`SoundEventResource` (`.sndevt`), which owns ranged volume/pitch, source
selection, and conditioned sound-file tracks with independent offsets and lengths.

## Runtime API

```csharp
var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
instance.Attributes3D = new Audio3DAttributes( worldPosition );
instance.SetParameterByNameWithLabel( "Action", "Hit" );
instance.SetParameterByName( "Intensity", 1.0f );
instance.Start();
instance.Release();
```

For persistent scene audio such as radios, add a `SoundPointComponent` to a
game object and select an `.audevt`. Its inspector derives continuous,
discrete, and labeled controls from that event's parameters. `PlayOnStart`
defaults to enabled, while `Play()` and `Stop()` provide explicit runtime
control. The component keeps the event instance at the game object's world
position and forwards parameter value changes while it plays.

The naming follows FMOD Studio/API concepts: `AudioSystem`, event description,
event instance, tracks, instruments, parameters, `Start`, `Stop`, and `Release`.
It is not an FMOD binding; native playback remains s&box `SoundEvent` playback.
Each `.sndevt` owns its only playable clip list plus ranged volume, pitch, and
selection mode. The owning `.audevt` supplies shared attenuation, occlusion,
reverb, UI, loudness, falloff, and mixer properties. Instruments can author fade-in and fade-out
durations with adjustable Bézier midpoint controls; overlapping faded
instruments form a crossfade, and the
fade envelope multiplies the existing track, automation, audition, and instance
gain. Eligible tracks use the authored `Random` or
`RandomExclusive` selection mode when an instance starts; selection is retained
while it remains eligible during live parameter changes.

Tracks and instruments can both have parameter conditions. A false regular-track
condition mutes only that track; a false master-track condition mutes the whole
event. Conditions react live and can restore playback when they become true.

Event instances have a real-time playhead. `TimelinePosition` seeks active
voices and reschedules future instruments, while `Paused` freezes the event and
its pending starts. Parameter changes are live: automation updates existing
voices immediately, false conditions stop their voices, and newly true
conditions start or schedule their instruments relative to the current
playhead. `Stop` cancels scheduled starts; `Release` leaves active and scheduled
voices running as fire-and-forget playback.

Adaptive events can author stable-ID destination and transition markers,
immediate/beat/bar-quantized transition regions, and conditional or
unconditional loop regions. One per-instance sample evaluator resolves crossed
markers, region conditions, quantized pending transitions, loop overshoot, and
competing priorities. When a loop wraps, instruments authored inside that region
retrigger and select a new eligible `.sndevt` variation; `RandomExclusive`
avoids the preceding variation when another eligible track exists. Instruments
that begin before the region keep their current selection. `TimelineDebugSnapshot`
exposes the current sample, active loop, pending destination and execution
sample, and jump-limit state.

Loop regions use a fixed authored end by default. `ContentEnd` loops can wrap
earlier when every currently eligible playing or pending instrument authored
inside the region has completed. The selected `.sndevt` variations determine
completion for that iteration, while the authored end remains the hard fallback
when no eligible content can establish an earlier boundary.

## Authoring and validation

Double-click an `.audevt` to open the dedicated Audio Event Editor. It provides
whole-event transport, seek and zoom controls, master and regular timeline
tracks with direct volume/pitch faders, movable/trim-able instrument blocks, waveform overlays, live parameter
controls, automation curves, condition regions, and a context inspector. Select
the Audio Event root to edit shared playback settings. Instrument inspection keeps
timeline properties separate; embedded sources use a dedicated source/variation
selection and external `.sndevt` assets open in their own editor. Drop a sound file
onto a track to place an instrument at that time. `Ctrl+S` saves dirty `.audevt` and referenced
`.sndevt` resources together.

Selected or hovered instruments show fade handles at the top of their start and
end edges. Drag a handle inward to author a fade and overlap two
instruments to create a crossfade. Each fade also shows a middle control point;
drag it vertically to bend the fade above or below its linear shape. The
envelope is drawn directly over the clip, while dragging the full-height side
edges continues to trim the source.

Timeline-control buttons and the timeline context menu create destination
markers, transition markers, transition regions, and loop regions at the
playhead or cursor. Markers drag horizontally; regions drag as a whole and
resize from either edge. Selecting a control exposes its conditions,
quantization, priority, name, and timing in the inspector. Transition context
menus select or navigate to stable-ID destinations, and destination inspection
lists every authored reference.

Beat- and bar-quantized transition regions also expose editor-only tempo
assistance. When the authored BPM is missing or outside 40–240 BPM, the editor
lazily decodes overlapping instrument variations, estimates their tempos in the
background, and shows per-clip confidence and half/double-time ambiguity. It
only suggests a median when the clips agree, and never changes the serialized
tempo until the author chooses **Apply Tempo**. **Analyze Again** is available
for plausible authored tempos without loading samples automatically.
`BeatsPerBar` remains author-controlled because PCM analysis does not infer
meter.

Incomplete events can still be saved. Preview is disabled until
`AudioEventValidator` reports a valid event. `audio.validate_events` validates
all loaded `.audevt` resources, and read-only `audio.inspect_event` reports one
event's tracks, start times, native sound references, derived duration,
conditions, automations, parameters, and issues.

See [FMOD_FEATURE_GAPS.md](FMOD_FEATURE_GAPS.md) before expanding the system.
Add a missing capability there first so new work stays aligned with an explicit
FMOD concept rather than growing another parallel abstraction.

## Tests

Run:

```powershell
dotnet test Tests\HostProject.Tests\host.tests.csproj --filter FullyQualifiedName~AudioEventTests
```

The focused gameplay regression tests are `AxeTreeAudioTests` in the main test
project.
