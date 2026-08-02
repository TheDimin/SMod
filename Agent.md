# Smod library agent guide

Read `README.md` before changing the public API.

- `AudioEvent` (`.audevt`), `AudioParameter` (`.audparam`), and `SoundEventResource` (`.sndevt`) are the custom authored audio resources.
- Keep names aligned with FMOD Studio/API concepts.
- `AudioEvent` directly owns the shared native playback settings used by every voice in the event.
- `SoundEventResource` owns ranged volume/pitch, source selection, and the sole conditioned sound-file track list with per-track offset and length; do not add a nested native `SoundEvent.Sounds` playlist.
- Audio tracks and instruments both support live parameter conditions; master-track conditions gate the whole event.
- Parameter conditions and automation currently evaluate into the playback plan at `Start()` and are re-evaluated when parameters change during active playback.
- Add validation and focused tests for every serialized capability.
- Do not recreate world facades, emitter schemas, behavior profiles, archetypes, or a bespoke audio editor.
- Run the host project's focused test target filtered to `AudioEventTests` after changes; do not assume a consumer-specific test project or name.
