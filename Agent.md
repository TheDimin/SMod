# Smod library agent guide

Read `README.md` before changing the public API, creating audio resources, or connecting gameplay to audio. For sounds with start, loop, and finish phases, follow its **One event for one process** workflow.

- Follow README **Host project source-file rule** before audio work: use only AudioSource originals or verified existing imports, and make all sound changes through Smod resources or runtime processing. Do not write audio files, including audition renders and capture exports.
- Use one `AudioEvent` and one retained instance for a complete process. Author its phases as timeline tracks and instruments. Use parameters and timeline transitions to control phase changes.
- For music, follow README **Music grids and transitions on a beat**. Author beat grids, transition boundaries, and fades in the event; gameplay sends world state and intensity only. Never expose Song, Track, or Variation selection parameters. The audio engine owns selection, rotation, arrangement variation, and transition timing. Read the smod-audio skill at `smod-audio/SKILL.md`.
- Keep source slices and pitch variations in `.sndevt` resources referenced by that event. Separate source resources do not require separate phase events.
- Verify that the same instance starts, repeats, changes phase on its trigger, and stops. Use editor resource validation and live playback checks for resource changes.

- `AudioEvent` (`.audevt`), `AudioParameter` (`.audparam`), and `SoundEventResource` (`.sndevt`) are the custom authored audio resources.
- Keep names aligned with FMOD Studio/API concepts.
- `AudioEvent` directly owns the shared native playback settings used by every voice in the event.
- `SoundEventResource` owns ranged volume/pitch, source selection, and the sole conditioned sound-file track list with per-track offset and length; do not add a nested native `SoundEvent.Sounds` playlist.
- Audio tracks and instruments both support live parameter conditions; master-track conditions gate the whole event.
- Parameter conditions and automation currently evaluate into the playback plan at `Start()` and are re-evaluated when parameters change during active playback.
- Give every serialized capability executable validation and focused behavioral coverage: construct controlled resource data, call the public validator/planner/playback boundary, and assert the result. Do not inspect `.cs` source text, private method bodies, or authored `.audevt`/`.audparam`/`.sndevt` files to prove the editor implementation has a particular shape. For editor-only interaction or rendering, extract a testable policy/state transition when practical and verify the remaining UI behavior in the editor.
- Do not recreate world facades, emitter schemas, behavior profiles, archetypes, or a bespoke audio editor.
- Run the host project's focused test target filtered to `AudioEventTests` after runtime or API changes; do not assume a consumer-specific test project or name.

- For large events, follow README **Sub-graphs**. Keep phase selection in the retained parent and each song's beat grid, loop, instruments, and world-state response in its child. Validate the full reference graph and exact parent/child live-instance navigation.

- Host project music policy: rotate to another eligible service song when the current full song ends. Enable `RotateSubEventsAtEnd` on the parent music instrument. Intensity changes instrument layers only. Keep phase eligibility in the parent; build uses a soft backing-led arrangement. Do not reintroduce intensity-based song eligibility.

- Movable music overlays use a track `Overlay` rule and source trims with `OverlayBars`. Gameplay supplies facts such as a service counter; the audio engine selects and times phrases. Keep continuous backing aligned, restrict phrase destinations by harmony, and test beat entry, cooldown, pause, stop, and live diagnostics. Read README **Movable music phrases**.

- Intensity must never determine song eligibility. Use world-state choice conditions and full-song rotation. For transitions, follow README **Musical handoff between song sub-graphs**: phrase-boundary phase changes, authored stem roles, and beat-based exit/entry envelopes. Do not overlap unrelated harmony based only on BPM or estimated key. Listening acceptance is separate from timing and gain tests.

- Prefer README **MCP authoring workflow** for event edits. Inspect live state and disk revision first; edit through native undo; use previewOnly for a proposed patch; save graphs with all revision tokens and read-back verification. Never silently overwrite a revision conflict. The graph save tool saves `.audevt` resources only; handle intentionally changed `.sndevt` or `.audparam` resources through the existing editor save workflow and verify them separately.
- Use README **Reusable scenarios and live reports** for repeatable muted parameter, phase, pause, reversal and song-end checks. Require both Passed and Cleaned. Inspect exact instance/descendant IDs and gain factors. Long-session comfort and repetition use build-review feedback. Do not run or require a 20-minute playback or listening test.

- For a pasted `smod:` link, follow README **Exact element links (Get Path)**. Resolve it before editing; use the returned live pointer and revision. Resolve the permanent GUID again before each edit to get its current JSON pointer. Never change IDs or guess missing targets.


Parent-to-child control only: the parent sub-graph instrument owns song selection and its repetition. For restaurant music, use the Restaurant music node (374b672a0023ea7fafa4f55dc52176a5) in cafe_music_loop.audevt. Loop Sub-graph Selection is the editor name for RotateSubEventsAtEnd. A child owns only its local arrangement. It cannot select a sibling, write parent parameters, or move the parent timeline. Go To markers and transition regions must target a destination in the same event, even when the destination GUID exists in another graph. Parent inspection of child playback status is permitted; it is not a child command. Internal backing loops keep sound continuous while the parent waits for a ready replacement. Never use a cross-graph Go To as a loop or song-change mechanism.

For audible cuts, sliced stems, or repeated music phrases, read the smod-audio skill reference `smod-audio/references/phrase-continuity.md`. Keep an unverified rhythm bed continuous; beat-aligned waveform cuts do not establish valid loops or complete phrases.
