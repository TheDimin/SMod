# Smod library agent guide

Read the active project's `Design/Audio/README.md` for project rules and examples. Store project decisions and listening results there. Keep shared skills and library guidance generic. Read `README.md` before changing the public API, creating audio resources, or connecting gameplay to audio. For sounds with start, loop, and finish phases, follow its **One event for one process** workflow.

- Follow the active project's `Design/Audio` source, licensing, processing, and capture policy before audio work.
- Use one `AudioEvent` and one retained instance for a complete process. Author its phases as timeline tracks and instruments. Use parameters and timeline transitions to control phase changes.
- For music, follow README **Music grids and transitions on a beat**. Author beat grids, transition boundaries, and fades in the event; gameplay sends world state and intensity only. Never expose Song, Track, or Variation selection parameters. The audio engine owns selection, rotation, arrangement variation, and transition timing. Read the smod-audio skill at `smod-audio/SKILL.md`.
- Keep source slices and pitch variations in plain `AudioSourceDefinition` entries in `AudioEvent.Sources`. A clip uses a local `SourceId`. Never create `.sndevt` resources.
- Verify that the same instance starts, repeats, changes phase on its trigger, and stops. Use editor resource validation and live playback checks for resource changes.

- `AudioEvent` (`.audevt`) and `AudioParameter` (`.audparam`) are the custom authored audio resources. Source definitions are plain graph-owned data.
- Keep names aligned with FMOD Studio/API concepts.
- `AudioEvent` directly owns the shared native playback settings used by every voice in the event.
- `AudioSourceDefinition` owns ranged volume/pitch, source selection, and the sole conditioned sound-file track list with per-track offset and length; do not add a nested native `SoundEvent.Sounds` playlist.
- Audio tracks and instruments both support live parameter conditions; master-track conditions gate the whole event.
- Parameter conditions and automation currently evaluate into the playback plan at `Start()` and are re-evaluated when parameters change during active playback.
- Give every serialized capability executable validation and focused behavioral coverage: construct controlled resource data, call the public validator/planner/playback boundary, and assert the result. Do not inspect `.cs` source text, private method bodies, or authored `.audevt`/`.audparam` files to prove the editor implementation has a particular shape. For editor-only interaction or rendering, extract a testable policy/state transition when practical and verify the remaining UI behavior in the editor.
- Do not recreate world facades, emitter schemas, behavior profiles, archetypes, or a bespoke audio editor.
- Run the host project's focused test target filtered to `AudioEventTests` after runtime or API changes; do not assume a consumer-specific test project or name.

- For large events, follow README **Sub-graphs**. Keep phase selection in the retained parent and each song's beat grid, loop, instruments, and world-state response in its child. Validate the full reference graph and exact parent/child live-instance navigation.

- Read the active project's `Design/Audio` for song rotation, intensity, and phase policy. The audio engine owns music decisions from world facts.

- For parent flow, sustained action roles, direct phase cues, or fixed live blocks, read README **Parent music flow and fixed source controls**. Use a child file plus a track GUID for scoped controls. Never claim the current runtime preparation is a separate build-time compiler.

- Before using overlays, read the project timeline and phrase policy. The existing overlay mechanism is not a requirement to compose short hit sets or move authored blocks during live preview. Distinguish parent flow, fixed composition parts, and triggered phrases. New prepared-plan and section-repeat capabilities must be documented as planned until implemented.

- Movable music overlays use a track `Overlay` rule and source trims with `OverlayBars`. Gameplay supplies facts such as a service counter; the audio engine selects and times phrases. Keep continuous backing aligned, restrict phrase destinations by harmony, and test beat entry, cooldown, pause, stop, and live diagnostics. Read README **Movable music phrases**.

- Apply the project's intensity and song rotation policy from `Design/Audio`. For transitions, follow README **Musical handoff between song sub-graphs**: phrase-boundary phase changes, authored stem roles, and beat-based exit/entry envelopes. Do not overlap unrelated harmony based only on BPM or estimated key. Listening acceptance is separate from timing and gain tests.

- Prefer README **MCP authoring workflow** for event edits. Inspect live state and disk revision first; edit through native undo; use previewOnly for a proposed patch; save graphs with all revision tokens and read-back verification. Never silently overwrite a revision conflict. The graph save tool saves `.audevt` resources only; handle intentionally changed `.audparam` resources through the existing editor save workflow and verify them separately.
- Use README **Reusable scenarios and live reports** for repeatable muted parameter, phase, pause, reversal and song-end checks. Require both Passed and Cleaned. Inspect exact instance/descendant IDs and gain factors. Long-session comfort and repetition use the project review plan in `Design/Audio`.

- For a pasted `smod:` link, follow README **Exact element links (Get Path)**. Resolve it before editing; use the returned live pointer and revision. Resolve the permanent GUID again before each edit to get its current JSON pointer. Never change IDs or guess missing targets.


Parent-to-child control only: the parent sub-graph instrument owns song selection and its repetition. Loop Sub-graph Selection is the editor name for RotateSubEventsAtEnd. A child owns only its local arrangement. It cannot select a sibling, write parent parameters, or move the parent timeline. Go To markers and transition regions must target a destination in the same event, even when the destination GUID exists in another graph. Parent inspection of child playback status is permitted; it is not a child command. Internal backing loops keep sound continuous while the parent waits for a ready replacement. Never use a cross-graph Go To as a loop or song-change mechanism.

For audible cuts, sliced stems, or repeated music phrases, read the smod-audio skill reference `smod-audio/references/phrase-continuity.md`. Keep an unverified rhythm bed continuous; beat-aligned waveform cuts do not establish valid loops or complete phrases.

- For a ready countdown, use README **Ready entry and song overlap**. Keep one service choice across early entry and the actual gameplay phase. Do not create a second choice that restarts the same song. Check both child IDs and positions across the phase change.
- For cancellation pitch bends, retain the current native voice and automate its pitch and volume from elapsed cancellation time. Remove only the exact replaced cues through native undo. Check effective gains on every child track; master automation must reach them. Shared music stems remain at unit pitch.

- For quiet/loud output, use README **Session level measurements** before changing gains. Start/mark/stop a passive recording, then report the relevant time range. Keep source audio unchanged. Identify active instances and instruments from correlated snapshots; distinguish control gains from measured PCM output. Never call RMS LUFS, sample peak true peak, or digital dBFS speaker dBSPL. Check clipping, mono loss, dropped windows and stop reasons. Use the project review duration and listening method.

- For level decisions and player listening review, read the smod-audio references `smod-audio/references/mix-level-tuning.md` and `smod-audio/references/music-theory-adaptive-audio.md`. The first covers measurement and controlled mix trials. The second covers musical roles, response timing, transitions, and listening acceptance, with plain examples for the user. The library README remains the source of truth for tool limits and implementation behavior.
