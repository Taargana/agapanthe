# Board — Audio-1: minimal one-shot playback

Spec: `docs/plans/2026-09-21-audio-1-design.md` (approved 4.72/5 after 3 review rounds — round 1
found a real AppHost plumbing error, round 2 found two testability gaps in the leak-tracking and
forced-degraded-path mechanisms, both closed and re-verified against real code).

## Rollback Point

`ba01746d730b00e00f6bd8ef10c04ecc586193a2` (Job-2, committed + pushed, tree clean before this
board's Wave 1 touches anything).

## Project Conventions (detected)

- .NET 10 solution (`Agapanthe.slnx`), xUnit tests in `tests/Agapanthe.Tests`, `dotnet test`/
  `dotnet build` (0 warnings, `TreatWarningsAsErrors`).
- New project pattern (MP-0a precedent): `.csproj` + `<Project Path=... />` entry in
  `Agapanthe.slnx` + a new `[InlineData]`/allowlist-row edit in
  `tests/Agapanthe.Tests/EngineIsHeadlessTests.cs`.
- Commits/pushes only on explicit user request.
- Double audit (`csharp-lowlevel` + `engine-architect`) before closing — OpenAL is a new native
  interop surface, closer to `csharp-lowlevel`'s remit than a new rendering technique (no
  `graphics-3d` deviation needed, per spec).

## Scope (from spec)

**In scope**: new `src/Agapanthe.Audio` (`WavFormat`, `AudioDevice`, `AudioClip`, `AudioLoader`),
never referenced by `Agapanthe.Engine`/`World`/`HeadlessSim`/`DedicatedServer`; a method-level
`audioDevice` local in `AppHost.RunClient` (mirrors `device`/`swapchain`/`renderer` — no
`PresentationSceneContext.Audio` field); `Key.J` demo (Sandbox + TopDown) playing a synthesized
sine tone; `AudioDevice.ReportLeaks()` ANDed into `AppHost`'s existing `clean` exit-code flag;
`AGAPANTHE_AUDIO=0` forcing the degraded path AFTER a real `alcOpenDevice` attempt (never a
short-circuit).

**Explicitly out of scope**: 3D/positional audio, streaming/music, mixing/buses, multi-channel,
compressed formats, a cooked `.agsound` format, any real gameplay-triggered sound beyond the manual
demo key.

## Task Graph

```
AUDIO-001 (WavFormat, new project) ──▶ AUDIO-002 (AudioDevice/Clip/Loader) ──▶ AUDIO-003 (AppHost wiring)
                                                                                       │
                                                                                       ▼
                                                                              AUDIO-004 (Self Code Review)
                                                                                       │
                                                                                       ▼
                                                                              AUDIO-005 (Req. Validation)
                                                                                       │
                                                                                       ▼
                                                                              AUDIO-006 (Full Verification)
```

Fully sequential — each task's files are a strict superset dependency of the previous (AUDIO-003
touches the shared `AppHost.cs`/`EngineIsHeadlessTests.cs`, safety-first serialization applies
regardless), and the domain is small enough that no genuine parallel-safe pair exists.

### Wave 1 — AUDIO-001: `Agapanthe.Audio` project scaffold + `WavFormat`

- **Type**: code+test · **Size**: S · **Deps**: none
- New `src/Agapanthe.Audio/Agapanthe.Audio.csproj` (references `Agapanthe.Core` only,
  `PackageReference Silk.NET.OpenAL`), added to `Agapanthe.slnx`.
- `WavFormat.cs`: `WavData` record struct (`SampleRate`, `Channels`, `BitsPerSample`, `Pcm`),
  `Read(Stream)`/`Write(Stream, in WavData)` — pure, zero OpenAL dependency (fully headless
  testable, no device involved at all).
- `AudioException.cs`: thrown by `WavFormat.Read` on malformed input (mirrors
  `WorldSerializationException`/`AgModelFormat`'s own dedicated exception types).
- **Tests** (`tests/Agapanthe.Tests/WavFormatTests.cs`): round-trip a synthesized sine tone (mono,
  16-bit PCM, 44100 Hz) through `Write`→`Read`, assert every field + every PCM sample matches
  exactly; malformed-input rejection (truncated header, wrong magic bytes, a declared PCM length
  exceeding the actual stream length) each throws `AudioException`.
- **Acceptance**: `dotnet build` 0 warnings; new tests green; `WavFormat` has zero reference to
  Silk.NET.OpenAL types (grep-verifiable).

### Wave 2 — AUDIO-002: `AudioDevice`/`AudioClip`/`AudioLoader`

- **Type**: code+test · **Size**: M · **Deps**: AUDIO-001
- `AudioDevice.cs`: `Supported` bool; `TryCreate()` — always attempts the real
  `alcOpenDevice`/`alcCreateContext` path first, ANDs `AGAPANTHE_AUDIO=0` onto `Supported`
  AFTERWARD (never a short-circuit, spec D11) — never throws; fixed-size pre-allocated source pool
  (D9, no per-call allocation); `alGetError()` checked after every OpenAL call in Debug at minimum
  (D8); `Play(AudioClip)` — silent no-op if `!Supported`; TWO independent live counters (buffers,
  sources — spec D10, never merged); `ReportLeaks()` (`bool`, `true` = clean, mirrors
  `ResourceTracker.Report()`'s exact role); `Dispose()`.
- `AudioClip.cs`: opaque handle wrapping the uploaded OpenAL buffer id (mirrors `MeshHandle`),
  lifetime owned by the `AudioDevice` that created it.
- `AudioLoader.cs`: `Load(string path, AudioDevice device) -> AudioClip` — `WavFormat.Read` +
  upload to a new AL buffer via `device` (no upload/buffer creation at all if `!device.Supported` —
  the mechanism spec D10's stated limitation depends on).
- **Tests** (`tests/Agapanthe.Tests/AudioDeviceTests.cs`): `TryCreate` never throws on any machine;
  `Play` is a true no-op when unsupported (no exception, no allocation, no AL call attempted);
  0-alloc-after-warmup on `Play`; `ReportLeaks()` returns `true` after proper dispose (stated
  hardware-dependent caveat — trivially true with no real device, load-bearing only with one);
  `AGAPANTHE_AUDIO=0` forces `Supported = false` without altering whether the real open was
  attempted (assert via a way to observe the real attempt happened, e.g. a device that WOULD have
  been `Supported = true` without the env var still shows evidence the real path ran — exact
  mechanism left to implementation, but the test must not just check the final `Supported` value,
  which a short-circuit would also satisfy).
- **Acceptance**: `dotnet build` 0 warnings; new tests green; no test requires a specific machine
  capability to PASS (both hardware states are individually valid, per spec's stated limitation).

### Wave 3 — AUDIO-003: `AppHost` wiring + demo key + headless-allowlist update

- **Type**: code · **Size**: S · **Deps**: AUDIO-002
- `src/Agapanthe.App/Agapanthe.App.csproj`: add `ProjectReference` to `Agapanthe.Audio`.
- `src/Agapanthe.App/AppHost.cs`: new method-level `AudioDevice? audioDevice = null;` alongside
  `device`/`swapchain`/`renderer`/`registry` (mirrors their exact declaration site and lifecycle);
  assigned via `AudioDevice.TryCreate()` inside `window.Loaded`; a new `Key.J` case in the existing
  `window.KeyPressed` switch (sibling closure over `audioDevice`, exactly like `Key.F`/`G`/`H` close
  over `world`/`camera`) that loads (once, cached) a synthesized-tone `AudioClip` via `AudioLoader`
  and calls `audioDevice?.Play(clip)`; a new field on `TeardownTargets` + a new step in
  `BuildTeardown` disposing `audioDevice` and ANDing `ReportLeaks()` into the existing `clean` flag
  (same flag `ResourceTracker.Report()` feeds, `AppHost.cs:403,426`) — placed first in the teardown
  list (cheapest, most independent resource; each step's isolated try/catch means exact order has
  no correctness consequence, spec's stated reasoning).
- `tests/Agapanthe.Tests/EngineIsHeadlessTests.cs`: edit `Agapanthe.App`'s existing exact-match
  `[InlineData]` array (lines ~63-66) to include `"Agapanthe.Audio"` — NOT a new dedicated row
  (`Agapanthe.Audio` guards no closure of its own).
- **Tests**: an integration test (or a live-run assertion) that a deliberately-leaked AL buffer
  flips `AppHost`'s process exit code — mirrors the existing Vulkan leak-gate test shape.
- **Acceptance**: `dotnet build` 0 warnings; `EngineIsHeadlessTests` green; `Key.J` triggers
  `audioDevice?.Play(clip)` with no crash whether or not a real device is present.

### Wave 4 — AUDIO-004: Self Code Review (tail, mandatory)

- **Type**: code · **Size**: S · **Deps**: AUDIO-003
- Confirm `TryCreate` genuinely attempts the real AL open path before ANDing
  `AGAPANTHE_AUDIO=0` (no short-circuit slipped in during implementation); confirm `ReportLeaks()`
  tracks two INDEPENDENT counters, not one merged; confirm `ReportLeaks()` is ANDed into `clean` and
  actually affects the exit code (not just logged); confirm no `PresentationSceneContext.Audio`
  field was introduced; confirm `Agapanthe.Audio` is never referenced by `Agapanthe.Engine`/`World`
  (grep `ProjectReference` across `samples/HeadlessSim`/`samples/DedicatedServer` too — must show
  no `Agapanthe.Audio` anywhere in their closures); confirm `WavFormat` remains AL-free.

### Wave 5 — AUDIO-005: Requirements Validation (tail, mandatory)

- **Type**: code · **Size**: S · **Deps**: AUDIO-004
- Walk the spec's Out-of-scope section line by line: confirm no 3D/positional audio, no
  streaming/music, no mixing/buses/ducking, no multi-channel/compressed formats, no `.agsound`
  cook format/`AssetKey` integration, no real gameplay-triggered sound crept in beyond the manual
  `Key.J` demo.

### Wave 6 — AUDIO-006: Full Project Verification (tail, mandatory)

- **Type**: code · **Size**: M · **Deps**: AUDIO-005
- `dotnet build` 0 warnings; `dotnet test` full suite green; regression gate — all 9 pinned
  Sandbox/TopDown captures + `HeadlessSim`'s snapshot hash re-verified byte-identical (expected
  trivially unaffected: zero touch on `Engine`/`World`/`Rendering`/`Graphics`); AOT publish + JIT ==
  NativeAOT on `Sandbox`/`TopDown` (the only hosts referencing `Agapanthe.Audio`), explicitly
  including the `AGAPANTHE_AUDIO=0` degraded path, not only whichever the publish machine happens
  to have; live `Key.J` demo (Sandbox + TopDown) with human/ear verdict.
- Dispatch the double audit (`csharp-lowlevel` + `engine-architect`), apply findings, re-verify
  anything the findings touch.

## Wave 6 results — CLOSED (after a real fix pass, not a rubber-stamp)

`dotnet build` 0 warnings; `dotnet test` 1014/1014 green; regression gate held (JIT and NativeAOT
captures both byte-identical to the pre-Audio-1 baseline, `9a010fc3...`, confirmed both before and
after the audit-fix pass); NativeAOT publish confirmed working end-to-end including the
`AGAPANTHE_AUDIO=0` degraded path.

**Double audit `csharp-lowlevel` + `engine-architect` — FIRST PASS VERDICT: FAIL.** Not a
PASS-with-concerns — a real, empirically-confirmed 🔴: `AudioDevice.ReportLeaks()` could never
report a leak (`Dispose()` cleared its tracking lists unconditionally, so the "gate" always read
clean), AND `Dispose()` itself genuinely leaked every buffer that had ever been played (it deleted
buffers before sources/detaching them — OpenAL refuses `DeleteBuffer` on a buffer still attached to
a source). Both auditors independently converged on the same root defect. Confirmed by mutation:
reverting the fix reproduces `ReportLeaks_IsCleanAfterProperDisposal` going red.

**Fixed**: `Dispose()` now stops each source, detaches its buffer (`SetSourceProperty(Buffer, 0)`),
deletes sources, THEN deletes buffers — and verifies each delete with `AL.IsSource`/`AL.IsBuffer`
(always-on, not Debug-only — this check IS the leak gate). `ReportLeaks()` now reflects that
verification (`!_teardownFailed`) instead of a cleared-count tautology.

**4 more 🟠 fixed**: `UploadClip` now tracks a buffer id immediately after `GenBuffer` (before it
could be lost to an upload error) and deletes it on failure instead of orphaning it · `TryCreate`
now tracks partially-created native resources in locals and tears them down on any exception path
(previously leaked device/context/sources on a throw after context creation succeeded), and its
one-time init-error check is now always-on · a process-wide single-instance guard closes a real
interaction two of this file's own tests reproduced (`alcMakeContextCurrent` is process-global —
a second live `AudioDevice` was silently stealing the context from the first) · `audioClean` in
`AppHost` now starts `false` and is only set `true` after `Dispose()`+`ReportLeaks()` both actually
succeed (previously started `true`, so a thrown teardown step incorrectly reported a clean exit) ·
`WavFormat.Read` now bounds an allocation against the stream's actual remaining length before
allocating (a 44-byte forged file could previously trigger a 512 MiB allocation).

**3 more 🟠 fixed (architecture)**: `AGAPANTHE_AUDIO` moved out of `Agapanthe.Audio` entirely into
`HostOptions.AudioEnabled` (closes an S30 invariant violation — no path was supposed to read the
environment outside `HostOptions`; `AudioDevice.TryCreate` now takes a plain `bool enabled`) ·
`Silk.NET.OpenAL.Soft.Native` referenced (native `soft_oal.dll`/`libopenal.so`/`.dylib` now ship
for every RID, mirroring GLFW/shaderc's own convention — previously only worked via a
system-installed OpenAL, invisibly machine-dependent) + a status log line on `TryCreate` (was
completely silent) · the demo's synthesize-to-temp-file-then-reload dance replaced by a new public
`AudioLoader.Load(Stream, AudioDevice)` overload — no temp file, no shared/uncleaned path, no
`static` mutable field on `AppHost`; the sine-tone generator is now a single `WavFormat.SineTone`
used by the demo AND both test files (closes D6's claim, which an audit found was actually
duplicated three ways).

**2 new gates added** (audit finding — `Agapanthe.Audio` had the same "no Silk.NET.OpenAL type
leaves the assembly" property as `Agapanthe.Graphics`'s "no Vk\* leaves" invariant, but no test
pinned it): `AudioAssembly_ExposesNoSilkNetType`, `AudioProjectFile_CarriesOnlyTheAllowedPackageReferences`
(mirrors the `Agapanthe.Net` precedent) — plus `Agapanthe.Audio` added explicitly to
`EngineIsHeadlessTests`' `ForbiddenAssemblies` (previously only incidentally caught via the
`Silk.NET` name-prefix check).

**Live/ear verdict: PASS** (user-confirmed) — `Key.J` in Sandbox produces an audible beep.

**Debt deliberately deferred to Audio-2** (both audits agree, documented not dropped):
`AudioClip` has no individual release path — every buffer lives until the whole device is disposed
(fine for one demo clip, a real problem the moment scene reloads accumulate them; needs a
generation-tagged handle + `AudioDevice.Release`, mirroring `MeshHandle`) · nothing stops a future
`Stage.Simulation` `ISystem` in a client assembly from calling `AudioDevice.Play` directly — the
headless split is enforced at the ASSEMBLY level (Engine/World can't name `AudioDevice`) but not at
the SYSTEM level; Audio-2 should route through simulation-emitted cues consumed by a presentation-
side system, never called from `Stage.Simulation` itself, and add an `AssertOwnerThread` to
`Play`/`UploadClip` for consistency with `GameWorld`/`SimCommandQueue` · positional-audio prep notes
(mono-only spatialization, voice handles instead of `void Play`, `Vector3` camera-relative
positions never raw `Double3`) · a shared `ILeakReporter`-style vocabulary across Graphics'
`ResourceTracker`/Audio's `ReportLeaks`/SystemScheduler's disposal, before a 3rd native-resource
domain arrives.

## Deferred Work (out of scope, not dropped)

Per spec: 3D/positional audio (Audio-2, explicitly anticipated), streaming/music playback, mixing/
buses/volume groups/ducking, multi-channel/compressed-format sources, a cooked `.agsound` format +
`AssetKey`/`AssetCatalog` integration, real gameplay-triggered sound cues (spawn/save events).
