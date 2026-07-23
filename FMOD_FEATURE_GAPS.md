# FMOD Studio feature gap tracker

This document is the expansion backlog for ValArk Audio. FMOD terminology is
used as the shared vocabulary, but ValArk Audio currently plays s&box
`SoundEvent` resources and is not a binary-compatible FMOD implementation.

Primary references:

- [FMOD Studio concepts](https://www.fmod.com/docs/2.03/studio/fmod-studio-concepts.html)
- [Authoring events](https://www.fmod.com/docs/2.03/studio/authoring-events.html)
- [Working with instruments](https://www.fmod.com/docs/2.03/studio/working-with-instruments.html)
- [Parameters](https://www.fmod.com/docs/2.03/studio/parameters.html)
- [Quick start tutorial](https://www.fmod.com/docs/2.03/studio/quick-start-tutorial.html)
- [Mixing](https://www.fmod.com/docs/2.03/studio/mixing.html)
- [EventDescription API](https://www.fmod.com/docs/2.03/api/studio-api-eventdescription.html)
- [EventInstance API](https://www.fmod.com/docs/2.03/api/studio-api-eventinstance.html)
- [Bus API](https://www.fmod.com/docs/2.03/api/studio-api-bus.html)

## Status

| FMOD concept | ValArk status | Current behavior / missing work |
|---|---|---|
| Event resource | Basic | `.audevt` owns a master track, tracks, parameters, fade-out duration, and a timeline derived from its instruments. |
| Event description and instance | Basic | `AudioSystem.GetEvent`, `CreateInstance`, `Start`, `Stop`, `Release`, playback state, pause, and timeline seek. No instance pooling, GUID lookup, callbacks, or virtualization. |
| Audio and master tracks | Basic | Concurrent tracks, per-track gain/pitch, parameter conditions, master gain/pitch/conditions, and timeline placement are supported. False regular-track conditions mute that track; false master conditions mute the event. No routing graph, persistent mute/solo state, return tracks, track colors, or effect deck. |
| Single instrument | Basic | References one `SoundEventResource`; `.sndevt` directly owns its sole playable track list and playback properties. The event instrument adds timeline placement, conditions, trim, and automation. |
| Multi instrument | Basic | Conditioned sound-file tracks are selected from the eligible set using random or random-exclusive selection and support per-track source offset and playback length. No nested events or weighted selection. |
| Continuous parameter | Basic | Numeric range/default, live condition transitions, and live volume/pitch automation. No velocity, seek speed, or parameter-scoped trigger behavior. |
| Discrete parameter | Basic | Integral range/default with live conditions and automation. |
| Labeled parameter | Basic | Declared labels/default and live exact-label conditions. |
| Automation | Basic | Piecewise-linear volume/pitch curves update active voices. No curved segments, modulation, or seek speed. |
| 3D attributes | Basic | Position is applied to native voices; velocity/orientation are stored and validated but not yet consumed by s&box playback. |
| Event editor | Basic | Dedicated s&box-styled timeline, whole-event preview, repeat/follow audition, direct track volume/pitch faders, transient mute/solo, movable instrument blocks, sound-file picker/drop workflows, waveform overlays, live parameter controls, curves, condition regions, embedded `.sndevt` inspector, undo/redo, and grouped save. It is intentionally not full FMOD parity. |
| Event callbacks | Missing | Add lifecycle, marker, beat, and programmer-sound callbacks when a real consumer exists. |
| Timeline, markers, tempo | Partial | Real-time cursor, delayed starts, seek, pause, stable-ID destination/transition markers, conditional transition and loop regions, a constant tempo map, beat/bar quantization, deterministic priorities, and bounded jump chains are supported. No sustain points, changing tempo/time signatures, or callback tracks. |
| Async and programmer instruments | Missing | No asynchronous, command, silence, scatterer, or programmer instruments. |
| Nested events | Missing | Events cannot yet contain event instruments or parameter propagation. |
| Modulation and randomization | Partial | `.sndevt` resources directly own ranged pitch/volume randomization. No event-layer LFOs or AHDSR modulators. |
| Effect chain and sends | Missing | No DSP effect slots, pre/post fader sends, sidechains, or return tracks. Native `SoundEvent` settings still apply. |
| Buses and VCAs | Missing | No bus hierarchy, VCA control, pause/mute, channel-group access, or per-bus metering. |
| Snapshots and ducking | Missing | No snapshot events, scoped overrides, or sidechain ducking. |
| Banks and sample loading | Missing | Resources load through s&box; there are no FMOD-style banks, strings banks, sample-data states, or unload policies. |
| Global parameters | Missing | Parameters are local to an event instance. |
| Spatializer features | Missing | No listener masks, min/max distance override, doppler, cone attenuation, occlusion, geometry, or reverb sends. |
| Polyphony and stealing | Missing | No event/bus voice limits, virtualization, cooldown, or stealing policy. |
| Mixer and loudness tooling | Missing | No mixer UI, metering, loudness analysis, or platform mix variants. |
| Live update and profiler | Partial | Editor preview reacts to parameter changes and timeline edits. No remote authoring connection, command capture/replay, voice profiler, metering, or performance overlay. |
| Platform builds | Missing | No platform-specific compression, encoding, bank build, or localization pipeline. |

## Authoring ownership boundary

An `AudioInstrument` deliberately contains only a `.sndevt` reference, timeline
placement, conditions, trim, and automations. `SoundEventResource` directly owns
the playback properties and conditioned sound-file tracks. Runtime creates a
native one-clip `SoundEvent` only after the ValArk selector has chosen a track,
so the native playlist can never compete with the authored `.sndevt` tracks.

## FMOD authoring workflow parity

The FMOD 2.03 quick start is the target interaction model, adapted so that a
`.sndevt` provides the single/multi instrument playlist editor.

| Workflow step or behavior | Current state | Remaining authoring UX |
|---|---|---|
| Open an event and work on its timeline sheet | Basic | Timeline, track rows, ruler, playhead, zoom, scrolling, and separate asset windows exist. Missing named non-timeline parameter-sheet management and sheet reordering. |
| Add and organize audio tracks from the event canvas | Partial | Tracks can be created from the toolbar or master/track/empty-space context menus, renamed, deleted, and inspected. Colors, reordering, and inline name editing remain pending. |
| Create an instrument by dropping audio content | Basic | Dropping a sound file or choosing one from a track context menu creates an embedded `.sndevt` instrument at the target time; a block can move in time and between tracks. Multi-select and bulk movement remain pending. |
| Audition the event | Basic | Whole-event play/pause, stop/reset, seeking, repeat, follow-playhead, and transient per-track mute/solo preserve `.sndevt` variation behavior. Metering and instrument-only audition are missing. |
| Edit the selected instrument | Partial | Conditions, automations, referenced-sound inspection, duplication, deletion, and timeline movement are available through the editor. FMOD-style discoverability, bulk edit, trigger-region resize/trim, and asynchronous instrument modes are missing. Resize/trim is intentionally outside the current move-only scope. |
| Add and manipulate a parameter | Basic | Named creation, coordinated rename/removal, continuous/discrete/labeled definitions, live controls, sheets, conditions, and automation are supported. A guided type/range dialog, transport number boxes, parameter-sheet context actions, snapping, and velocity/seek-speed behavior are missing. |
| Automate track volume or pitch from a parameter | Partial | Linear volume/pitch automation curves update preview live and can be created from a track or focused fader context menu. Curved segments, multi-point selection, copy/paste, numeric point entry, and modulation are missing. |
| Define trigger conditions on a parameter sheet | Basic | Conditions combine with AND semantics and react live. More expressive region editing, inverse/ranged labeled conditions, and parameter-scoped trigger behavior are missing. |

FMOD behaviors not yet represented in the quick-start path include nested event,
programmer, command, silence, scatterer, and asynchronous instruments; sustain
points, transition and loop regions; effect chains and sends; tempo markers;
polyphony/stealing controls; and mixer/profiler workflows.

## Expansion rule

Implement the smallest missing FMOD concept that unlocks a real game need.
Keep the public name aligned with FMOD unless s&box imposes a materially
different contract. Add focused validation and tests, update this table, and do
not revive the removed director, emitter schema, behavior profile, archetype,
or world-facade layers.
