# Board — Job-1: inter-system parallelism on `Stage.Simulation`

Status: **completed** (2026-09-16) — all waves executed, double audit findings applied, regression gate green.
See spec's "Outcome (post-execution, post-audit)" section for the full closure summary.

Spec: `docs/plans/2026-09-15-job-system-foundations-design.md` (approved 4.2/5)

## Rollback Point

`abdd1d5557a48504d09a5fd728ef7621a463a94b` (HEAD at DECOMPOSE time; working tree otherwise clean
except this board + the spec file, both untracked).

## Project Conventions

- .NET 10, `dotnet build` / `dotnet test` from repo root.
- TDD: write the failing test before the implementation for every task below.
- 0-alloc hot path is a hard gate (`GC.GetAllocatedBytesForCurrentThread()` delta after warmup).
- `[Conditional("DEBUG")]` guards throw `InvalidOperationException`, never `Debug.Assert`, when
  crossing a thread/queue-ownership boundary (established pattern in `GameWorld.AssertOwnerThread`
  and `SimCommandQueue.AssertOwnerThread`).
- No `Task.Run`/`Parallel.Invoke`/`ThreadPool.QueueUserWorkItem` anywhere on this path (D5 — they
  allocate per call).
- Double audit before close: `csharp-lowlevel` + `engine-architect`.
- Never commit/push without explicit user request.

## Dependency Graph

```
Wave 1 (parallel-safe, disjoint files)
  JS-001 (Systems.cs)  JS-002 (GameWorld.cs)  JS-003 (SimCommandQueue.cs)
        \                    |                    |
         \                   |                    |
Wave 2    JS-004 (PhysicsSystem.cs)   JS-005 (SystemScheduler.cs: wave algorithm)
              (needs JS-001)              (needs JS-001)
                                               |
Wave 3                                    JS-006 (SystemScheduler.cs: worker pool +
                                           parallel Tick + sanctioned-thread wiring)
                                           (needs JS-002, JS-003, JS-005)
                                               |
Wave 4          JS-007 (parallel scheduler test suite)   JS-008 (stale doc comments rewrite)
                          (needs JS-006)                          (needs JS-006)
                                               |
Wave 5 (tail, sequential)
   JS-009 Self Code Review -> JS-010 Requirements Validation -> JS-011 Full Project Verification
```

## Tasks

### JS-001 — `ISystem` gains `Reads`/`Writes`/`RequiresExclusiveExecution`
- **Type**: code · **Size**: S · **Deps**: none
- **Files**: `src/Agapanthe.Engine/Systems.cs`
- Add three default-implemented interface members per spec Architecture section:
  ```csharp
  IReadOnlyList<Type> Reads => Array.Empty<Type>();
  IReadOnlyList<Type> Writes => Array.Empty<Type>();
  bool RequiresExclusiveExecution => true;
  ```
- Do **not** rewrite the "parallelises nothing" doc remark yet (JS-008 does that, once the real
  behavior is final) — but do NOT let it contradict compilable reality in the meantime; leave a
  `// TODO(Job-1): stale, see JS-008` marker on that paragraph so it can't be missed.
- **Tests**: existing `SchedulerTests.cs`/implementors (`LandingChallengeSystem`, `ProbeDropSystem`,
  `PhysicsSystem`, `DebugOverlaySystem`, `UiRenderSystem`, `DriveControlSystemFactory`'s system,
  `PropagateSystem`) still compile untouched — this is a compile-time acceptance criterion, no new
  test file needed. Add one small test asserting the three defaults on a bare minimal `ISystem`
  implementation (empty `Reads`/`Writes`, `RequiresExclusiveExecution == true`).
- **Acceptance**: `dotnet build` 0 warnings; the 7 known implementors compile with zero changes.

### JS-002 — `GameWorld.AssertOwnerThread` sanctioned-worker allowlist
- **Type**: code · **Size**: S · **Deps**: none
- **Files**: `src/Agapanthe.World/GameWorld.cs` (around line 66/169, see current
  `_ownerThreadId`/`AssertOwnerThread`)
- Add `private HashSet<int>? _sanctionedWorkerThreadIds;` and a setter (e.g.
  `internal void SetSanctionedWorkerThreads(IReadOnlyCollection<int> ids)`) the scheduler calls
  once at worker-pool creation. `AssertOwnerThread` passes if
  `Environment.CurrentManagedThreadId == _ownerThreadId` **or** the id is in the sanctioned set;
  otherwise unchanged throw. `[Conditional("DEBUG")]` unchanged.
- **Tests**: new/extended test — a sanctioned thread (spawn a `Thread`, register its id, call a
  method that asserts owner thread) passes; an unrelated unsanctioned thread still throws
  (regression guard against D2 disabling the check for everyone).
- **Acceptance**: existing `AssertOwnerThread` call sites (15 of them, per current grep) unaffected
  when no sanctioned set is configured (`null` = today's exact behavior).

### JS-003 — `SimCommandQueue.AssertOwnerThread` sanctioned-worker allowlist
- **Type**: code · **Size**: S · **Deps**: none
- **Files**: `src/Agapanthe.Engine/SimCommandQueue.cs` (its own independent copy, line ~120)
- Mirror JS-002 exactly: `HashSet<int>?` field + setter, `AssertOwnerThread` passes for owner OR
  sanctioned thread.
- **Tests**: same shape as JS-002's — sanctioned thread passes `Enqueue`/`DrainUpTo`, unsanctioned
  thread still throws.
- **Acceptance**: `SimCommandQueue`'s existing re-entrancy/FIFO tests unaffected.

### JS-004 — `PhysicsSystem.RequiresExclusiveExecution => true`
- **Type**: code · **Size**: S · **Deps**: JS-001
- **Files**: `src/Agapanthe.Engine/PhysicsSystem.cs`
- One-line override: `public bool RequiresExclusiveExecution => true;` — it calls
  `GameWorld.StepPhysics`, which owns `_cellHead`/`_cellNext` shared broadphase scratch
  (`GameWorld.Physics.cs`), invisible to the `Reads`/`Writes` conflict model (D4).
- **Tests**: regression pin — `new PhysicsSystem(...).RequiresExclusiveExecution == true`.

### JS-005 — `SystemScheduler`: wave-grouping algorithm (pure, no threading yet)
- **Type**: code · **Size**: M · **Deps**: JS-001
- **Files**: `src/Agapanthe.Engine/SystemScheduler.cs`
- Implement the greedy wave computation described in the spec, scoped to `Stage.Simulation`
  systems only: at first `Tick` (`_frozen` transition), compute once and cache a
  `List<List<ISystem>>` (or equivalent 0-alloc-after-warmup structure) of waves. Each system joins
  the earliest wave with no conflict (non-empty `Writes`∩`Writes`, `Writes`∩`Reads`, or
  `Reads`∩`Writes` against anything already in that wave); any system with
  `RequiresExclusiveExecution == true` gets its own solo wave. `Stage.Input`/`PostSimulation` keep
  today's flat sequential loop — untouched.
- Do **not** wire actual threads yet (JS-006) — `Tick` can execute the computed waves
  sequentially in this task, proving the grouping is correct before adding concurrency risk.
- **Tests**: new test file (e.g. `SystemSchedulerWaveGroupingTests.cs`) — two systems with disjoint
  `Reads`/`Writes` end up in the same wave; two systems with overlapping `Writes` end up in
  different waves; a `RequiresExclusiveExecution` system is always alone in its wave; wave
  computation happens once (cached) and registration after first `Tick` still throws (existing
  `ThrowIfFrozen` behavior preserved).
- **Acceptance**: `Stage.Input`/`PostSimulation` behavior and existing `SchedulerTests.cs` all pass
  unmodified (sequential-equivalent output for `Stage.Simulation` too, since no real concurrency
  yet).

### JS-006 — `SystemScheduler`: persistent worker pool + parallel `Tick` execution
- **Type**: code · **Size**: M · **Deps**: JS-002, JS-003, JS-005
- **Files**: `src/Agapanthe.Engine/SystemScheduler.cs`
- At the same `_frozen` moment as JS-005's wave computation, create a persistent worker-thread
  pool (`Environment.ProcessorCount - 1`, floor 1) — **not** `Task.Run`/`Parallel.Invoke` (D5).
  Use a reused array of job descriptors (plain structs) + `SemaphoreSlim`/counter-based signaling
  for dispatch and completion, so nothing is heap-allocated per tick.
- `Tick`'s `Stage.Simulation` loop now: for each wave, dispatch its systems to workers, **full
  join** before the next wave, another full join before the stage's structural barrier fires
  (barrier call itself unchanged — one call, owner thread, after complete join).
- At pool creation, call the JS-002/JS-003 setters with the pool's thread ids.
- **Tests**: none new in this task (JS-007 covers proof-of-parallelism) — but do NOT skip local
  verification that `dotnet test` stays green throughout, since this is the highest-risk task on
  the board.
- **Acceptance**: `dotnet test` green; no `Task`/`Parallel.Invoke` anywhere in the diff (grep
  check); `Stage.Input`/`PostSimulation` untouched by the diff.

### JS-007 — Parallel scheduler test suite (proof of correctness)
- **Type**: test · **Size**: M · **Deps**: JS-006
- **Files**: new `tests/Agapanthe.Tests/SystemSchedulerParallelismTests.cs`
- Cases (per spec Testing strategy):
  1. Real-parallelism proof: two fake systems, disjoint `Reads`/`Writes`, artificial **busy-wait**
     delay (not `Thread.Sleep`) — assert wall-clock `Tick` time < sum of both delays.
  2. Correctness proof: parallel execution of two systems yields the same final state as running
     them sequentially, for a scripted scenario with a known expected outcome.
  3. Conflict detection: two systems with overlapping `Writes` never run concurrently (assert via
     a shared synchronization primitive both touch, or by inspecting the computed wave grouping).
  4. `RequiresExclusiveExecution` isolation: such a system never shares a wave with anything else.
  5. `AssertOwnerThread`/`SimCommandQueue.AssertOwnerThread` regression: a sanctioned worker thread
     passes, an unrelated unsanctioned thread still throws (may already be covered by JS-002/
     JS-003's own tests — do not duplicate, cross-reference instead).
  6. 0-alloc-after-warmup on `Tick` with active parallel waves (warmup call before the measured
     loop, `GC.GetAllocatedBytesForCurrentThread()` delta == 0).
- **Acceptance**: all 6 cases pass reliably (no flakiness from timing — use generous margins on
  the wall-clock assertion, consistent with how this project has handled timing-sensitive tests
  elsewhere).

### JS-008 — Rewrite stale doc comments
- **Type**: docs · **Size**: S · **Deps**: JS-006
- **Files**: `src/Agapanthe.Engine/Systems.cs`, `src/Agapanthe.Engine/SystemScheduler.cs`
- Replace the "the scheduler parallelises nothing... a job system is a separate question" (`ISystem`
  remark) and "Single-threaded. The scheduler parallelises nothing... a job system is a separate
  design" (`SystemScheduler` class doc) with accurate descriptions of the wave-based parallel
  execution now shipped for `Stage.Simulation`, including the explicit scope boundary
  (`Stage.Input`/`PostSimulation` remain sequential) and a pointer to `Reads`/`Writes`/
  `RequiresExclusiveExecution` as the declaration mechanism. Remove the `// TODO(Job-1)` marker
  from JS-001.
- **Acceptance**: no lingering claim that the engine has zero parallelism; doc accurately scopes
  what is and isn't parallel.

### JS-009 — Self Code Review (tail, mandatory)
- **Type**: code · **Size**: S · **Deps**: JS-001…JS-008
- Full diff self-review against the spec's Decision Log (D1-D6): confirm no `Task`/
  `Parallel.Invoke` anywhere, `RequiresExclusiveExecution` truly defaults `true`, sanctioned-thread
  allowlists don't weaken the Debug guard for non-sanctioned callers, wave computation is
  genuinely 0-alloc after warmup.

### JS-010 — Requirements Validation (tail, mandatory)
- **Type**: code · **Size**: S · **Deps**: JS-009
- Walk the spec's Testing strategy and Out-of-scope sections line by line; confirm every item is
  either delivered or explicitly, correctly deferred (not silently dropped). Confirm
  `Stage.Render`/`RenderSystemScheduler` and `Stage.Input`/`PostSimulation` are untouched
  (`git diff` should show 0 lines outside `Agapanthe.Engine`/`Agapanthe.World`/tests).

### JS-011 — Full Project Verification (tail, mandatory)
- **Type**: code · **Size**: M · **Deps**: JS-010
- `dotnet build` 0 warnings; `dotnet test` full suite green; all 9 pinned Sandbox/TopDown captures
  + `HeadlessSim` snapshot hash re-verified byte-identical via `git stash` A/B against
  `abdd1d5557a48504d09a5fd728ef7621a463a94b`; AOT publish + JIT == NativeAOT re-confirmed on
  Sandbox, TopDown, and HeadlessSim; then dispatch the double audit (`csharp-lowlevel` +
  `engine-architect`), apply findings, and re-verify anything the findings touch.

## Post-audit fixes (Wave 5, JS-009/010/011)

Double audit (`csharp-lowlevel` 3.4/5, `engine-architect` 3.5/5, both PASS-with-concerns) found real issues,
fixed in this order:
- **F1 (blocking)**: the sanctioned-thread allowlist was applied to ALL 25 `GameWorld.AssertOwnerThread` call
  sites, including every structural mutator (`Spawn`/`Despawn`/`Save`/`Load`/`StepPhysics`/the broadphase-scratch
  query methods) — none of which are safe for concurrent access. Split into `AssertOwnerThread` (read-only, honors
  the allowlist — only `IsAlive` uses it) and `AssertOwnerThreadStrict` (owner-only, never honors the allowlist —
  every mutator uses it). The original test suite had pinned the dangerous case (`Spawn` from a worker not
  throwing) as the intended guarantee; rewritten to pin the opposite.
- **Thread/handle leak (engine-architect 🔴-2)**: `SystemScheduler` now implements `IDisposable` — wakes,
  joins, and disposes the worker pool. `SimulationHost` too (thin passthrough). Every test that forces a
  multi-system wave now uses `using`.
- **F2**: `SetSanctionedWorkerThreads` unions rather than replaces (two independent pools sharing one `GameWorld`
  must not un-sanction each other), Volatile.Read/Write publication, and `GameWorld`'s copy is now `internal`
  (added `InternalsVisibleTo("Agapanthe.Engine")`) instead of a public surface.
- **F5**: `WorkerLoop`'s entire iteration is now wrapped in an outer catch — previously an exception from `Wait()`
  itself or the disposal-check path would kill a worker silently and hang the owner forever with no diagnostic.
  Debug builds also time out a wave's join at 30s with a named exception (Release keeps the unbounded wait).
- **F6**: worker pool size is now capped by the widest wave a scheduler will ever actually run, not a flat
  `ProcessorCount - 1` — a 2-system wave on a 32-core machine now spins up 1 worker, not 31 idle ones.
- **F8**: `SystemScheduler` gained its own Debug-only owner-thread assert (mirroring `GameWorld`/`SimCommandQueue`),
  closing the one engine type that owned threads but had no anchor of its own.
- **F4**: added exception-propagation tests (single throw, two throws → `AggregateException`, and a tick after a
  throw still ticks correctly) and a 17-system wide-wave partition-coverage test — the uneven-partition arithmetic
  and the whole exception path had zero test coverage before. Verified by mutation (temporarily broke the
  exception capture, confirmed the new tests fail, restored).
- **F9/F10/F12**: added `[Collection("World")]` to the three new `GameWorld`-touching test classes; renamed/fixed
  a misleading test whose comment claimed "not wired yet" for behavior that had already shipped; exposed
  `_simulationWaveExclusive` via a test accessor instead of leaving it write-only.
- **SimulationHost's stale class doc** ("Single-threaded. The scheduler parallelises nothing...") rewritten to
  name the real blind spot: worker-thread allocations are invisible to `LastFrameAllocatedBytes` (a per-thread
  counter) — harmless today since no real system ever triggers pool creation in production, explicitly flagged
  as debt for whichever future sub-milestone adds a second real `Stage.Simulation` system.

**Accepted, not fixed**: engine-architect's 🟠-1/csharp-lowlevel's F11 (owner thread idles during a wave instead
of running one partition itself) — a real perf/utilization nicety, correctness-neutral, deferred to avoid adding
fresh complexity to already-audited code under time pressure. csharp-lowlevel's F3 (wiring worker-thread
allocations into `FrameStats`) — same rationale, same "no live production trigger yet" backstop.

**New finding during fix verification**: none of the new tests disposed their `SystemScheduler`/`SimulationHost`
instances, so every multi-system-wave test leaked a live worker-thread pool for the rest of the test process.
Empirically proven via A/B stress-testing (23/23 clean on the pre-Job-1 baseline vs. occasional cross-test
interference with Job-1's pool-creating tests present): an unrelated pre-existing test
(`CopySyncStateTests.PlanFrame_IsZeroAlloc_InSteadyState`) flaked at roughly 1-in-10 to 1-in-15 runs due to
background OS thread churn from Job-1's tests, even though `IsBackground = true` prevented it from ever blocking
process exit. Fixed by adding `using` to every `SystemScheduler`/`SimulationHost` construction that can trigger
`EnsureWorkerPool` (~12 sites) — this closed the majority of the interference (unbounded → per-test-scoped
thread lifetime) but a residual, much lower flake rate on that same unrelated test may still exist (inherent to
any test suite that creates and tears down real OS threads near time-sensitive 0-alloc tests). Production is
unaffected either way (the parallel path is unreachable there — see "Why now" in the spec). Documented rather
than chased further, given the diminishing returns and that this is test-infrastructure noise, not a defect in
Job-1's own correctness.

## Deferred Work (out of scope, not dropped)

Per spec: fine-grained resource modeling of `GameWorld`'s physics/query broadphase scratch and
`SimCommandQueue` (sub-milestone 2); runtime-verified declared-vs-actual access safety net
(sub-milestone 3); `Stage.Input`/`PostSimulation`/`Render` parallelism; worker-pool sizing/affinity
tuning; the risk noted by spec review iteration 2 — if no second `Stage.Simulation` system lands
soon, this sub-milestone's complexity has no live payoff yet (accepted, infra-ahead-of-load, per
spec's "Why now" section).
