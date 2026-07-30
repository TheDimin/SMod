# Smod library agent guide

Read `README.md`, then `FMOD_FEATURE_GAPS.md`, before changing the public API.

- `AudioEvent` (`.audevt`), `AudioParameter` (`.audparam`), and `SoundEventResource` (`.sndevt`) are the custom authored audio resources.
- Keep names aligned with FMOD Studio/API concepts.
- `AudioEvent` directly owns the shared native playback settings used by every voice in the event.
- `SoundEventResource` owns ranged volume/pitch, source selection, and the sole conditioned sound-file track list with per-track offset and length; do not add a nested native `SoundEvent.Sounds` playlist.
- Audio tracks and instruments both support live parameter conditions; master-track conditions gate the whole event.
- Parameter conditions and automation currently evaluate at `Start()`.
- Add validation and focused tests for every serialized capability.
- Do not recreate world facades, emitter schemas, behavior profiles, archetypes, or a bespoke audio editor.
- Run `dotnet test Tests\Survive.Tests\survive.tests.csproj --filter FullyQualifiedName~AudioEventTests` after changes.
