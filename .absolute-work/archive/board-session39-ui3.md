# Absolute Work Board — UI-3 (GPU timestamps + `FrameProfiler` seam)

**Status**: `completed` (2026-09-13) — all 5 waves done, 12/12 tasks closed
**Spec**: `docs/plans/2026-09-13-ui3-gpu-timestamps-design.md` (APPROVED 4.15/5, scored review by
a separate subagent, 2 iterations — round 1 found 2 factual errors + 3 design gaps, all fixed;
round 2 found 1 leftover contradiction, fixed)
**Session**: 39
**Rollback point**: commit `55f19af` (Slice-2, closed — last commit before this milestone)

## Scope

3rd/last of the "Texte & UI" trio (UI-1/UI-2 closed S25). GPU timestamps (`QueryPool`,
synchronization2) for the engine's 4 existing render-pass regions (Shadow/Scene/Tonemap/UI),
surfaced in the debug overlay as a decoupled series (not retrofitted into `FrameStats`), with
capability detection + graceful degradation (`AGAPANTHE_GPU_TIMESTAMPS=0` to force-test it).

## Project conventions (unchanged)

.NET 10 solution, xUnit tests in `tests/Agapanthe.Tests`, `dotnet build`/`dotnet test` via
`Agapanthe.slnx`, TDD expected, PowerShell primary shell (Bash lacks `vswhere.exe` on PATH for
AOT publish — add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH first).
Commits on explicit request only. Double audit for this milestone is **`csharp-lowlevel` +
`graphics-3d`** (deviation from the project-standard `engine-architect` pairing, decided in the
original session-25 pre-spec — this is squarely new Vulkan query-pool/sync2 work).

## Wave 1 — `Agapanthe.Graphics` primitives (pure math + QueryPool + capability detection)

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| UI3-001 | test | S | **done** | — | `tests/Agapanthe.Tests/GpuTimestampMathTests.cs` (TDD-first, confirmed red — `CS0103` compile error, type didn't exist): ordinary case, zero-duration case, non-unit-period case, `endTicks < beginTicks` wraparound-guard case |
| UI3-002 | code | S | **done** | UI3-001 | `src/Agapanthe.Graphics/GpuTimestampMath.cs`: `TryToMilliseconds` (sentinel `false` on wraparound, not a fabricated negative value). 858 tests total (+4), 0 warning |
| UI3-003 | code | M | **done** | — | `src/Agapanthe.Graphics/QueryPool.cs` (new: `VkQueryPool` wrapper, `QueryType.Timestamp`, deferred `IDisposable` via `DeletionQueue` — same pattern as every other module resource, e.g. `Sampler`, not the "plain lifetime" the spec originally suggested) + `CommandList.WriteTimestampBegin`/`WriteTimestampEnd(QueryPool, uint)` + `CommandList.ResetQueryPool(QueryPool, uint, uint)` + `GraphicsDevice.Commands.cs` `CmdWriteTimestamp2` dispatch (1.3 core vs `KhrSynchronization2`, mirrors `CmdPipelineBarrier2`). **Deviation from spec's literal API shape**: exposed as 2 fixed-stage methods (`WriteTimestampBegin`=TOP_OF_PIPE, `WriteTimestampEnd`=BOTTOM_OF_PIPE), not a generic `WriteTimestamp(pool, query, PipelineStage2 stage)` — a raw `PipelineStageFlags2` parameter would leak a `Vk*` type out of `Agapanthe.Graphics` into every caller (`Agapanthe.Rendering`), violating the project's locked "no `Vk*` type exits Graphics" rule; no caller needs any other stage. **Naming collision found and fixed**: `Agapanthe.Graphics.QueryPool` (our new wrapper) and `Silk.NET.Vulkan.QueryPool` share both a name and a namespace-lookup path — files needing the raw Vulkan handle (`GraphicsDevice.Commands.cs`) now alias it `VkQueryPool`, matching `Sampler.cs`'s existing precedent for the same collision |
| UI3-004 | code | S | **done** | — | `src/Agapanthe.Graphics/GraphicsDevice.cs`: `DetectGpuTimestampSupport` called once after physical-device selection — `PhysicalDeviceProperties.Limits.TimestampPeriod > 0` AND the ACTUAL graphics queue family's `QueueFamilyProperties.TimestampValidBits > 0` (not the blanket `timestampComputeAndGraphics` feature — MoltenVK caution, matches spec). Exposed as `GraphicsDevice.SupportsGpuTimestamps`, logged alongside the existing device-selection line |

**Parallel-safe**: UI3-003 ∥ UI3-004 (disjoint files: `QueryPool.cs`+`CommandList.cs` vs
`GraphicsDevice.cs`), both ∥ the UI3-001→002 TDD pair (disjoint files, `GpuTimestampMath.cs`).

## Wave 2 — `Agapanthe.Rendering`: `Renderer` owns the pool + per-region readout

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| UI3-005 | code | M | **done** | UI3-002, UI3-003, UI3-004 | `src/Agapanthe.Rendering/Renderer.cs`: new `bool gpuTimestampsRequested = true` ctor param → `SupportsGpuTimestamps` (ANDed with `GraphicsDevice.SupportsGpuTimestamps`); `QueryPool` created only when supported (`GpuQueriesPerSlot × FramesInFlight` = 16 queries), disposed in `DisposeResources`; `WriteTimestampBegin`/`End` bracketing each of the 4 existing debug-label regions (`Shadow`/`Scene`/`Tonemap`/`UI`, inside each region's existing `PushDebugLabel` scope); new `BeginGpuTimestampFrame` called once per frame at the top of `DrawScene` (the one call site guaranteed to run every frame, before `DrawUi`) — reads back the OTHER slot's 4 regions **independently** (non-blocking, `Result64Bit\|ResultWithAvailabilityBit`, no `WAIT_BIT`), then resets the current slot; new `public readonly struct GpuPassTimingsMs { float? Shadow, Scene, Tonemap, Ui; }` + `public GpuPassTimingsMs LastGpuPassTimingsMs { get; private set; }` (the property itself is never null — all-fields-null default when unsupported or a region didn't run that cycle). **Smoke-tested live**: `GraphicsDevice` logs `GPU timestamps: True` on this machine, `model` scene capture re-verified byte-identical to the pinned MD5 (`9a010fc3…`) — confirms zero HDR pixel change from the instrumentation, 0 leak |

## Wave 3 — `Agapanthe.App`: the env knob

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| UI3-006 | code | S | **done** | — | `src/Agapanthe.App/HostOptions.cs`: `GpuTimestampsEnabled` (bool, default `true`), `FromEnvironment` reads `AGAPANTHE_GPU_TIMESTAMPS` matching the `OverlayVisible` shape (`is not "0"`), NOT the `CullStats`/`ShaderReloadTest` shape |
| UI3-007 | code | S | **done** | UI3-005, UI3-006 | `src/Agapanthe.App/AppHost.cs`: threads `options.GpuTimestampsEnabled` into `Renderer`'s construction (4th ctor arg). Full solution build 0 warning |

**Parallel-safe**: UI3-006 ∥ Wave 2 (disjoint file, no dependency) — sequenced into its own wave
here for gate simplicity, not because of a real ordering constraint.

## Wave 4 — `Agapanthe.Engine.Render`: `DebugOverlaySystem` displays it

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| UI3-008 | code | M | **done** | UI3-005 | `src/Agapanthe.Engine.Render/DebugOverlaySystem.cs`: 5 new `FrameSeries` fields (Shadow/Scene/Tonemap/Ui/**Total**, same capacity as `FrameTimeMs` — Total is its own series, the sum of whichever regions were available that frame, not derived after the fact); `Execute()` — omit the whole GPU block when `!_renderer.SupportsGpuTimestamps`; else read `LastGpuPassTimingsMs`, append each non-null field into its own series independently (a null field skips only that series, never stalls the others), record Total unconditionally (sum treating a null field as 0 contribution — a null UI field, the common case, must not make the total vanish for one frame), one compact text line (per-pass ms, `--` placeholder for a null field via new `AppendMsOrPlaceholder` — never `0.00`, a real skip must not read as a real zero), one `Sparkline` (new `GpuGraphColour`, visually distinct from the CPU frame-time graph) for the Total series; `panelHeight` grows by 1 line + 1 graph when supported+visible. Full solution build 0 warning, 858 tests green |

## Wave 5 — verify + mandatory tail

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| UI3-009 | test | M | **done** | UI3-007, UI3-008 | Full `dotnet test` green (858). **Byte-identical A/B via `git stash`**: masked-overlay swapchain capture (`AGAPANTHE_OVERLAY=0`) hashes identical before/after UI-3 (`b5382ac6…`) — proves the whole render pipeline's pixel output is untouched by the GPU-timestamp instrumentation itself (timestamp queries are a non-rendering side channel). Visible-overlay capture with GPU timestamps on: **human visual verdict PASS** — panel shows `gpu  shadow 0.03  scene 0.03  tonemap 0.01  ui 0.00` line + a 3rd sparkline, legible, plausible values, **`alloc 0 B/frame` stays green** (0-alloc regression check holds for the new per-frame host readback). Same with `AGAPANTHE_GPU_TIMESTAMPS=0`: **human visual verdict PASS** — panel is visibly shorter, GPU block cleanly absent, no artifacts. Every existing non-UI capture (8 Sandbox scenes + `topdown`) re-verified byte-identical on JIT. **AOT-published all 3 binaries** (Sandbox/TopDown/HeadlessSim) — **JIT == NativeAOT confirmed** on all 9 captures + HeadlessSim's own run, 0 leak/0 validation everywhere |
| UI3-010 | test | — | **done** | UI3-009 | Self code review + Requirements validation vs D1-D6. Diff is contained: 7 files touched, exactly those the spec named (`Agapanthe.World`/`Engine`/`Assets`/`Scene` untouched), 2 new Graphics files + 1 new test file, no stray debug artifacts. **D1** confirmed — no compute-cull sub-regions, no `Skybox` sub-region, no IBL-pass coverage added ✓ · **D2** confirmed — 4 regions instrumented, `DrawUi`'s early-return-before-`PushDebugLabel` genuinely leaves that region's queries unwritten that cycle (code-verified; not exercised live since the overlay itself always draws text when visible — noted, not a gap: the *mechanism* is D4's per-region independent check, exercised by every OTHER region every single frame) ✓ · **D3** confirmed — `git diff` on `Agapanthe.Engine/FrameStats.cs` is empty, zero changes ✓ · **D4** confirmed — no `WAIT_BIT` anywhere, `ReadGpuRegionMs` reads one region (2 queries) at a time, independently, 4 separate calls per frame, not one bulk 8-query gate ✓ · **D5** confirmed live — `AGAPANTHE_GPU_TIMESTAMPS=0` capture shows the clean absence ✓ · **D6** confirmed — `Renderer` owns `QueryPool`+readout, `DebugOverlaySystem` owns its own 5 `FrameSeries` (4 regions + Total), no new cross-project `GpuFrameStats`-shaped type exists anywhere ✓ |
| UI3-011 | infra | — | **done** | UI3-010 | Double audit `csharp-lowlevel` (3.4/5) + `graphics-3d` (3.4/5), dispatched in parallel. **2 🔴 found independently by both, fixed**: (1) **slot inversion** — `BeginGpuTimestampFrame` read `frame.Slot - 1` ("the other slot"), assuming the current slot was the one about to be reused. `FrameRenderer` actually waits `_inFlightFences[_frameSlot]` for the CURRENT slot before this method ever runs — so `frame.Slot` is the one guaranteed complete; the other slot is in flight with no fence wait, a genuine race (host reset vs. host read, cross-frame begin/end pairing producing a plausible-but-wrong positive delta the wraparound guard can't catch). Fixed: read `frame.Slot` before resetting it. (2) **`TOP_OF_PIPE` for begin** — in synchronization2 that stage in a first scope is equivalent to `NONE` (waits on nothing), so all 4 regions' begin timestamps latched at nearly the same instant, producing cumulative totals (Scene≈shadow+scene, Tonemap≈shadow+scene+tonemap, `Total` up to ~4× the real GPU cost) instead of independent per-region durations. Fixed: `AllCommandsBit` (modern, non-legacy) on both ends. **A 3rd 🔴, found by `graphics-3d` alone, more severe than csharp-lowlevel's 🟠 on the same topic**: reading never-reset queries (`VUID-vkGetQueryPoolResults-None-09401` — every query is uninitialized until reset) on the first `FramesInFlight` frames — a real spec violation every run, invisible only because the installed validation SDK doesn't check it yet. Fixed: new `_gpuTimestampFramesSeen` counter — the first 2 calls only reset (never read), reading only starts once every slot has been through one full cycle. **🟠 findings applied**: `TimestampValidBits` tested then discarded — Vulkan only defines the low bits (Intel/AMD/MoltenVK commonly 36-40 vs. NVIDIA's 64) — `GpuTimestampMath.TryToMilliseconds` now masks explicitly (+ a finiteness guard on `timestampPeriodNs`/the result; `graphics-3d` confirmed via spec it's a modulo-wrap, not a rejection, but keeping the bit count is the right fix either way) · `QueryPool.ReadResultsNonBlocking` had no bounds/disposed guard/overflow-safe arithmetic — 4 guards added (`ObjectDisposedException`, `firstQuery+count<=QueryCount`, reject `count=0`, `ulong` arithmetic) · 4 per-region `FrameSeries` in `DebugOverlaySystem` written every frame and read by nothing (the `AggregateBoundsSystem` shape MP-0a already closed once) — removed, only `_gpuTotalMs` (actually drawn) remains · `_gpuTotalMs.Record(0)` when all 4 fields are null (indistinguishable from a genuinely free frame) — now only records when at least one field is available · missing `ms` suffix on the text line (the only overlay line without a unit) — added. **`graphics-3d` also documented 4 real MoltenVK risks for P3-M0** (unactionable without Apple hardware, deferred to backlog): `stage` is entirely ignored by MoltenVK (so the TOP_OF_PIPE→ALL_COMMANDS fix is a no-op there, but harmless too) · per-Metal-encoder, not per-draw, granularity on Apple Silicon · a CPU fallback that writes flat zeros (not noise) if `MTLCounterSampleBuffer` fails · 2 open MoltenVK bugs (#2378 availability without a usable value, #2698 intermittent use-after-free) on exactly this double-buffered reset+read pattern. Re-verified after EVERY fix: 861 tests green, masked capture byte-identical (`b5382ac6…`, `git stash` A/B), human visual verdict PASS on the corrected overlay (per-pass numbers now plausible and non-cumulative, e.g. `shadow 0.03 scene 0.03 tonemap 0.01 ui 0.00 ms`), all 9 HDR captures re-verified byte-identical on JIT **and** NativeAOT, HeadlessSim unchanged, 0 leak/0 validation everywhere |
| UI3-012 | docs | — | **done** | UI3-011 | Converge: `CLAUDE.md` (top summary + prose milestone entry + dette persistante), `docs/AVANCEMENT.md` (§Reprise entry), `docs/BACKLOG.md` (Texte & UI marked entirely closed, MoltenVK risks logged under P3-M0), spec §8 outcome section added, board archived |

## DAG (dependency depth)

```
W1: UI3-001 ── UI3-002 ─┐
    UI3-003 (∥)          ├─► W2: UI3-005 ─┬─► W4: UI3-008 ─┐
    UI3-004 (∥)          ┘                │                │
W3: UI3-006 (∥ W2) ── UI3-007 (needs 005,006) ──────────────┴─► W5: UI3-009 ── UI3-010 ── UI3-011 ── UI3-012
```

## Requirements validation (to fill at UI3-010)

- D1 scope = exactly the session-25 pre-spec — verify nothing extra crept in (no compute-cull
  sub-regions, no Skybox sub-region, no IBL-pass coverage).
- D2 4 regions, UI region's legitimate per-frame skip handled — verify via a capture where the
  UI draw list is genuinely empty at least once (or by code inspection of the skip path).
- D3 `FrameStats`/`FrameSeries` untouched — `git diff` should show zero changes to those 2 files.
- D4 non-blocking, per-region readback — verify no `WAIT_BIT` anywhere, one query pool result call
  per region not one bulk call across 8 queries covering all 4 regions as a single gate.
- D5 `AGAPANTHE_GPU_TIMESTAMPS=0` forces the disabled path — verify live.
- D6 `Renderer` owns pool+readout, `DebugOverlaySystem` owns its own 4 `FrameSeries` — verify no
  new cross-project `GpuFrameStats`-shaped type exists.

## Deferred Work (carried from spec §7)

Separate timestamp regions for `shadow_cull.comp`/`scene_cull.comp` (bundled into Shadow/Scene) ·
a separate pair for nested `Skybox` (bundled into Scene) · GPU-timing coverage of one-time IBL
generation (out of per-frame scope) · retrofitting `FrameSeries` for backfill (D3 rejected) ·
zero test coverage of the hardware-genuinely-unsupported capability-detection branch (spec §5,
explicitly accepted bounded risk, consistent with macOS/Linux never validated project-wide).
