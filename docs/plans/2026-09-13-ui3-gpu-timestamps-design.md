# UI-3 — GPU timestamps + the `FrameProfiler` seam

## 1. Summary

Third and last of the "Texte & UI" backlog trio (UI-1 text rendering, UI-2 CPU profiler +
debug overlay, both closed session 25). UI-2 left one explicit piece of debt: **the
`FrameProfiler` seam**. GPU timings are asynchronous — under the project's double-buffered
`FramesInFlight = 2`, a command buffer's GPU work typically finishes and its results become
readable roughly 2 frames after it was submitted. `FrameStats`/`FrameSeries` (the CPU profiler's
data structures) are append-only circular buffers with no way to backfill a historical sample,
so bolting GPU timings onto them naively would either desync the CPU/GPU series or require
retrofitting a type that is currently simple, tested, and 0-alloc.

This milestone was pre-specced in detail in `docs/plans/2026-08-03-text-ui-design.md` §UI-3:
a Vulkan `QueryPool`, per-pass GPU instrumentation, a GPU series in the debug overlay, and
explicit capability detection with graceful degradation (GPU timestamp support varies by
platform/driver, and macOS/Linux have never been validated for this project). It was flagged
there as "abandonable" — if it turns out not to pan out, nothing else regresses.

This design keeps that scope exactly, and resolves the seam by **decoupling** the GPU series
from the CPU one entirely rather than retrofitting `FrameStats`.

## 2. Decision log (interview)

| # | Decision |
|---|---|
| D1 | Scope = exactly the session-25 pre-spec (`QueryPool` + per-pass GPU timings + overlay series + graceful degradation), no more, no less. |
| D2 | Instrument the **4 existing debug-label regions** in `Renderer.cs`: `Shadow` (already wraps the `shadow_cull.comp` dispatch *and* the shadow raster), `Scene` (already wraps `scene_cull.comp` *and* the scene raster, with `Skybox` nested inside), `Tonemap`, and `UI`. `UiRenderSystem` calls `Renderer.DrawUi` unconditionally every frame, but `DrawUi` itself early-returns *before* `PushDebugLabel(UiPassLabel)` when there is nothing to draw (empty quad list, or no font/pass resources loaded yet) — so the **region**, unlike the other three, can legitimately not execute in a given frame. §3.2/§3.3 handle this per-region, not as a blanket assumption. No new named regions — these boundaries already exist and already delineate exactly this work. |
| D3 | The GPU series is a **separate, decoupled** history. `FrameStats`/`FrameSeries` (the CPU profiler, tested and 0-alloc since UI-2) are **not** touched or retrofitted for backfill. |
| D4 | Host readback of query results is **non-blocking, and per-region**: each region's begin/end query pair carries its own availability check (`WITH_AVAILABILITY_BIT`, never `WAIT_BIT`) — there is no single bulk gate across all 8 queries. A region whose pair is not available (because its pass didn't run 2 frames ago — the UI case above — or, in principle, because the GPU genuinely hasn't finished yet) is skipped **individually**; the other three regions still populate normally that frame. This also means "not yet available" and "didn't run" are handled by the same, already-non-blocking mechanism — no separate bookkeeping needed for the UI case. |
| D5 | `AGAPANTHE_GPU_TIMESTAMPS=0` (surfaced as `HostOptions.GpuTimestampsEnabled`, default `true`, `"0"` disables — the `OverlayVisible` pattern, see §3.4) forces the unsupported code path regardless of hardware capability — the only way to exercise and prove the *explicitly-disabled* path in a session running on a GPU that *does* support timestamps. (This does **not** cover the *hardware-unsupported* branch — see §5's honest accounting of that gap.) |
| D6 | **Ownership**: `Renderer` owns the `QueryPool`, writes the timestamps, and reads back the previous (N-2) in-flight slot's results once per frame, exposing them through a `LastGpuPassTimingsMs` property of a new readonly struct type `GpuPassTimingsMs` whose **4 fields are each independently nullable** (not one bulk-nullable struct — see D4) — mirroring the existing `LastSceneDrawCalls`/`LastShadowDrawCalls` "Renderer exposes a `Last*` readout, `DebugOverlaySystem` just reads it" pattern. `DebugOverlaySystem` owns its **own** 4 history series (Shadow/Scene/Tonemap/UI) — no new cross-project `GpuFrameStats` type is introduced. |

## 3. Architecture

### 3.1 `Agapanthe.Graphics` — the new primitive

- **`QueryPool`** (new file): a mono-backend wrapper (no `Vk*` type crosses this project's
  boundary, per the project's standing rule) around a `VkQueryPool` of type
  `QUERY_TYPE_TIMESTAMP`, sized `2 (begin/end) × 4 (regions) × FramesInFlight (2) = 16` queries.
  Created once (device/renderer lifetime — a query pool is not swapchain-size-dependent, so it
  needs no `DeletionQueue` N+2 deferred-destroy dance, just a plain `IDisposable` lifetime like
  `GraphicsPipeline`).
- **`CommandList.WriteTimestamp(QueryPool pool, uint query, PipelineStage2 stage)`**: thin wrapper
  over `vkCmdWriteTimestamp2` (synchronization2 — the project's declared Vulkan baseline,
  consistent with every existing barrier in the codebase). `TOP_OF_PIPE` for a region's begin
  timestamp, `BOTTOM_OF_PIPE` for its end — the simplest, most portable choice, inclusive of
  everything recorded in between (barriers included).
- **`CommandList.ResetQueryPool(QueryPool pool, uint firstQuery, uint count)`**: wraps
  `vkCmdResetQueryPool` — Vulkan requires a query be reset before it is written again; this is a
  core 1.0 requirement, no extension needed.
- **`GraphicsDevice` capability detection**: at the same physical-device-selection call site as
  the existing `SupportsRequiredFeatures` check, read
  `PhysicalDeviceProperties.Limits.TimestampPeriod > 0` **and** the graphics queue family's
  `QueueFamilyProperties.TimestampValidBits > 0` — checking the specific queue actually used,
  not just the blanket `timestampComputeAndGraphics` device feature, matching the project's
  standing MoltenVK caution ("no mutable comparator, no imageCubeArray — check every feature at
  the first VUID"): some drivers report the per-queue bit as 0 even when the blanket feature
  reads true. Exposed as `GraphicsDevice.SupportsGpuTimestamps`. This is **ANDed** with
  `HostOptions.GpuTimestampsEnabled` (D5) at `Renderer` construction to produce the final
  `Renderer.SupportsGpuTimestamps`. When false: no `QueryPool` is ever created, `CommandList`
  never writes a timestamp, `Renderer.LastGpuPassTimingsMs` permanently reads its all-fields-null
  default (see §3.2 — the property itself is never null, only its 4 fields are) — zero extra
  cost, zero extra state, on the unsupported/disabled path.
- **Pure, testable math extracted**, following the project's established precedent of pulling
  host-side math into GPU-free, unit-tested statics (`MathHelpers`, `ShadowFit`, `Frustum` all
  follow this shape): a static `GpuTimestampMath.ToMilliseconds(ulong beginTicks, ulong endTicks,
  float timestampPeriodNs)` helper. Unit-tested for the ordinary case, a zero-duration case, and
  a `endTicks < beginTicks` wraparound guard (a hardware timer counter can wrap over long
  uptimes — the guard rejects/clamps rather than silently producing a negative millisecond value
  that would read as a suspiciously fast frame instead of a garbage sample).

### 3.2 `Agapanthe.Rendering` — `Renderer` owns the pool and the readout

- Each of the 4 regions gets one `WriteTimestamp(pool, beginIndex, TopOfPipe)` immediately after
  its existing `PushDebugLabel` call, and one `WriteTimestamp(pool, endIndex, BottomOfPipe)`
  immediately before that region's existing closing point (the end of `RecordShadowPass`,
  `RecordScenePass`, `RecordTonemapPass`, and `DrawUi` respectively — after each pass's last
  barrier, still inside the `using` debug-label scope).
- Each frame, before writing new timestamps into the current in-flight slot: reset that slot's 8
  queries, then **read back the OTHER slot's** (the one written 2 frames ago) results,
  non-blocking, **one region at a time** (D4): for each of the 4 regions independently, query
  both its begin and end timestamps with `WITH_AVAILABILITY_BIT`; only if both are available,
  compute that region's millisecond value via `GpuTimestampMath.ToMilliseconds` and store it.
  A region whose pair isn't available (never written this cycle — the skipped-UI case, or, in
  principle, genuinely not yet finished) leaves that one field `null`; the other regions are
  unaffected.
- New public surface on `Renderer`:
  ```csharp
  public readonly struct GpuPassTimingsMs
  {
      public float? Shadow { get; init; }
      public float? Scene { get; init; }
      public float? Tonemap { get; init; }
      public float? Ui { get; init; }
  }

  public GpuPassTimingsMs LastGpuPassTimingsMs { get; private set; }
  public bool SupportsGpuTimestamps { get; }
  ```
  `LastGpuPassTimingsMs` itself is never null — when `SupportsGpuTimestamps` is false it is
  simply always the all-`null`-fields default; when true, each field is independently populated
  or `null` per the per-region check above.

### 3.3 `Agapanthe.Engine.Render` — `DebugOverlaySystem` displays it

- No constructor change (`_renderer` is already held). Adds its own 4 `FrameSeries` fields
  (Shadow/Scene/Tonemap/UI), sized to the same capacity as the existing `FrameTimeMs`/
  `AllocatedBytes` series, for visual and retention consistency.
- In `Execute`: if `!_renderer.SupportsGpuTimestamps`, the entire GPU block (one text line + one
  `Sparkline`) is omitted — the panel shrinks by exactly that much (clean absence, matching the
  original DoD: "per-pass GPU milliseconds on screen where supported; clean absence where not").
  If supported: read `_renderer.LastGpuPassTimingsMs`; **each of the 4 fields is appended into
  its own `FrameSeries` independently** — a field that is `null` this frame (its region didn't
  run, or in principle wasn't ready) simply does not advance that one series, while the other
  3 still do. The summed-total `Sparkline` sums only the non-null fields present that frame
  (a `null` UI field, the common case when the overlay itself has nothing else queued, should
  not make the total silently vanish for one frame). One additional compact text line shows the
  per-pass breakdown (e.g. `gpu  shadow 0.42  scene 3.10  tonemap 0.18  ui —— ms`, a field's
  absence rendered as a placeholder rather than `0.00` — a real skipped region and a genuine
  zero-cost region must not read the same — exact formatting finalized during EXECUTE, following
  the existing `TextBuilder`/`stackalloc` 0-alloc pattern used by every other overlay line).
- `panelHeight`'s calculation grows by one `LineHeight` + one `GraphHeight` when GPU timestamps
  are supported and visible. This changes the overlay's rendered pixels whenever it is on, so
  every existing pinned `AGAPANTHE_CAPTURE_UI` hash is expected to change and must be re-pinned
  with a fresh human visual verdict — this is not a silent regression, it is the intended new
  content.

### 3.4 `Agapanthe.App` — the env knob

- `HostOptions.GpuTimestampsEnabled` (`bool`, default `true`), read via `FromEnvironment` matching
  the existing **`OverlayVisible`** flag's shape (`HostOptions.cs`: `read("AGAPANTHE_OVERLAY") is
  not "0"` — default-on, the literal string `"0"` disables), **not** `ShaderReloadTest`/
  `CullStats` (those default `false` and are enabled by the mere *presence* of any non-empty
  value — a different shape, not the one this flag needs). `AppHost` threads
  `GpuTimestampsEnabled` into `Renderer`'s construction, ANDed there with the hardware capability
  check (D6) to produce `Renderer.SupportsGpuTimestamps`.

## 4. Error handling

- **Unsupported hardware or explicitly disabled** (`SupportsGpuTimestamps == false`): no
  `QueryPool` allocated, `CommandList.WriteTimestamp` calls are skipped entirely (guarded at the
  `Renderer` call sites, not inside `CommandList` — `CommandList` stays a thin, unconditional
  wrapper), overlay omits the block. No exception path — this is the expected, designed-for
  steady state on unsupported platforms, not an error.
- **One region's result not available this frame** (D4 — either it didn't run, e.g. `UI` with an
  empty draw list, or in principle the GPU hasn't finished it yet): only that field of
  `LastGpuPassTimingsMs` is `null`; the overlay simply does not advance that one series that
  frame, independent of the other 3. Not logged (this is not an error condition — the UI case in
  particular is an everyday occurrence, not a rare one — and logging every occurrence would
  itself be a source of noise/allocation on a hot path).
- **Timer wraparound** (`endTicks < beginTicks` in `GpuTimestampMath.ToMilliseconds`): treated
  identically to "not available" — the caller receives a sentinel (e.g. a `false`-returning
  `TryToMilliseconds`, or a documented `NaN`/negative-rejection contract finalized during
  EXECUTE with its own test) rather than a fabricated negative millisecond value.

## 5. Testing strategy

- **GPU-free, unit-tested**: `GpuTimestampMath.ToMilliseconds` — ordinary case, zero-duration
  case, wraparound-guard case. Mirrors the `ReversedZProjectionTests`/`ShadowFitTests` precedent
  of testing the pure host-side math a GPU feature depends on, independent of any Vulkan device.
- **Not unit-testable** (confirmed: no test anywhere in the suite constructs a real
  `GraphicsDevice`): `QueryPool` lifecycle, the write/reset/readback sequencing, capability
  detection, `Renderer.LastGpuPassTimingsMs` end-to-end, and `DebugOverlaySystem`'s new graph/
  line. Verified only via live capture and the double audit below.
- **Honest gap, not silently accepted**: `GraphicsDevice.SupportsGpuTimestamps`'s actual
  capability read (`TimestampPeriod > 0 && TimestampValidBits > 0`) ships with **zero test
  coverage of the branch where hardware genuinely lacks support** — this session's GPU supports
  timestamps, so that branch cannot be exercised live either, and `AGAPANTHE_GPU_TIMESTAMPS=0`
  (D5) only proves the *explicitly-disabled* path, not the *hardware-unsupported* one (they take
  different code paths that happen to converge on the same `SupportsGpuTimestamps == false`
  outcome — the env-var short-circuit never reaches the capability read at all). This is a
  bounded, accepted risk consistent with the project's existing macOS/Linux-never-validated
  status, not a new one introduced here — but it is called out explicitly rather than implied to
  be covered by the disabled-path test.
- **Live verification**:
  - `AGAPANTHE_CAPTURE_UI=<path> AGAPANTHE_MAX_FRAMES=2` with the overlay visible, on a scene
    with an already-pinned overlay capture hash — new hash pinned (content changed, expected),
    human visual verdict that the new GPU line/graph is present and legible.
  - Same, with `AGAPANTHE_GPU_TIMESTAMPS=0` — capture shows the panel **without** the GPU
    block, proving the explicitly-disabled path degrades cleanly rather than crashing or
    showing garbage. 0 leak / 0 validation on both.
  - **0-alloc regression check**: with GPU timestamps enabled, the pre-existing CPU allocation
    line/graph (UI-2, already on screen) must stay at its steady-state 0 B/frame. The new
    per-frame host readback (`vkGetQueryPoolResults` marshalling, 4 independent availability
    checks) is exactly the shape of code prone to a hidden allocation, and this project's 0-alloc
    gate is blocking — this is the existing continuous gate, not a new mechanism, but it must be
    explicitly watched for this milestone rather than assumed to still hold.
  - Every existing non-UI capture (all 8 Sandbox scenes, `topdown`) re-verified byte-identical —
    this milestone touches `Renderer`/`CommandList`, both shared by every scene, and must not
    change a single HDR/tonemap pixel.
  - AOT publish, `JIT == NativeAOT` on the overlay captures (no new native dependency is
    introduced — `QueryPool` is pure Vulkan API surface already linked).
- **Double audit**: `csharp-lowlevel` + **`graphics-3d`** — the session-25 spec's own explicit,
  documented deviation from the project-standard `csharp-lowlevel` + `engine-architect` pairing,
  because this work is squarely new Vulkan query-pool and synchronization2 territory.

## 6. Migration path

Single-phase milestone (unlike Contenu-3c's gated sub-phases or Slice-2's 6 waves — this is one
cohesive, already-tightly-scoped unit of work). Waves, in dependency order: (1) `QueryPool` +
`CommandList` timestamp/reset wrappers + `GpuTimestampMath` + capability detection + tests,
(2) `Renderer` instrumentation of the 4 regions + readback + `LastGpuPassTimingsMs`,
(3) `HostOptions.GpuTimestampsEnabled` + `AppHost` wiring, (4) `DebugOverlaySystem` display,
(5) verify + mandatory tail + double audit + converge.

## 7. Out of scope (deferred, not silently dropped)

- Separate timestamp regions for the `shadow_cull.comp`/`scene_cull.comp` compute dispatches —
  bundled into their enclosing `Shadow`/`Scene` region instead (D2).
- A separate timestamp pair for the nested `Skybox` sub-region — bundled into `Scene`.
- Any GPU-timing coverage of the one-time IBL-generation compute passes — those are not
  per-frame work and are outside this per-frame overlay's scope entirely.
- Retrofitting `FrameSeries` for historical backfill (D3 explicitly rejected this in favor of a
  decoupled series).

## 8. Outcome (2026-09-13, session 39)

**Shipped as designed**, D1-D6 all held, with 3 real correctness bugs (2 🔴 found independently
by both auditors, 1 🔴 found by `graphics-3d` alone) fixed before closure — none of them visible
in the automated verification that ran before the audit, all three would have shipped silently
wrong numbers or a latent spec violation.

**Delivered**: `QueryPool` (new, `Agapanthe.Graphics`), instrumenting the 4 existing debug-label
regions (`Shadow`/`Scene`/`Tonemap`/`UI`) via `CommandList.WriteTimestampBegin`/`End` +
`ResetQueryPool`. Capability detection by the graphics queue actually in use
(`TimestampValidBits`, not the blanket feature bit). `Renderer` owns the pool and reads back
once per frame, non-blocking, per-region independently, into `GpuPassTimingsMs` (4 nullable
fields). `DebugOverlaySystem` displays a per-pass text line + a total graph, cleanly omitted
when unsupported. `AGAPANTHE_GPU_TIMESTAMPS=0` proves the disabled path on capable hardware.
The `FrameProfiler` seam UI-2 left as debt is closed by **decoupling**, not retrofitting —
`FrameStats`/`FrameSeries` (`Agapanthe.Engine`) are untouched (`git diff` empty).

**3 real bugs found by the double audit, all fixed**:
1. **Slot inversion** — the first draft read back `frame.Slot - 1` ("the other slot"), reasoning
   the current slot was about to be reused. Backwards: `FrameRenderer` waits
   `_inFlightFences[_frameSlot]` for the CURRENT slot before `BeginGpuTimestampFrame` ever runs,
   so `frame.Slot` is the one guaranteed complete; the other slot was submitted moments earlier
   with no fence wait — reading it raced the GPU (a reset there could run concurrently with the
   host read) and could pair a begin from one frame with an end from a different one, producing
   a plausible-looking, entirely wrong positive delta the wraparound guard cannot catch. Found
   independently by both `csharp-lowlevel` and `graphics-3d`. Fixed: read `frame.Slot` before
   resetting it.
2. **`TOP_OF_PIPE` for the begin timestamp** — in synchronization2, that stage in a first scope
   is equivalent to `NONE` (waits on nothing), so all 4 regions' begin timestamps latched at
   nearly the same instant and successive regions measured cumulative totals instead of
   independent durations (the summed "Total" could read up to ~4× the frame's real GPU cost).
   Found independently by both auditors. Fixed: `AllCommandsBit` (the modern, non-legacy
   spelling) on both ends.
3. **Reading never-reset queries** — every Vulkan query is uninitialized until its first reset
   (`VUID-vkGetQueryPoolResults-None-09401`); the first `FramesInFlight` calls to
   `BeginGpuTimestampFrame` would read queries no command had ever touched — a spec violation on
   every single run, invisible only because the installed validation SDK doesn't check it yet.
   Found by `graphics-3d` alone (escalated from a milder 🟠 `csharp-lowlevel` raised on the same
   topic). Fixed: a new `_gpuTimestampFramesSeen` counter — the first `FramesInFlight` calls only
   reset, never read.

**5 additional 🟠 findings applied**: `TimestampValidBits` tested then discarded (Intel/AMD/
MoltenVK commonly report 36-40 bits vs. NVIDIA's 64 — `GpuTimestampMath` now masks explicitly +
guards finiteness) · `QueryPool.ReadResultsNonBlocking` had no bounds/disposed/overflow-safe
guards (4 added) · 4 per-region `FrameSeries` written every frame and read by nothing (the
`AggregateBoundsSystem` shape MP-0a already closed once — removed, only the used `Total` series
remains) · the total was recorded as a bare `0` when all 4 fields were null, indistinguishable
from a genuinely free frame (now only recorded when at least one field is available) · the text
line was missing its `ms` unit suffix (every other overlay line has one).

**Gates**: 861 tests, 0 warning, masked-overlay capture byte-identical before/after (`git stash`
A/B, `b5382ac6...`) proving the instrumentation touches no rendered pixel, all 9 existing HDR
captures (8 Sandbox scenes + `topdown`) unchanged, human visual verdict PASS on both the
GPU-enabled and force-disabled overlay captures, JIT == NativeAOT on all 3 binaries, 0 leak / 0
validation everywhere.

**Deferred to backlog** (documented, not silently dropped): 4 real MoltenVK risks `graphics-3d`
found via spec/source research — `vkCmdWriteTimestamp2`'s `stage` parameter is entirely ignored
by MoltenVK (harmless: the TOP_OF_PIPE fix is a no-op there either way) · per-Metal-encoder, not
per-draw, timestamp granularity on Apple Silicon · a CPU fallback path that writes flat zeros
(not noise) if `MTLCounterSampleBuffer` fails, indistinguishable from a genuinely free pass · 2
open MoltenVK bugs (#2378, #2698) on exactly this double-buffered reset+read pattern — none
actionable without Apple hardware, all named for P3-M0 · zero test coverage of the
hardware-genuinely-unsupported capability branch (only the explicitly-disabled path is
exercisable on this machine) · the "Total" graph does not cover the candidate-buffer sync copy
or swapchain transitions, which fall outside the 4 instrumented regions.

**How to test it**:
```
dotnet build Agapanthe.slnx -c Release
dotnet test
AGAPANTHE_SCENE=model AGAPANTHE_CAPTURE_UI=/tmp/ui.ppm AGAPANTHE_MAX_FRAMES=8 \
  dotnet run --project samples/Sandbox -c Release --no-build
# Overlay shows: gpu  shadow X.XX  scene X.XX  tonemap X.XX  ui X.XX ms + a 3rd sparkline
AGAPANTHE_GPU_TIMESTAMPS=0 AGAPANTHE_SCENE=model AGAPANTHE_CAPTURE_UI=/tmp/ui-off.ppm \
  AGAPANTHE_MAX_FRAMES=8 dotnet run --project samples/Sandbox -c Release --no-build
# Overlay panel is visibly shorter — GPU block cleanly absent
```
