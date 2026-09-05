# MP-0c — Time authority — design

> Third sub-milestone of **MP-0 (authority foundations)**, itself the next step of the **engine cap**
> ([backlog §4quater](../BACKLOG.md)). Anchor decisions (S25): the artifact is **the engine** · generalist but
> especially large-scale space sims · **server-authoritative** multiplayer designed in from now (NOT lockstep —
> byte-identity is intra-binary only) · topology is a **deployment** choice, never an architecture one, so the same
> simulation code runs everywhere and only authority changes.
>
> Status: **IMPLEMENTED** (MP-0c closed session 28). Spec APPROVED — 4.4/5 (independent scored review, round 2;
> threshold 4.0). Round 1 scored 3.7/5 NEEDS REVISION; round 2 confirmed F1–F9 mechanically fixed and left three 🟡,
> all closed: R1 (a `maxFrames >= 0` regression the v2 edit introduced — reverted to `> 0`), R2 (the `0L`
> justification was wrong — `Math.Max(0, long)` *does* compile; kept `0L` for readability), R3 (a failed
> `Debug.Assert` cannot be caught in-process — the rate check is extracted as `PhysicsSystem.RatesMatch`).
>
> **Implementation amendments** (post double audit `csharp-lowlevel` + `engine-architect`, both PASS-with-concerns,
> no 🔴):
> - **The `Debug.Assert` on a non-finite/negative wall-clock delta (§Architecture 1) was NOT added** to
>   `FixedTimestepAccumulator.Advance`. A failed `Debug.Assert` terminates the test host, which makes both the
>   corruption-safety behaviour and the new `SanitisedInputCount` counter untestable. The sanitisation is extracted
>   as `internal static Sanitise(float, float)` (spec R3 shape, unit-tested directly), and `SanitisedInputCount` — a
>   counter any build / test / telemetry can read — is the signal instead. This is a stronger signal than a
>   Debug-only dialog, so the assert buys nothing it doesn't cost.
> - **Non-finite / negative delta → `0f`, not `→ MaxWallClockDeltaSeconds`** (audit LL F-3). Mapping `NaN` to the
>   ceiling runs a full catch-up burst (~15 physics ticks) for a broken-clock input, silently; losing the frame is
>   the inert failure.
> - **Constructor rejects a `max/fixed` ratio above 1024** (audit LL F-1): below ~1 µs steps the catch-up loop
>   stalls a frame, and past ~2²⁴ the `float` subtraction rounds to a no-op and the loop never terminates. Fail
>   loudly, not hang.
> - **`FixedTimestepAccumulator.AdvanceFrame(host, dt)`** = `BeginFrame()` + `Advance()` (audit arch F2):
>   `FrameOrchestrator.Tick` is now a one-liner calling it, and the catch-up test exercises that method instead of
>   recopying the sequence. A dedicated headless server loop reuses it directly.
> - **`SimulationHost.LastFrameTickCount` is latched in `EndFrame`** (audit arch F3), same lifecycle as
>   `LastFrameMs` — "last complete frame", not a live counter.
> - **`CreateDefault` params renamed** `fixedTickRate`→`fixedTickDeltaSeconds`, `maxWallClockDt`→
>   `maxWallClockDeltaSeconds` (audit arch F1 — they carry a period, not a rate).
> - **`AotComponentProbe` exercises the accumulator** (`AotAccumulatorSmoke`, audit LL F-4) so the AOT gate line is
>   not vacuous for the new type.
>
> Deferred (board §Deferred Work): `Reset()` on the accumulator (arch F4), fixed-rate single source of truth (arch
> F7, netcode prerequisite), `HasTicked` on `SimulationHost` (arch F3 / F7), thread anchor in `Tick` (LL F-6).
> Follows MP-0a ([2026-08-13-mp0a-headless-split-design.md](2026-08-13-mp0a-headless-split-design.md)) and MP-0b
> ([2026-08-13-mp0b-entity-identity-design.md](2026-08-13-mp0b-entity-identity-design.md)). Both earlier specs were
> downgraded on their first round for the **same** failure — a load-bearing premise that was plausible, cited, and
> unverified (MP-0a v1 cited a doc comment instead of executable code; MP-0b v1 built its central gate on a `Set` that
> was actually an `Add`). Every factual claim below cites an **executable** line, and the two claims this milestone
> most depends on — that the accumulator shape is already wired through `SimulationHost`, and that the `CurrentTick`
> off-by-one is currently **untested** — are checked in §Context rather than asserted.
>
> **v1 scored 3.7/5 — NEEDS REVISION.** All ~35 line citations verified exact; the blast radius verified exhaustive;
> the 8 decisions implemented faithfully. What fell short was one rung higher in the reasoning chain: (F1) the
> frame-rate equivalence test — the milestone's backbone — **fails as written**, because `3f/60f` and `3f*(1f/60f)`
> differ by 1 ULP in `float`, so the two profiles run 59 vs 60 ticks; (F2) "capture hashes will change" is **false** —
> no production code reads `TickContext.DeltaSeconds` (grep-confirmed), and in capture mode decision 2 yields exactly
> one tick per frame, so the output must stay bit-identical, and the DoD must **require** that rather than accept a
> drift; (F3) nothing exercised the `FrameOrchestrator`↔accumulator wiring or the catch-up path (N>1 tick/frame);
> (F4) once F1 is fixed the position-equality assertion is near-tautological (decision 3 has `PhysicsSystem` ignore
> `DeltaSeconds`), so the primary assertion must be on the **integer tick count**; (F5) a blast-radius row said "update
> the comment" where the **code** at `RenderStageNeutralityTests.cs:105-108` builds the post-increment context — the
> exact comment-vs-code trap that sank MP-0a v1. Plus five 🟡: NaN clamp propagation (F6), `Math.Max` typing +
> `TickIndex == 0` ambiguity (F7), `Advance`'s return value dropped with nothing surfacing catch-up (F8), decision 2
> covering only `AGAPANTHE_MAX_FRAMES` (F9). All fixed below; no re-design. **F2 corrects a factual prediction the
> brainstorm carried (decision 2's "hashes will change"), not one of the 8 locked decisions — the decoupling
> mechanism is unchanged; validated with the human before this revision.**
>
> Brainstorm: 8/8 decisions locked, 100 % confidence both sides on every exchange
> ([.absolute-human/board.md](../../.absolute-human/board.md), session 27). This document turns that into a spec; it
> does not re-open the interview.

## Summary

`PhysicsSystem` advances the simulation by a **fixed** step once **per rendered frame**, so simulation speed is
bolted to the machine's frame rate: at 144 fps the physics runs 144×/s where 60×/s was intended.

- `PhysicsSystem.Execute` is `_world.StepPhysics(in _settings)` and **ignores `ctx` entirely**
  (`PhysicsSystem.cs:29`); its own remark says `TickContext.DeltaSeconds` is *"deliberately ignored — physics steps
  by frame count"* (`PhysicsSystem.cs:12-16`).
- `GameWorld.StepPhysics(in PhysicsSettings settings)` reads `var dt = settings.FixedDt;` (`GameWorld.Physics.cs:120`),
  a constant defaulting to `1/60` (`PhysicsSettings.cs:44-45`).
- The scheduler is ticked once per frame: `orchestrator.Tick((float)dt)` inside the `window.Rendered` handler
  (`samples/Sandbox/Program.cs:766`), and `FrameOrchestrator.Tick` runs exactly one `_simulation.Tick`
  (`FrameOrchestrator.cs:146-153`).

This is a debt noted since P3-M3 (`PhysicsSettings.cs:9-14` defers *"a wall-clock accumulator + render
interpolation"* to the backlog). Server-authoritative multiplayer is what makes it a **now** problem: two nodes
running the same scene at different frame rates step physics a different number of times and diverge.

**This milestone delivers the decoupling and nothing more.** An accumulator drives the simulation at a fixed step
regardless of frame rate; rendering displays the last completed tick, with **no** interpolation between ticks
(decision 1). Interpolation is a separate, more invasive milestone — there is no per-entity "previous state" anywhere
to interpolate from (verified in §Context).

**Blast radius, measured this session** (do not re-search — §"Measured blast radius" enumerates every site):

- `FixedTimestepAccumulator`: **new** type, `Agapanthe.Engine`, no existing code to migrate.
- `FrameOrchestrator.CreateDefault`: **one** production caller (`samples/Sandbox/Program.cs:477`), GPU-bound, never
  reached by a GPU-free test — extending its signature with defaulted parameters is risk-free.
- `PhysicsSettings` / `FixedDt`: **unchanged** (decision 3). `PhysicsSystem` gains one `Debug.Assert`.
- `FrameIndex` → `TickIndex` rename: `SystemScheduler`, `TickContext`, `SimulationHost` (3 production files), plus
  `samples/HeadlessSim/Program.cs:114` and **5 test files** — full list in §Measured blast radius.
- The `CurrentTick` off-by-one fix touches **one** expression (`SimulationHost.cs:71`) and is currently pinned by
  **no test** (verified — §Context), so the fix arrives with the first test of it.

Nothing degrades by having waited: the bug and its fix arrive in the same change.

## Context — what the codebase already provides

- **MP-0a already wired the accumulator shape into the code, on purpose.** `SimulationHost.BeginFrame()` is separate
  from `Tick()` (`SimulationHost.cs:78,98`), with a doc comment stating the split exists *because* "the time-authority
  sub-milestone introduces a fixed-step accumulator — N ticks per wall-clock frame" and the old bracket-in-`Tick`
  shape "would have filed N samples per frame" (`SimulationHost.cs:84-97`). This is **verified, not assumed**: the
  test `HeadlessSimulationTests.SeveralTicksInOneFrameRecordASingleSample` (`HeadlessSimulationTests.cs:98-119`)
  already drives three `Tick` calls inside one `BeginFrame`/`EndFrame` pair and asserts `Stats.FrameCount == 1` and
  `FrameIndex == 4`. The frame-measurement bracket is therefore **already correct** for this milestone and does not
  move.
- **`SystemScheduler.FrameIndex` is already a tick counter, not a frame counter.** `Tick` increments `_frameIndex`
  after the stages (`SystemScheduler.cs:116`); the property is `_frameIndex` (`SystemScheduler.cs:50`), with the
  comment *"A tick is not a frame: a frame can be skipped, a tick never is."* `SchedulerTests.FrameIndex_AdvancesWithTicksOnly`
  (`SchedulerTests.cs:63-73`) pins it. The rename in decision 4 is cosmetic on this member — the counter's semantics
  are already right.
- **`FrameOrchestrator.Tick` already opens the bracket where the accumulator loop will sit.** `FrameOrchestrator.cs:146-153`
  is `_simulation.BeginFrame(); _simulation.Tick(deltaSeconds);` with the comment *"When the time-authority
  sub-milestone turns this into an accumulator loop, BeginFrame stays here and only the Tick call repeats."* The
  change is to replace the single `_simulation.Tick` with an accumulator that calls it N times.
- **The `CurrentTick` off-by-one is real and currently untested.** `SimulationHost.CurrentTick` is
  `new(_dt, _scheduler.FrameIndex)` (`SimulationHost.cs:71`), read only by the render delegate
  (`FrameOrchestrator.cs:84`), which runs **after** `Tick` incremented the counter — so a render system sees `N+1`
  where the tick systems of the same frame saw `N` (doc: `SimulationHost.cs:61-70`). The doc says *"Pinned by
  `RenderBarrierTests`"*, but that is **imprecise**: `grep CurrentTick` over the whole repo returns exactly two hits
  (`SimulationHost.cs:71`, `FrameOrchestrator.cs:84`) — **no test names it**. `RenderBarrierTests` pins the
  *scheduler counter* (`RenderBarrierTests.cs:118`, `Render_DoesNotAdvanceTheTickIndex`), not `CurrentTick`'s value.
  The fix therefore introduces the first test of this member.
- **`PhysicsSettings` is consumed by ~20 direct `StepPhysics` calls that never build a `TickContext`.**
  `ContactResolutionOrderTests` (`ContactResolutionOrderTests.cs:38-61`) is representative: it calls
  `world.StepPhysics(in Settings)` in a bare loop, with `Settings` a `new PhysicsSettings(...)` at
  `ContactResolutionOrderTests.cs:33`. Removing `FixedDt` to make `PhysicsSystem` consume `ctx.DeltaSeconds` would
  break every one of those signatures for a cosmetic gain — this is exactly what killed the initial recommendation in
  decision 3.
- **`HeadlessSim` does not go through `FrameOrchestrator` or any accumulator.** Its loop is
  `host.BeginFrame(); host.Tick(FixedDt); host.EndFrame();` repeated `ticks` times (`samples/HeadlessSim/Program.cs:106-111`),
  with `FixedDt = 1f/60f` (`:21`). The comment already says *"A real server will grow an accumulator; BeginFrame
  stays outside that inner loop"* (`:104-105`). That loop is **already** the correct fixed-step batch behaviour for a
  deterministic server — it stays untouched, and its snapshot hash therefore stays
  `7e8dc68f5a25914c84677a7a53ad3a58` (pinned at `HeadlessSimSnapshotFormatTests.cs:62`).
- **The Sandbox already parses `AGAPANTHE_MAX_FRAMES`** into `maxFrames` at `samples/Sandbox/Program.cs:46` (for
  headless auto-close). Decision 2 reuses that same signal to select a synthetic constant `dt`.
- **No `Lerp`, no per-entity "previous" state anywhere.** `WorldPosition`/`WorldTransform` have no historical
  counterpart (verified by grep over `Agapanthe.World` and `Agapanthe.Rendering`). This is what makes decision 1's
  "no interpolation" not a shortcut but the only bounded option — the alternative is a milestone.

## Locked decisions (interview, session 27 — 8/8, 100 % confidence both sides)

1. **Scope: decoupling only, no interpolation.** The accumulator advances the sim at fixed `dt` independently of
   frame rate; the renderer shows the last completed tick, unsmoothed. Slight judder is possible at frame rates that
   do not divide the tick rate evenly — accepted. Adding per-entity previous-state storage is a separate, more
   invasive milestone. Backlog: interpolation stays 🟡 (secondary); the accumulator was 🟠.

2. **Capture/bench determinism: a fixed synthetic `dt` in capture mode.** In a real interactive session the
   accumulator consumes the true GLFW wall-clock `dt`. A `AGAPANTHE_MAX_FRAMES` / `AGAPANTHE_CAPTURE` run must stay
   reproducible run-to-run on one machine. So: **when `AGAPANTHE_MAX_FRAMES` is set, the `dt` handed to the
   accumulator is a constant equal to the fixed tick period** — exactly one tick per frame, as today.
   *(Prediction correction, F2 — the mechanism is the locked decision, this consequence was carried from the
   brainstorm and is wrong.)* The capture hashes therefore **must not change**: no production code reads
   `TickContext.DeltaSeconds` (§Context), one tick per frame is the exact behaviour at `HEAD`, `420 × Advance(fixed)`
   consumes 420 ticks with a zero remainder, and the `CurrentTick` off-by-one fix moves nothing a render system
   observes. `12638edd` / `03421357` staying byte-identical is the milestone's strongest regression gate, not a
   result to be re-pinned (§DoD).

3. **`PhysicsSettings.FixedDt` unchanged; guard by `Debug.Assert` only.** The initial recommendation — make
   `PhysicsSystem` consume `ctx.DeltaSeconds`, delete `FixedDt` — was **dropped after measuring the blast radius**
   (§Context): `GameWorld.StepPhysics` reads `settings.FixedDt` internally (`GameWorld.Physics.cs:120`) and ~20 test
   sites call `StepPhysics` directly, never through `TickContext`. `PhysicsSystem.Execute` instead gains
   `Debug.Assert(ctx.DeltaSeconds == _settings.FixedDt, …)` — the same safety net against an accumulator/physics
   drift, zero test files broken.

4. **`FrameIndex` → `TickIndex` everywhere, and the off-by-one is fixed.** MP-0a explicitly reserved this to this
   milestone (`SimulationHost.cs:66-68`: *"belongs to the time-authority sub-milestone, which will revisit what a
   tick index means"*). Full rename: `TickContext.FrameIndex`, `SystemScheduler.FrameIndex`, `SimulationHost.FrameIndex`
   → `TickIndex`, including `HeadlessSim` and the tests. Off-by-one: `SimulationHost.CurrentTick` currently exposes
   `_scheduler.FrameIndex` **after** the increment; it is corrected to expose the **last tick actually executed** —
   `Math.Max(0L, _scheduler.TickIndex - 1)` (the clamp covers the boundary before any tick has run; `0L` for
   readability — the counter is `long`).

5. **Anti-spiral-of-death: clamp the wall-clock `dt` on INPUT.** One abnormally long frame (breakpoint, OS pause,
   blocking resize) explodes the accumulated time → burst catch-up → worse next frame. The wall-clock `dt` entering
   the accumulator is clamped to a maximum (default **250 ms**, ~15 catch-up ticks at 60 Hz) **before** the catch-up
   loop. Time beyond the ceiling is lost: the simulation "slows down" rather than attempting unbounded catch-up.

6. **`DebugOverlaySystem` / `UiRenderSystem` running N× per catch-up frame: documented debt, not fixed now.** Both are
   `ISystem`s that run every tick; under catch-up they rebuild their content N times for nothing (only the last tick
   is displayed). Functionally correct, CPU-wasteful. Restructuring their cadence touches `Agapanthe.Engine.Render`
   beyond the accumulator — out of scope to stay focused on time authority. Explicit debt, for a future UI pass
   (UI-3 or dedicated). *(Note: `DebugOverlaySystem` is a tick-side `ISystem` living in `Agapanthe.Engine.Render` —
   MP-0a placed it by dependency, not by interface.)*

7. **Accumulator location: a new standalone type in `Agapanthe.Engine`.** Not in `FrameOrchestrator`
   (`Agapanthe.Engine.Render` — a future headless real-time server would have to duplicate the logic), not merged
   into `SimulationHost` (MP-0a kept it deliberately minimal: *"It owns nothing"* — `SimulationHost.cs:15-18`). A new
   `FixedTimestepAccumulator` drives `SimulationHost.Tick` by composition — reusable as-is by a future dedicated
   server that has no `FrameOrchestrator`.

8. **Verification: a GPU-free equivalence test + the standard capture protocol.** The real goal (sim speed
   independent of frame rate) is invisible on a static hash. A test drives the accumulator with two `dt` profiles
   chunking the same total simulated time differently and asserts they run the **same integer tick count** (and,
   secondarily, produce identical positions). The brainstorm's example — "30 calls of 33 ms vs 10 calls of 100 ms" —
   is **not** how the test is built: those values are not exact multiples of `1/60` and run 59 vs 60 ticks (F1); the
   profiles use synthetic multiples of `accumulator.FixedDeltaSeconds` instead. Plus the standard capture protocol
   for the visual gate — hashes **unchanged** (F2 — decision 2 makes capture mode one-tick-per-frame, byte-identical
   to `HEAD`).

## Architecture

### 1. `FixedTimestepAccumulator` (new, `Agapanthe.Engine`)

```csharp
namespace Agapanthe.Engine;

/// <summary>
/// Decouples simulation speed from frame rate (MP-0c): accumulates wall-clock time and runs a fixed-step
/// <see cref="SimulationHost.Tick"/> as many times as the accumulated time allows. It owns no state beyond the
/// leftover accumulator; a dedicated server with no FrameOrchestrator drives it directly.
/// </summary>
public sealed class FixedTimestepAccumulator
{
    public FixedTimestepAccumulator(float fixedDeltaSeconds, float maxWallClockDeltaSeconds = 0.25f);

    /// <summary>The fixed simulation step, seconds. Every <see cref="SimulationHost.Tick"/> this type issues uses
    /// exactly this value.</summary>
    public float FixedDeltaSeconds { get; }

    /// <summary>The input clamp (decision 5): a wall-clock delta larger than this is treated as this. Time above the
    /// ceiling is dropped — the simulation slows rather than catching up without bound.</summary>
    public float MaxWallClockDeltaSeconds { get; }

    /// <summary>
    /// Clamps <paramref name="wallClockDeltaSeconds"/> to <see cref="MaxWallClockDeltaSeconds"/>, adds it to the
    /// internal accumulator, then calls <c>host.Tick(FixedDeltaSeconds)</c> once per whole <see cref="FixedDeltaSeconds"/>
    /// that fits, carrying the remainder forward. Returns the number of ticks run this call (may be 0).
    /// <para>0-alloc: no delegate, no closure — it calls <see cref="SimulationHost.Tick"/> directly in a loop.</para>
    /// </summary>
    public int Advance(SimulationHost host, float wallClockDeltaSeconds);
}
```

Constructor validation (loud, per the project's standing preference):

- `fixedDeltaSeconds > 0` — else `ArgumentOutOfRangeException`.
- `maxWallClockDeltaSeconds >= fixedDeltaSeconds` — else no tick could ever accumulate; `ArgumentOutOfRangeException`.
- `wallClockDeltaSeconds` is sanitised **in this order** (F6 — `MathF.Min` propagates `NaN`, so the finiteness test
  must come first, or a single `NaN` frame freezes the sim permanently and silently — the worst case this milestone
  exists to prevent):

  ```csharp
  if (!float.IsFinite(dt))       dt = MaxWallClockDeltaSeconds;   // NaN/±Inf → treat as a pathological long frame
  else if (dt < 0f)              dt = 0f;
  else                           dt = MathF.Min(dt, MaxWallClockDeltaSeconds);
  ```

  A non-finite or negative input is `Debug.Assert`-flagged (a caller feeding those has an upstream bug) but must not
  corrupt the accumulator in Release.

`Advance` does **not** call `BeginFrame`/`EndFrame`: the frame-measurement bracket is the caller's (decision 7 keeps
the accumulator minimal, and `SimulationHost` already separates the two — §Context).

### 2. `FrameOrchestrator` (modified)

- `CreateDefault(...)` — **both** overloads (`FrameOrchestrator.cs:95-97` and `:108-122`) — gain two optional
  trailing parameters: `float fixedTickRate = 1f / 60f, float maxWallClockDt = 0.25f`. The only production caller is
  `samples/Sandbox/Program.cs:477` (the 5-arg overload), GPU-bound, never invoked by a GPU-free test — extending the
  signature with defaults is risk-free.
- The private constructor (`FrameOrchestrator.cs:70`) builds a `FixedTimestepAccumulator` from those values and holds
  it in a field.
- `Tick(float deltaSeconds)` (`FrameOrchestrator.cs:146-153`) — the parameter is renamed `wallClockDeltaSeconds`
  (it is now a wall-clock delta, not "the dt this tick will use"):

  ```csharp
  public void Tick(float wallClockDeltaSeconds)
  {
      _simulation.BeginFrame();
      _accumulator.Advance(_simulation, wallClockDeltaSeconds);
  }
  ```

  `BeginFrame` stays exactly where it is; only the single `_simulation.Tick(deltaSeconds)` becomes the accumulator
  call. `EndFrame` (`FrameOrchestrator.cs:171`) is unchanged. `Advance`'s return value is not used here — the count
  is surfaced through `SimulationHost` instead (next point), which is where `FrameStats` already lives.
- `FrameOrchestrator` gains one forwarding property so the Sandbox's `window.Rendered` closure can select the
  synthetic capture `dt` (decision 2) without reaching into internals:

  ```csharp
  /// <summary>The fixed simulation step the accumulator issues, seconds. In capture mode the host feeds this value
  /// as the wall-clock delta so a run is reproducible tick-for-tick.</summary>
  public float FixedTickDeltaSeconds => _accumulator.FixedDeltaSeconds;
  ```

- **Surfacing catch-up (F8).** `Advance` running N > 1 ticks in one frame is decision 6's accepted CPU waste, and
  today nothing would reveal it. `SimulationHost` counts ticks per frame — a field reset in `BeginFrame`,
  incremented in `Tick`, exposed as `LastFrameTickCount` (1 in steady state, forwarded by `FrameOrchestrator`):

  ```csharp
  // SimulationHost
  public int LastFrameTickCount { get; private set; }   // ticks since the last BeginFrame
  ```

  This is the value the F3 wiring test asserts on, and the Sandbox's bench-stats log line (`Program.cs:787`) appends
  it so an interactive non-60 Hz run prints the catch-up count. **Wiring it into `DebugOverlaySystem` is out of scope
  and deferred to UI-3**: the overlay is constructed with a `FrameStats` and not the host (MP-0a audit narrowed it
  deliberately), so an on-screen readout would mean either a new ctor dependency or a `FrameStats` field change —
  exactly the refactor the UI-2 debt note parks for the milestone that will know the `FrameProfiler` seam's shape.

### 3. `SimulationHost` / `SystemScheduler` / `TickContext` (rename + off-by-one fix)

Pure rename (the counter and its semantics do not change):

- `SystemScheduler.FrameIndex` → `TickIndex` (`SystemScheduler.cs:50`; field `_frameIndex` → `_tickIndex` at `:37,103,116`).
- `TickContext` constructor parameter `frameIndex` → `tickIndex` and property `FrameIndex` → `TickIndex`
  (`Systems.cs:13-26`).
- `SimulationHost.FrameIndex` → `TickIndex` (forwarding property, `SimulationHost.cs:58-59`).

Behaviour fix (decision 4):

- `SimulationHost.CurrentTick` (`SimulationHost.cs:71`): `new(_dt, Math.Max(0L, _scheduler.TickIndex - 1))` instead
  of the raw post-increment value — `0L` for clarity of intent (`Math.Max(long, long)` is already selected without
  it via the implicit `int→long` conversion, but writing `0` reads like an `int` comparison; R2, review round 2).
  The doc block at `:61-70` is rewritten from "this is deliberate preservation of an off-by-one" to "this is the last
  tick actually executed", and the stale *"Pinned by `RenderBarrierTests`"* line is replaced with the name of the new
  test that actually pins it (§Testing).
- **Accepted ambiguity (F7):** after the clamp, `CurrentTick.TickIndex == 0` means *both* "no tick has run yet" and
  "tick 0 has completed". No current consumer distinguishes them (there is exactly one reader,
  `FrameOrchestrator.cs:84`, and it does not branch on the value). MP-0d will timestamp commands against this counter
  and will need the distinction — it is called out here, and a `bool HasTicked => _scheduler.TickIndex > 0` is the
  cheap forward-compatible answer if MP-0d wants it. Adding it now, unused, is scope this milestone declines.

`SystemScheduler.cs:103` still builds the context with the pre-increment value — a system running *inside* tick `N`
sees `TickIndex == N`, unchanged. Only the *render-facing* `CurrentTick` view changes.

### 4. `PhysicsSystem` (modified, minimal)

```csharp
// The predicate is extracted so a test can exercise it directly: a failed Debug.Assert in modern .NET goes through
// DebugProvider.FailCore, which terminates the test host rather than raising a catchable exception (R3, review
// round 2). The assert stays as the runtime guard; RatesMatch is what the unit test asserts on.
internal static bool RatesMatch(float tickDeltaSeconds, float fixedDt) => tickDeltaSeconds == fixedDt;

public void Execute(in TickContext ctx)
{
    Debug.Assert(
        RatesMatch(ctx.DeltaSeconds, _settings.FixedDt),
        "PhysicsSystem's fixed step and the accumulator's tick rate have drifted apart — configure them to match.");
    _world.StepPhysics(in _settings);
}
```

`PhysicsSettings`: **no change**. The remark at `PhysicsSystem.cs:12-16` is updated: `DeltaSeconds` is no longer
"deliberately ignored" — it is now *checked* against the fixed step and must match, because the accumulator is what
guarantees they do.

### 5. `samples/Sandbox/Program.cs` (modified)

The `window.Rendered` handler (`Program.cs:766`) selects the wall-clock delta:

```csharp
// Capture/bench reproducibility (MP-0c decision 2): when AGAPANTHE_MAX_FRAMES is set, feed a constant dt equal to
// the fixed tick period, so the accumulator runs exactly one tick per frame and the run is reproducible run-to-run.
var captureMode = maxFrames > 0;
var wallClockDt = captureMode ? orchestrator.FixedTickDeltaSeconds : (float)dt;
orchestrator.Tick(wallClockDt);
frameRenderer.DrawFrame(orchestrator.RenderDelegate);
orchestrator.EndFrame();
```

`maxFrames` defaults to `-1` and parses to a non-negative value when `AGAPANTHE_MAX_FRAMES` is set (`Program.cs:46`).
`> 0` — not `>= 0` — is the right gate (R2, review round 2): `AGAPANTHE_MAX_FRAMES=0` disables the auto-close
(`Program.cs:803`, `maxFrames > 0`) and does **not** run a capture, so treating it as capture mode would feed the
synthetic `dt` to a normally-windowed interactive session and reintroduce the exact frame-rate coupling this
milestone removes. `> 0` also matches the two other guards in the file (auto-close `:803`, UI-capture arming `:797`
`maxFrames > 1`).

**The synthetic `dt` is gated on `AGAPANTHE_MAX_FRAMES` alone.** A run that sets `AGAPANTHE_CAPTURE` or
`AGAPANTHE_CULL_STATS=1` **without** `AGAPANTHE_MAX_FRAMES` consumes the real wall-clock `dt` and is **not**
reproducible run-to-run — where today it happens to be, because nothing consumes `dt`. Every deterministic capture
and bench protocol in the repo already sets `AGAPANTHE_MAX_FRAMES` (the capture protocol below, the cull-stats bench,
`launchSettings.json`), so this changes no existing workflow, but it is a rule now rather than an accident: **a
deterministic run must set `AGAPANTHE_MAX_FRAMES`.** Stated in `CLAUDE.md`'s env-var list at CONVERGE.

`FrameOrchestrator.CreateDefault` at `Program.cs:477` keeps its current call — the two new parameters take their
defaults (`1/60`, `0.25`), which is exactly today's behaviour at a 60 Hz-dividing frame rate.

`samples/HeadlessSim/Program.cs`: **unchanged** (§Context). Its discrete `host.Tick(FixedDt)` loop
(`HeadlessSim/Program.cs:106-111`) is already the right "no real time" behaviour for a deterministic batch; the
snapshot hash stays `7e8dc68f5a25914c84677a7a53ad3a58`.

### 6. `BenchSpinSystem` and the bench path

`BenchSpinSystem` and `AGAPANTHE_CHURN` drive per-tick work and do not read the tick index (verified). Under the
capture protocol (`AGAPANTHE_MAX_FRAMES` set) they run exactly once per frame, as today. In an interactive
`benchMode` run without `AGAPANTHE_MAX_FRAMES` they may run N times per frame under catch-up — acceptable: the bench
readout is a wall-clock per-frame cost, and "the frame did N ticks of work" is a truthful measurement of a machine
that is behind. No change.

## Measured blast radius (verified this session — do not re-search)

| Site | What changes |
|---|---|
| `SystemScheduler.cs:37,50,103,116` | `_frameIndex`/`FrameIndex` → `_tickIndex`/`TickIndex` |
| `Systems.cs:13-26` (`TickContext`) | ctor param + property → `TickIndex` |
| `SimulationHost.cs:58-59` | forwarding property → `TickIndex` |
| `SimulationHost.cs:61-71` (`CurrentTick`) | off-by-one fix + doc rewrite |
| `PhysicsSystem.cs:12-16,29` | remark rewrite + `Debug.Assert` |
| `FrameOrchestrator.cs:70,95-97,108-122,146-153` | accumulator field, two `CreateDefault` signatures, `Tick` body, `FixedTickDeltaSeconds` |
| `samples/Sandbox/Program.cs:766` | `wallClockDt` selection |
| `samples/HeadlessSim/Program.cs:114` | `host.FrameIndex` → `host.TickIndex` (rename only) |
| `tests/…/SchedulerTests.cs:64-92` | `FrameIndex` → `TickIndex`; `:91` comment about "counter advances after the stages" stays true |
| `tests/…/HeadlessSimulationTests.cs:118` | `host.FrameIndex` → `host.TickIndex` |
| `tests/…/RenderBarrierTests.cs:28-29,61,108,114-118` | `frameIndex` param + `tick.FrameIndex` → `TickIndex`; `Render_DoesNotAdvanceTheTickIndex` already named right |
| `tests/…/RenderStageNeutralityTests.cs:105-108,187` | **code + comment**, not comment alone (F5): `:108` must become `new TickContext(Dt, Math.Max(0L, scheduler.TickIndex - 1))` so the test keeps matching what `FrameOrchestrator.cs:84` builds after the off-by-one fix — leaving the code at `scheduler.FrameIndex` would make the test stop reflecting production and quietly void the only guard §Risks cites for a `CurrentTick` drift. `:187` `ctx.FrameIndex` → `TickIndex` |

**Nothing outside this table.** `grep FrameIndex` over the repo also hits `DeletionQueueTests.cs`
(`completedFrameIndex:` — a `GraphicsDevice` parameter, unrelated) and `RenderStageNeutralityTests.cs`'s test system
that branches on `ctx.FrameIndex == 5` (renamed with the rest). `LandingChallengeRule` / `ProbeDropSystem` do **not**
read the tick index (verified — their "every N ticks" cadence is a separate counter) and benefit from the decoupling
with no code change.

## Testing strategy

| Test | Wave | Kind | What breaks without it |
|---|---|---|---|
| **`Advance` runs the right tick count**: `dt = 5·fixed` → returns 5; `dt = 0.5·fixed` → returns 0, remainder carried; next `Advance(0.5·fixed)` → returns 1. Use `dt = n · accumulator.FixedDeltaSeconds` — **never** `n/60f`, which differs by 1 ULP (F1) | **W1** | GPU-free unit | the core loop is wrong and every downstream gate is meaningless |
| **Input clamp**: `Advance(host, 10f)` with a 250 ms ceiling runs ≤ 15 ticks, not ~600 | **W1** | GPU-free unit | one long frame stalls the process in a catch-up burst (spiral of death) |
| **Constructor validation**: `fixed <= 0`, `max < fixed` throw `ArgumentOutOfRangeException` | **W1** | GPU-free unit | a silently dead accumulator (never ticks) or a divide/`while(true)` |
| **Non-finite / negative `dt`** does not corrupt the accumulator (Release path): after `Advance(host, NaN)` a subsequent `Advance(host, fixed)` still returns 1 | **W1** | GPU-free unit | a `NaN` accumulator freezes the sim permanently with no error (F6) |
| **`Advance` is 0-alloc after warmup** (loop of `Advance` calls, `GC.GetAllocatedBytesForCurrentThread` delta 0) | **W1** | GPU-free alloc gate | the accumulator allocates per frame — the continuously-displayed 0-alloc gate is lost |
| **Tick-count equivalence** (decision 8, primary): two wall-clock `dt` profiles totalling the same simulated time → the **same total tick count**, asserted as integers. Profile A = 20 × `Advance(host, 3·fixed)`, profile B = 60 × `Advance(host, 1·fixed)`, both → **60** (F1: `3·fixed` computed from `accumulator.FixedDeltaSeconds`, not `3f/60f`) | **W2** | GPU-free unit | the accumulator's tick count depends on how wall-clock time is chunked — the milestone's actual goal, and a hash cannot show it |
| **Position equivalence** (decision 8, integration check): same two profiles → **bit-for-bit identical** final positions. Secondary to the count assertion — with decision 3, equal counts *imply* equal positions, so this guards the accumulator→host→`PhysicsSystem` wiring, not the decoupling itself (F4) | **W2** | GPU-free unit | a wiring bug (wrong `dt` forwarded, tick dropped) that the count test alone would miss |
| **Step-count sensitivity** (companion, keeps the two above non-vacuous): a third profile of 30 ticks ends in a *different* position from the 60-tick runs | **W2** | GPU-free unit | a settled scene passes both equivalence assertions trivially |
| **`FrameOrchestrator`-shape wiring + catch-up** (F3): replay `BeginFrame(); Advance(host, dt); … EndFrame()` — the exact `FrameOrchestrator.Tick` sequence — over a catch-up profile (e.g. one `dt = 4·fixed` frame among `dt = fixed` frames). Assert (a) `Stats.FrameCount` == number of frames, not ticks; (b) `SimulationHost.LastFrameTickCount` == 4 on the slow frame, 1 elsewhere; (c) total ticks correct | **W2** | GPU-free unit | the N>1-tick-per-frame path — the whole point — is exercised by no test, no hash, no capture (decision 2 forces 1/frame in capture mode) |
| **`CurrentTick` reports the last executed tick**: fresh host → `CurrentTick.TickIndex == 0`; after 1 `Tick` → `0`; after 3 `Tick` → `2` | **W2** | GPU-free unit | the off-by-one silently persists or inverts — currently pinned by nothing |
| **`TickContext` inside tick N still sees `TickIndex == N`** (pre-increment view unchanged) | **W2** | GPU-free unit | the rename accidentally shifts the value systems observe mid-tick |
| **`PhysicsSystem.RatesMatch`**: `true` for equal rates, `false` for 1/30 vs 1/60 (the extracted predicate, since a failed `Debug.Assert` cannot be caught in-process — R3) | **W2** | GPU-free unit | an accumulator/physics mismatch integrates silently at the wrong rate |
| Existing scheduler/headless tests green after rename (`SchedulerTests`, `HeadlessSimulationTests`, `RenderBarrierTests`, `RenderStageNeutralityTests`) | **W2** | existing | the rename broke a guarantee |
| `AotComponentProbe` | W3 | AOT publish + run | `FixedTimestepAccumulator` is missing native code under ILC (it is a plain class — low risk, but the gate is cheap) |
| Sandbox headless capture HDR `12638edd` / UI `03421357` **unchanged** across 3 runs; `HeadlessSim` JIT == AOT at its **unchanged** hash | W3 | run-level | a "pure decoupling" moved a pixel or a byte it must not have (F2) |

**The equivalence test, spelled out** (decision 8), because both a naive version and the brainstorm's own example
cannot pass as stated:

*The float trap (F1).* The brainstorm framed this as "30 calls of 33 ms vs 10 calls of 100 ms → identical". Run
literally, that gives **59 ticks vs 60**: `0.033f` and `0.1f` are not exact multiples of `1f/60f`, and even
`3f/60f` vs `3f * (1f/60f)` differ by 1 ULP — the first `Advance` of a "3 ticks" profile yields 2, and the profile
ends one tick short of the fine-grained one. So the two profiles are constructed **from the accumulator's own
`FixedDeltaSeconds`**: `3f * accumulator.FixedDeltaSeconds`, never `3f/60f`. Verified: `20 × Advance(3·fixed)` →
exactly 60 ticks, matching `60 × Advance(1·fixed)`.

*What the test actually proves (F4).* Because decision 3 has `PhysicsSystem` ignore `ctx.DeltaSeconds`, two runs
that issue the **same number** of `StepPhysics(1/60)` calls produce bit-identical positions *by construction* — the
position assertion is not where the decoupling is proven. The decoupling is proven by the **integer tick count**
being independent of how wall-clock time is chunked. So:

- **Primary assertion — counts, as integers.** Sum the return values of every `Advance` call. Profile A
  (20 × `Advance(host, 3·fixed)`) and profile B (60 × `Advance(host, 1·fixed)`) must both total **60**. This is the
  claim "sim speed does not depend on frame rate", stated in the one quantity that carries it.
- **Secondary assertion — positions, bit-for-bit.** With the counts equal, identical positions are expected; this
  assertion therefore guards the *wiring* (accumulator forwards the right `dt`, drives `host.Tick` the right number
  of times, no tick silently dropped), not the decoupling. Stated as such so a future reader does not mistake it for
  the headline.
- **Companion assertion — step-count sensitivity.** A third profile totalling **30** ticks must end in a *different*
  position from the 60-tick runs. Without it, a scene that has already settled passes both assertions above
  vacuously. Model the scene on `ContactResolutionOrderTests` (`ContactResolutionOrderTests.cs:38-61`): a
  `[Collection("World")]` test, an asymmetric multi-body overlapping cluster (distinct masses, restitutions,
  velocities), spawned through the public `SpawnBody` API, read back through `GetWorldPosition`.

This runs `SimulationHost` and `PhysicsSystem` for real — register `PhysicsSystem` on the host, let `Advance` drive
`host.Tick` — not `StepPhysics` in isolation. The point is the accumulator → host → system chain.

## Waves

**W1 — `FixedTimestepAccumulator`, alone.** The new type, its constructor validation, the input clamp, the 0-alloc
gate. No existing file changes. Every test here is GPU-free and self-contained.

**W2 — wire it in + the rename + the off-by-one.** `FrameOrchestrator` holds and drives the accumulator, forwards
`FixedTickDeltaSeconds`, `SimulationHost` counts `LastFrameTickCount`; `FrameIndex` → `TickIndex` across the 3
production files + `HeadlessSim` + 5 test files (`RenderStageNeutralityTests.cs:108` changes **code**, not just its
comment — F5); `CurrentTick` fixed; `PhysicsSystem` gains its assert; `Sandbox/Program.cs` selects the capture `dt`.
The tick-count equivalence test and the `FrameOrchestrator`-shape catch-up test are the wave's headline. Existing
scheduler/headless/render tests go green under the rename — and the capture hashes must **already** be unchanged at
the end of W2 (nothing here changes a pixel), which is the fast check before W3's formal 3-run confirmation.

**W3 — captures + tail.** Confirm HDR `12638edd` and UI `03421357` **unchanged** across 3 runs (F2 — a moved hash is
a regression to chase, not a result to re-pin), confirm `HeadlessSim` JIT == AOT at its **unchanged** hash,
`AotComponentProbe`, self-review of the diff, double audit (`csharp-lowlevel` + `engine-architect`; no GPU pass is
touched, so `graphics-3d` is not warranted), human verdict on the equivalence tests + a **live interactive run at a
non-60 Hz frame rate** (probe fall speed matches a 60 Hz run — the one thing no automated gate covers, F3), CONVERGE.

W1 and W2 are separate on purpose: W1 must be provable in complete isolation, and it cannot be if it lands in the
same commit as a repo-wide rename.

## Verification (DoD)

- `dotnet build` **0 warning** · `dotnet test` green including every test above.
- **Capture hashes unchanged: HDR `12638edd`, UI overlay-hidden `03421357`** (F2 — same values MP-0a and MP-0b
  carried; decision 2 makes capture mode exactly one tick per frame and no production code reads `ctx.DeltaSeconds`,
  so a changed hash is a regression, not an expected outcome). Capture protocol — do **not** drop the last variable,
  the default of `AGAPANTHE_DROP_EVERY` is 30 (`samples/Sandbox/Program.cs:544`) and omitting it changes the scene:
  `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420 AGAPANTHE_OVERLAY=0 AGAPANTHE_DROP_EVERY=12`, 1280×720, Debug,
  `AGAPANTHE_CAPTURE` + `AGAPANTHE_CAPTURE_UI`, 3 runs.
- **A deterministic run must set `AGAPANTHE_MAX_FRAMES`** (F9) — the synthetic capture `dt` is gated on it alone; a
  capture or bench without it now consumes real wall-clock `dt` and is not run-to-run reproducible. Add this to
  `CLAUDE.md`'s env-var notes at CONVERGE.
- **`HeadlessSim` snapshot hash unchanged**: `7e8dc68f5a25914c84677a7a53ad3a58` (MD5, 1868 bytes), JIT == NativeAOT.
  Reproduce with `dotnet run --project samples/HeadlessSim -c Debug -- --ticks 600 --bodies 8 --save <path>` then
  `Get-FileHash -Path <path> -Algorithm MD5`. `HeadlessSimSnapshotFormatTests` already asserts it in-process — it
  must not need editing, which is itself the proof that the headless path did not move.
- AOT publish reminder (MP-0a): prefix `PATH` with the folder containing **`vswhere.exe`**
  (`C:\Program Files (x86)\Microsoft Visual Studio\Installer`), not the MSVC folder, or ILC fails with code 123.
- Sandbox headless: **0 validation message**, **0 leak**.
- `AotComponentProbe` PASS.
- **0 alloc/frame** in steady state (bench + churn), verified in the overlay and the cull-stats bench.
- Double audit PASS + human verdict, then CONVERGE (`AVANCEMENT` / `BACKLOG` / `CLAUDE` updated, board archived).
  **Commit on explicit request only.**

## Risks

- 🟠 **Capture judder from a non-dividing frame rate is real, and the capture gate deliberately hides it.** Decision 2
  feeds a synthetic `dt` in capture mode, so the captured frames are always exactly one tick apart — the judder that
  decision 1 accepts is invisible in the gate. That is the right trade (a run must be reproducible). It is assessed
  only by the W3 human verdict item: a live interactive run at a non-60 Hz frame rate, checking probe fall speed
  matches a 60 Hz run. Called out so it is a decision on the record, not a surprise.
- 🟠 **The `Debug.Assert` in `PhysicsSystem` is Debug-only.** A Release build with a mis-wired accumulator (tick rate
  ≠ `FixedDt`) integrates physics at the wrong rate with no signal. This matches the project's other guards (the
  frame-measurement thread assert, `SimulationHost.cs:143`), and a hard throw on every tick was judged worse; the
  `RatesMatch` unit test plus the tick-count equivalence test are the CI-level backstop.
- 🟠 **`CurrentTick`'s off-by-one was pinned by no test, so the fix has no regression baseline** beyond the new tests
  it ships with. If any render-side code implicitly depended on seeing `N+1` (none found — `grep CurrentTick` returns
  two hits, both listed), it would shift by one silently. The neutrality tests (`RenderStageNeutralityTests`) are the
  guard that a render system's *observable output* did not change — provided its manually-built context is updated in
  **code**, not just its comment (F5, blast-radius table).
- 🟠 **`DebugOverlaySystem` / `UiRenderSystem` run N× under catch-up** (decision 6). Functionally correct, wasteful.
  Documented debt for a future UI pass; not this milestone. `SimulationHost.LastFrameTickCount` at least makes it
  countable (logged by the bench line), even though it is not wired to the on-screen overlay this milestone (F8).
- 🟡 **The `250 ms` ceiling is a guess.** ~15 ticks at 60 Hz. Too low and a legitimate hitch loses simulated time
  visibly; too high and a real stall still bursts. It is a constructor parameter with a default, tunable without a
  refactor.
- 🟡 **`FixedTimestepAccumulator` under NativeAOT.** A plain non-generic class with no reflection — expected to be
  trivially rooted, but `AotComponentProbe` covers it at no cost.
- 🟡 **Accumulator remainder and `float` drift — measured, not open** (audit LL F-8). Repeatedly subtracting
  `FixedDeltaSeconds` from a `float` accumulator accumulates rounding error. The `csharp-lowlevel` audit ran the
  loop: feeding the exact `FixedDeltaSeconds` a million times leaves a residue of **exactly `0f`** (so capture-mode
  determinism is bit-exact, not "approximately"); one hour at 60 fps with dt jittered ±50 % drifts **5.5 × 10⁻⁷ s**
  cumulative. The remainder is always `< FixedDeltaSeconds` and the `dt` *passed* to a tick is always exactly
  `FixedDeltaSeconds`, so a single tick's determinism is untouched; only the *number* of ticks in a given wall-clock
  window can shift by one. **This is why the equivalence test builds its profiles from
  `accumulator.FixedDeltaSeconds` rather than `n/60f`** (F1). A `double` accumulator is a trivial later change if it
  ever matters; v1 stays `float` to match `TickContext.DeltaSeconds` and `PhysicsSettings.FixedDt`.

## Out of scope (explicit)

Visual interpolation between ticks (a dedicated future milestone — no previous-state infrastructure exists) ·
restructuring `DebugOverlaySystem` / `UiRenderSystem` to a render cadence (documented debt) · MP-0d (input →
timestamped commands, the next sub-milestone) · any change to the snapshot format (MP-0b closed it, MP-0c does not
reopen it) · a variable or adaptive tick rate · multi-threaded ticking / a job system · `Agapanthe.Engine.Render` →
`Agapanthe.Engine.Presentation` rename (MP-0a 🟡, unrelated). The remaining MP-0 sub-milestone keeps its backlog
entry and its order.
