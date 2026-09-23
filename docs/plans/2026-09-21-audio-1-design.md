# Audio — sub-milestone 1: minimal one-shot playback (Audio-1)

## Summary

Backlog §4quater's "Audio" item (VS-5, twice paused with no regret — "repris quand un jeu-échantillon
le tire"). This is a genuinely greenfield domain: the repository has zero audio code, zero audio
packages, zero prior design. Decomposed like every other §4quater domain into gated sub-milestones
(MP-0/Contenu/Job/Net precedent) — **this is sub-milestone 1**: the smallest real vertical slice
that proves the mechanism (device lifecycle, asset decode, one-shot trigger), deliberately excluding
3D/positional audio, streaming/music, and mixing — all real, deferred work, not gaps.

## Decisions

- **D1**: Library is **Silk.NET.OpenAL** — an OS/driver-facing binding, the same bucket as
  Silk.NET.Vulkan/GLFW under the project's locked convention ("Bindings Silk.NET [Vulkan + GLFW +
  input] ; le reste from scratch"). Not hand-rolled (WASAPI/CoreAudio) — that would contradict the
  convention Vulkan/GLFW themselves follow.
- **D2**: Scope is **one-shot 2D playback only** — mono, 16-bit PCM, 44100 Hz. No 3D/positional
  audio (no listener/source spatialization), no streaming/music, no mixing/buses, no multi-channel,
  no other bit depths/sample rates. Smallest complete vertical slice first, mirroring every other
  §4quater domain's own decomposition (MP-0a headless split before identity; Contenu-1 identity
  before cook; Net-1 thin-client loop before multi-client; Job-1 wave grouping before fine-grained
  resources).
- **D3**: New **`src/Agapanthe.Audio`** project — never referenced by `Agapanthe.Engine`/`World`
  (mirrors `Agapanthe.Graphics`'s own placement: a dedicated project referenced only by `App`/client
  hosts). A `DedicatedServer` never needs to play a sound, exactly as it never needs a GPU.
  `EngineIsHeadlessTests`' existing `Agapanthe.App` allowlist row is updated to include it (see
  Testing strategy for the precise mechanics — an edit to an existing exact-match row, not a new
  dedicated row, since `Agapanthe.Audio` guards no closure of its own).
- **D4**: **No cook step** — `AudioLoader.Load(path, device)` decodes a `.wav` file directly at
  runtime, mirroring the existing `HdrImageLoader.Load(path)` precedent (a runtime-decode loader
  that bypasses `AssetKey`/`AssetCatalog`/the manifest entirely — already shipped, already the
  established alternative to cooking for a decode this trivial). No concrete need for streaming,
  compressed formats, or cook-time metadata exists yet to justify a `.agsound` format.
- **D5**: **Load-once, play-by-handle** — `AudioLoader.Load` decodes and uploads PCM to an OpenAL
  buffer exactly once, returning an `AudioClip`; `AudioDevice.Play(AudioClip)` triggers a pooled
  OpenAL source. Mirrors `ResourceRegistry`'s own load-once/handle-based pattern (`MeshHandle`/
  `MaterialHandle`) — avoids re-decoding a file on every trigger.
- **D6**: Test/demo content is a **synthesized sine tone**, generated in code — no binary asset is
  checked into the repository. Both the unit tests and the interactive demo use the same generator.
  The WAV reader/writer is still genuinely exercised (round-trip a real `.wav` byte stream), just
  without depending on an external file's provenance/licensing/size.
- **D7 (confirmed with the user, diverging from the initial recommendation)**: `AppHost` opens an
  OpenAL device **unconditionally** at bootstrap, the same posture as `GraphicsDevice` — not
  lazily-on-first-use. **With an explicit graceful-degradation requirement**: device creation must
  never crash the host. `AudioDevice.TryCreate()` returns a device with `Supported = false` on any
  failure (no hardware, no driver, OpenAL runtime absent) rather than throwing — mirrors the
  already-shipped `GraphicsDevice.SupportsGpuTimestamps`/`Renderer.GpuPassTimingsMs` pattern from
  UI-3 (capability detection + clean fallback, never a crash). `Play(clip)` on an unsupported device
  is a silent no-op — no per-frame log spam, no repeated failure noise.
  **Plumbing, corrected post-review**: `GraphicsDevice` itself is NOT a `PresentationSceneContext`
  field — it is a method-level nullable local in `AppHost.RunClient`
  (`GraphicsDevice? device = null;`, `AppHost.cs:52`), assigned inside the `window.Loaded += () => {...}`
  closure (`:75`) and captured by the `window.KeyPressed += key => {...}` closure (`:199`) as a
  **sibling** closure over the same method-level variable — `Key.F`/`G`/`H`'s existing demos
  (`:250-283`) already work exactly this way (they close over method-level `world`/`camera`, never
  over a `PresentationSceneContext`, which only exists inside `Loaded` and is not visible to
  `KeyPressed` at all). Audio's device follows the SAME shape for consistency: a method-level
  `AudioDevice? audioDevice = null;` alongside `device`/`swapchain`/`renderer`, assigned once in
  `Loaded`, and `Key.J`'s handler calls `audioDevice?.Play(clip)` directly — no
  `PresentationSceneContext.Audio` field is introduced (Audio-1 has no recipe-facing consumer at
  all per Out-of-scope: no gameplay-triggered sound yet, only the manual demo key).
- **D8**: An **AL error-checking discipline** mirrors the project's locked Vulkan rule ("tout
  message de validation layer = bug"): every OpenAL call is followed by `alGetError()` in Debug (at
  minimum), and a genuine AL error (as opposed to "no device present," which D7 already handles
  separately) is a bug to surface loudly, not swallow.
- **D9**: A small, **fixed-size pre-allocated source pool** (no per-call allocation, no growth) —
  mirrors this codebase's established pre-grown-buffer discipline (`SimCommandQueue`'s backing
  array, `PersistentInstanceBuffer`). Exact size is an implementation detail (a handful of
  concurrent one-shot sources is enough for this sub-milestone's scope — no demo scenario plays
  more than one sound at a time).
- **D10 (added post-review round 1, tightened round 2)**: `AudioDevice` tracks two INDEPENDENT live
  counts — buffers (grown by `AudioLoader.Load`, one per clip) and sources (the fixed pool, D9) —
  not one merged counter (a merged counter could let an off-by-N error in one category cancel
  against the other and still read zero). Mirrors the project's Vulkan-side `ResourceTracker`/
  "0-leak" gate (`Agapanthe.Graphics`, unreachable from `Agapanthe.Audio` by design, D3) in SHAPE,
  not just in spirit: `AudioDevice` exposes `bool ReportLeaks()` (mirrors `ResourceTracker.Report()`'s
  exact signature/role — verified: `AppHost.cs:426`, `clean = ResourceTracker.Report();`, and that
  `clean` bool directly drives the process exit code), called from `AudioDevice`'s own teardown step
  and ANDed into the SAME `clean` flag the GPU teardown steps already feed — a Release-mode audio
  leak fails CI exactly as a Vulkan leak does, not merely a log line (round-2 review finding: the
  first draft of D10 said only "asserts," leaving Debug-vs-Release and exit-code wiring unspecified
  — a real gap given this project's explicit "0-leak gate... jamais ignoré ni contourné" rule for
  Vulkan, which would have been quietly weaker for audio without this).
  **Known, stated limitation (parallel to D11's own, not new)**: when `Supported = false` (no real
  device — the common case on headless CI, see D11), `AudioLoader.Load`/`Play` are no-ops that
  allocate no real AL buffer/source at all, so `ReportLeaks()` trivially reports zero without
  exercising any actual cleanup logic. This test is therefore only load-bearing on a machine with a
  real (or software-fallback) OpenAL device — the same "forced-off path is deterministic, forced-on
  path depends on real hardware" asymmetry D11 already accepts explicitly, not a new hole.
- **D11 (added post-review round 1, disambiguated round 2)**: `AGAPANTHE_AUDIO=0` (mirrors
  `AGAPANTHE_GPU_TIMESTAMPS=0`'s existing MECHANISM, not just its name) ANDs a caller override onto
  a REAL capability probe — `TryCreate` always attempts the genuine `alcOpenDevice`/
  `alcCreateContext` path first (exactly as `GraphicsDevice`'s own timestamp-support detection
  always runs for real before `HostOptions.GpuTimestampsEnabled` is ANDed onto it), and
  `AGAPANTHE_AUDIO=0` only forces `Supported = false` AFTERWARD. It never short-circuits before
  touching AL. This is the round-2 review's specific ask: a short-circuit would make the
  "`TryCreate` never throws" test exercise only a trivial early return, proving nothing about real
  AL failure handling — ANDing after a real attempt keeps that test meaningful on every machine
  (the real open either succeeds or is exercised-and-then-overridden), while still giving the
  `Play`-is-a-no-op test a deterministic, hardware-independent way to reach the degraded branch.

## Architecture

### `src/Agapanthe.Audio` — new project

```
Agapanthe.Audio
├── WavFormat.cs      — pure WAV read/write, NO OpenAL dependency, fully testable headless
├── AudioDevice.cs    — TryCreate/Dispose, source pool, AL error-check helper
├── AudioClip.cs      — decoded PCM + uploaded AL buffer handle (readonly struct/small class)
└── AudioLoader.cs    — Load(path, device) -> AudioClip (WavFormat.Read + device upload)
```

Referenced only by `Agapanthe.App` (and transitively `Agapanthe.Platform.App`, `samples/Sandbox`,
`samples/TopDown`, `samples/ThinClient`) — never by `Agapanthe.Engine`, `Agapanthe.World`, or
`samples/HeadlessSim`/`samples/DedicatedServer`.

**`WavFormat`** (pure, GPU/AL-free — the piece that's testable without any device, mirroring
`AgModelFormat`/`AgFontFormat`'s own "reader is pure" discipline):
```csharp
public static class WavFormat
{
    public readonly record struct WavData(int SampleRate, short Channels, short BitsPerSample, byte[] Pcm);

    public static WavData Read(Stream stream); // throws AudioException on malformed input
    public static void Write(Stream stream, in WavData data);
}
```

**`AudioDevice`**:
```csharp
public sealed class AudioDevice : IDisposable
{
    public bool Supported { get; }

    // Always attempts the real alcOpenDevice/alcCreateContext path first; AGAPANTHE_AUDIO=0 (D11)
    // ANDs Supported to false AFTERWARD — never a short-circuit before touching AL.
    public static AudioDevice TryCreate(); // never throws — Supported=false on any failure

    public void Play(AudioClip clip); // silent no-op if !Supported

    // Two independent live counts (buffers, sources) — mirrors ResourceTracker.Report()'s role;
    // AppHost's teardown step ANDs this into the same `clean` flag the GPU steps already feed.
    public bool ReportLeaks(); // true = clean

    public void Dispose();
}
```

**`AudioClip`** — an opaque handle-like type (mirrors `MeshHandle`) wrapping the uploaded OpenAL
buffer id; disposal/lifetime owned by the `AudioDevice` that created it (mirrors `ResourceRegistry`
owning GPU resources, not the caller).

**Plumbing in `AppHost.RunClient`** (corrected post-review — see D7): a new method-level
`AudioDevice? audioDevice = null;` alongside the existing `device`/`swapchain`/`renderer`/
`registry` locals (`AppHost.cs:52-58`), assigned via `AudioDevice.TryCreate()` inside the
`window.Loaded += () => {...}` closure (`:72-`, same place `device`/`renderer`/`registry` are
constructed). **No `PresentationSceneContext.Audio` field** — Audio-1 has no recipe-facing
consumer (Out-of-scope: no gameplay-triggered sound), so there is nothing for a scene's `Build` to
need it for; introducing the field now would be speculative surface with no caller. `Key.J`'s
handler (a sibling closure over the same method-level `audioDevice`, exactly like `Key.F`/`G`/`H`
close over method-level `world`/`camera`, `:250-283`) calls `audioDevice?.Play(clip)` directly.
Teardown: a new field on `TeardownTargets` (`AppHost.cs:570`) and a new step in `BuildTeardown`
(`:508`) disposing `audioDevice` — each teardown step already runs in its own isolated `try/catch`
(`:435-445`), so exact ordering relative to the 9 GPU steps has no correctness consequence; placed
first in the list for simplicity (cheapest, most independent resource, nothing else depends on it
being alive or dead).

**Demo wiring**: a free key (`Key.J` — `F`/`G`/`H` are taken by the raycast/overlap query demos) in
`AppHost`'s existing `KeyPressed` switch, same posture as those three: loads (once, cached) a
synthesized-tone `AudioClip` and calls `audioDevice?.Play(clip)`.

## Testing strategy

- **`WavFormat` round-trip**: generate a sine tone (mono, 16-bit PCM, 44100 Hz) in a test helper,
  `Write` it to a `MemoryStream`, `Read` it back, assert every field and every PCM sample matches
  exactly. No file I/O, no OpenAL, no device — pure and fast.
- **`WavFormat` malformed-input rejection**: truncated header, wrong magic bytes, a declared PCM
  length exceeding the actual stream length — each throws `AudioException` (mirrors the project's
  established posture on malformed cooked-format input: `AgModelFormat`/`AgSceneFormat`'s own
  bounds-checked `ReadCount`).
- **`AudioDevice.TryCreate` never throws**, on any machine.
- **The degraded path is deterministically forced, not left to chance** (spec-review finding —
  "whichever is true of the test machine" is close to a vacuous test on its own): `AGAPANTHE_AUDIO=0`
  (D11) forces `Supported = false` regardless of real hardware, so `Play`-is-a-silent-no-op is
  exercised by every CI run unconditionally, not only on a machine that happens to lack audio
  hardware.
- **Stated, accepted limitation, not hidden**: the "device opens successfully and a source
  genuinely plays" path can only be exercised on a machine with real (or software-fallback) OpenAL
  support — there is no forced-success seam, mirroring how a genuinely GPU-less machine can't be
  forced to fake Vulkan support either. This is why the live/interactive human verdict below exists
  — it is this path's actual gate, same relationship `AGAPANTHE_GPU_TIMESTAMPS=0`'s forced-off path
  has to `GpuPassTimingsMs`'s real numbers only being checked by the visual/human protocol.
- **`Play` is a true no-op when unsupported**: no exception, no allocation, no AL call attempted.
- **0-alloc-after-warmup** on `Play` (mirrors every other 0-alloc gate in this codebase).
- **`AudioDevice.ReportLeaks()` returns `true` (clean) after proper disposal** (D10): on a machine
  with a real/software OpenAL device, create several clips (grows the buffer counter), play them
  (exercises the source pool), dispose everything, assert `ReportLeaks() == true` and both internal
  counters are independently zero. Explicitly a hardware-dependent test (see D10's stated
  limitation) — on `Supported = false`, this test still runs and still passes, but trivially (no
  real allocation happened), exactly like `AGAPANTHE_GPU_TIMESTAMPS=0` doesn't exercise real GPU
  timestamp math either. Not a gap introduced here — the same accepted asymmetry as D11.
- **`AppHost`'s exit code reflects an audio leak** (D10): `AudioDevice.ReportLeaks()` is ANDed into
  the same `clean` flag `ResourceTracker.Report()` already feeds (`AppHost.cs:403,426`) — an
  integration test (or a live run) confirms a deliberately-leaked AL buffer flips the process exit
  code, mirroring the existing Vulkan leak-gate test shape.
- **Live/interactive verification** (human verdict, no automated equivalent — audio has no visual
  capture): `Key.J` in Sandbox (and TopDown) actually produces an audible beep. This is this
  sub-milestone's equivalent of a "visual verdict PASS" — heard, not seen.

**Regression gate**: this sub-milestone touches zero files in `Agapanthe.Engine`/`Agapanthe.World`/
`Agapanthe.Rendering`/`Agapanthe.Graphics` — all 9 pinned captures and `HeadlessSim`'s snapshot hash
are expected unaffected by construction; verify anyway. Full `dotnet test` green, `dotnet build` 0
warnings, AOT publish + JIT == NativeAOT re-confirmed on `Sandbox`/`TopDown` (the only hosts that
now reference `Agapanthe.Audio`) — including the `AGAPANTHE_AUDIO=0` degraded path explicitly, not
only whichever the publish machine happens to have.

**`EngineIsHeadlessTests` note (corrected post-review)**: `Agapanthe.App`'s existing
`[InlineData]` row (`tests/Agapanthe.Tests/EngineIsHeadlessTests.cs:63-66`) is an exact-match
allowlist (`Assert.Equal` against a sorted array) — adding `Agapanthe.Audio` as a `ProjectReference`
on `App.csproj` means **editing that row's existing expected array** to include
`"Agapanthe.Audio"`, not adding a new dedicated `[InlineData]` row (`Agapanthe.Audio` itself, with
no headless-closure concerns of its own since nothing in `Engine`/`World` ever references it, does
not need its own row the way `Agapanthe.Net`/`Agapanthe.Platform.App` earned one at their own
milestones — those guard THEIR OWN closures; this only needs App's row to stay accurate).

**Double audit**: `csharp-lowlevel` + `engine-architect` — same pairing as every other non-Vulkan-
surface sub-milestone (OpenAL is a new native interop surface, closer to `csharp-lowlevel`'s remit
than a new rendering technique).

## Out of scope

- 3D/positional audio (spatialized source/listener, camera-relative double-precision position) —
  the natural Audio-2, explicitly anticipated but not built here.
- Streaming/music playback (looping tracks, crossfade, buffered streaming decode).
- Mixing, buses, volume groups, ducking.
- Multi-channel (stereo/surround) sources, compressed formats (Vorbis/Opus), any bit depth/sample
  rate beyond mono 16-bit 44100 Hz.
- A cooked `.agsound` format / `AssetKey`/`AssetCatalog` integration (D4) — revisit if/when a
  concrete need appears (streaming, cook-time loudness normalization, compressed formats).
- Any gameplay-triggered sound (spawn/save cues, as originally floated as a VS-3 stretch idea) —
  this sub-milestone proves the mechanism via a manual demo key, not a real gameplay integration.

## Verification (end-to-end)

`dotnet build` 0 warnings; `dotnet test` green (new `WavFormat`/`AudioDevice` tests); live `Key.J`
demo (Sandbox + TopDown) with human/ear verdict; all 9 pinned captures + `HeadlessSim` snapshot hash
byte-identical (expected, zero touch on rendering/simulation code); AOT publish + JIT == NativeAOT
on Sandbox/TopDown; double audit `csharp-lowlevel` + `engine-architect`, apply findings.

Once approved: task board, then execute.

## Outcome (closed session 46)

Delivered as specified: D1-D11 all implemented as designed (D7/D10/D11 tightened during the audit-
fix pass, described below). New `src/Agapanthe.Audio` (`WavFormat`, `AudioDevice`, `AudioClip`,
`AudioLoader`, `AudioException`), `Key.J` demo in `AppHost` (Sandbox + TopDown), `HostOptions.AudioEnabled`.
1014 tests, 0 warning, 0 regression (zero touch on `Engine`/`World`/`Rendering`/`Graphics` — the
`model` capture stayed byte-identical throughout). AOT publish + JIT == NativeAOT confirmed, native
`Silk.NET.OpenAL.Soft.Native` runtime now ships for every RID. Live human/ear verdict: PASS —
`Key.J` produces an audible beep, confirmed by the user.

**The double audit's first pass returned FAIL, not PASS-with-concerns.** Both `csharp-lowlevel` and
`engine-architect` independently converged on the same root defect, confirmed empirically by
mutation: `AudioDevice.ReportLeaks()` could never report a leak — `Dispose()` unconditionally
cleared its tracking lists, so the gate always read clean — and the leak was real: `Dispose()`
deleted buffers before detaching/deleting sources, and OpenAL refuses to delete a buffer still
attached to a source (a source keeps its `AL_BUFFER` set after a one-shot finishes). Pressing
`Key.J` once and quitting leaked a real OpenAL buffer while the log said "clean" and the exit code
was 0 — the shipped test (`ReportLeaks_IsCleanAfterProperDisposal`: load, play, dispose) passed
without ever detecting it.

Fixed and re-verified by mutation (temporarily reverted the delete order, confirmed the test goes
red, restored the fix): `Dispose()` now stops and detaches each source (`SetSourceProperty(Buffer, 0)`)
before deleting sources, deletes sources before buffers, and verifies every delete with
`AL.IsSource`/`AL.IsBuffer` — always-on, not `[Conditional("DEBUG")]`, since this check IS the leak
gate itself, not a developer convenience. `ReportLeaks()` now reflects a genuine `_teardownFailed`
flag instead of a cleared counter.

Four more low-level 🟠 findings fixed: `UploadClip` could lose a buffer's native id if the upload
failed right after `GenBuffer` (now tracked immediately, deleted on failure); `TryCreate` could leak
the partially-created device/context/source-pool on any throw after context creation succeeded (now
tracked in locals and torn down in the catch); a process-wide single-instance guard was added —
`alcMakeContextCurrent` is process-global state, and a second live `AudioDevice` was silently
stealing the context from the first (reproduced by two of this sub-milestone's own tests); `AppHost`'s
`audioClean` flag started `true`, so a thrown teardown step incorrectly reported a clean exit — now
starts `false`, matching `clean`'s (GPU) own posture.

Three more architecture 🟠 findings fixed: `AGAPANTHE_AUDIO` was read inside `Agapanthe.Audio`
itself, violating the S30 invariant ("no path reads the environment outside `HostOptions`") — moved
to `HostOptions.AudioEnabled`, `AudioDevice.TryCreate` now takes a plain `bool`; no OpenAL runtime
was shipped at all — the sub-milestone only worked via a system-installed Creative driver on this
dev machine, silently — `Silk.NET.OpenAL.Soft.Native` now ships native binaries for every RID
(mirrors GLFW/shaderc) plus a startup status log line where there was previously total silence;
the demo's synthesize-tone-to-temp-file-then-reread dance revealed a real API gap — a new public
`AudioLoader.Load(Stream, AudioDevice)` overload removed the temp file entirely, and a single
`WavFormat.SineTone` generator now backs the demo and both test files (closing D6's claim, which an
audit found was actually duplicated three ways). Two new reflective gates were added (mirroring
`Agapanthe.Net`'s own precedent, absent from the first pass): `AudioAssembly_ExposesNoSilkNetType`,
`AudioProjectFile_CarriesOnlyTheAllowedPackageReferences`.

Debt deliberately deferred to Audio-2, documented not dropped: `AudioClip` has no individual release
path (every buffer lives until the whole device is disposed — fine for one demo clip, a real
problem once scene reloads accumulate them; needs a generation-tagged handle + `AudioDevice.Release`,
mirroring `MeshHandle`); the headless split is enforced only at the assembly level (Engine/World
cannot name `AudioDevice`) not at the system level — nothing stops a future client-assembly
`Stage.Simulation` `ISystem` from calling `Play` directly; Audio-2 should route through simulation-
emitted cues consumed by a presentation-side system, never a direct call from `Stage.Simulation`,
plus an `AssertOwnerThread` on `Play`/`UploadClip` for consistency with `GameWorld`/`SimCommandQueue`;
positional-audio prep notes (OpenAL only spatializes mono, `Play` will need to return a voice handle
instead of `void`, positions must be camera-relative `Vector3`, never raw `Double3`); a shared
leak-reporting vocabulary across Graphics' `ResourceTracker`/Audio's `ReportLeaks`/`SystemScheduler`'s
disposal, worth unifying before a third native-resource domain arrives, not urgent now.
