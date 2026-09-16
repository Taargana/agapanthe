# Job system, foundations: inter-system parallelism on `Stage.Simulation` (Job-1)

## Summary

Backlog §4quater's "job system" item. Today the engine has zero CPU-bound parallel compute
anywhere: `SystemScheduler` (`src/Agapanthe.Engine/SystemScheduler.cs`) runs `Stage.Input`,
`Stage.Simulation`, and `Stage.PostSimulation` as flat, registration-ordered lists on the single
owner thread, and `GameWorld.AssertOwnerThread()`/`SimCommandQueue`'s own copy hard-fail (Debug)
the instant a non-owner thread touches them.

Given the size of the full "job system" backlog item (comparable in scope to all of MP-0
combined), it is decomposed into human-gated sub-milestones, mirroring the MP-0 and Contenu-3c
precedent. **This is sub-milestone 1 ("Job-1")**: a dependency-declaration model, a conflict
graph, and real parallel execution — scoped deliberately to `Stage.Simulation` only.
`Stage.Input`, `Stage.PostSimulation`, and `Stage.Render`/`RenderSystemScheduler` remain fully
sequential, unchanged.

Two real hazards were identified during research and resolved by explicit decision rather than
glossed over:

1. `AssertOwnerThread` would immediately throw the instant any system executes on a worker
   thread. Resolved by a sanctioned-worker-thread allowlist (D2) — a coarse but correct fix; the
   full "declared access verified against actual access" safety net is deferred to sub-milestone
   3.
2. `GameWorld`'s physics/query broadphase scratch buffers (`_cellHead`/`_cellNext`,
   `_qGid`/`_qCenter`/`_qRadius`/`_qCellHead`/`_qCellNext`, etc.) and `SimCommandQueue` are shared
   mutable state entirely invisible to a component-based read/write conflict model. Resolved by a
   coarse `RequiresExclusiveExecution` opt-out flag (D4), default `true` (safe) — any system that
   touches this kind of state simply runs alone. Fine-grained resource modeling is sub-milestone
   2's job.

### Why now, given `Stage.Simulation` has one system today

`PhysicsSystem` is currently the *only* system registered on `Stage.Simulation` across Sandbox,
TopDown, and HeadlessSim (`LandingChallengeSystem`/`PropagateSystem` run in `PostSimulation`,
`ProbeDropSystem`/`UiRenderSystem` in `Input`) — so this sub-milestone's parallel scheduler has,
today, nothing concurrent to actually schedule. This is accepted deliberately, not overlooked:
Job-1 is **infrastructure laid ahead of load**, the same posture MP-0 took (headless split,
`GlobalId`, the fixed-timestep accumulator, and the input-command queue all shipped before any
consumer strictly needed them, because retrofitting thread-safety/determinism into a live system
graph is far more expensive than building on top of rails already in place). Concretely: `Reads`/
`Writes`/`RequiresExclusiveExecution` need to exist and be exercised (proven by real, wall-clock
parallel execution in tests, per the Testing strategy) *before* a second `Stage.Simulation` system
is added — retrofitting them onto an already-populated stage would force every existing system's
declared access to be audited under time pressure instead of at its own leisure. The two systems
used to prove real parallelism (see Testing strategy) are test-only fakes precisely because no
second real `Stage.Simulation` system exists yet; the first real beneficiary lands whenever the
next `Stage.Simulation` system is added (deliberately not part of this sub-milestone's scope).

## Decisions

- **D1 — Scope**: Full inter-system job system is decomposed into gated sub-milestones (mirrors
  MP-0/Contenu-3c). This sub-milestone delivers dependency declaration + conflict graph + real
  parallel execution for `Stage.Simulation` only.
- **D2 — Thread-ownership guard**: `GameWorld.AssertOwnerThread()` and `SimCommandQueue`'s
  independent copy each gain a sanctioned-worker-thread allowlist (`HashSet<int>?`), populated
  once by the scheduler's worker pool at startup. A call passes if the calling thread is the
  original owner thread *or* is in the sanctioned set; any other thread still throws
  (`[Conditional("DEBUG")]`, unchanged). Verifying that a system's *actual* access matches its
  *declared* `Reads`/`Writes` is explicitly deferred to sub-milestone 3.
- **D3 — Access declaration**: `ISystem` declares component access via interface properties
  (`IReadOnlyList<Type> Reads { get; }` / `Writes { get; }`), not attributes/reflection — avoids
  any NativeAOT/trimming risk.
- **D4 — Unmodeled shared state**: `GameWorld`'s physics/query broadphase scratch and
  `SimCommandQueue` are not finely resource-modeled in this sub-milestone. A coarse
  `bool RequiresExclusiveExecution` (deliberately named generically, not GameWorld-specific)
  forces a system into its own solo wave, never running in parallel with anything else. Proper
  fine-grained tagging is sub-milestone 2.
- **D5 — Execution mechanism**: A **persistent worker-thread pool**, created once (not
  per-frame), driven by low-level signaling (`SemaphoreSlim`/counter-based handshake) — not
  `Task.Run`/`Parallel.Invoke`, which allocate a `Task` object per call and would violate the
  project's locked "0 alloc managed per frame" gate (visible live in the debug overlay since
  UI-2). These same worker threads populate D2's sanctioned-thread set.
- **D6 — Safe default**: `RequiresExclusiveExecution` defaults to `true` via a C# default
  interface member. An existing, unaudited `ISystem` implementation stays exactly as sequential
  as it is today; parallel eligibility is an explicit opt-in after review, never an accidental
  consequence of empty default `Reads`/`Writes`.

## Architecture

### `ISystem` (src/Agapanthe.Engine/Systems.cs — or wherever `ISystem` is currently declared)

Three new default-implemented members. Every existing implementor compiles unchanged.

```csharp
public interface ISystem
{
    IReadOnlyList<Type> Reads => Array.Empty<Type>();
    IReadOnlyList<Type> Writes => Array.Empty<Type>();
    bool RequiresExclusiveExecution => true; // safe default — opt-in required
    void Execute(in TickContext ctx);
}
```

### `SystemScheduler` (src/Agapanthe.Engine/SystemScheduler.cs)

- **Wave computation** (once, at the existing `_frozen` moment on first `Tick`, cached and reused
  every tick thereafter — 0-alloc): for `Stage.Simulation`'s registered systems only, a greedy
  pass in registration order assigns each system to the earliest wave with no conflict against
  everything already placed in it. Conflict = non-empty intersection of `Writes`∩`Writes`,
  `Writes`∩`Reads`, or `Reads`∩`Writes` between two systems. Any system with
  `RequiresExclusiveExecution == true` is placed alone in its own wave. `Stage.Input` and
  `Stage.PostSimulation` are untouched — they keep today's flat sequential loop.
- **Worker pool**: `Environment.ProcessorCount - 1` threads (floor 1), created once at the same
  `_frozen` moment, not per-frame. Dispatch/completion via low-level signaling (a reused array of
  job descriptors — plain structs, not heap-allocated per tick — plus `SemaphoreSlim` or an
  equivalent counter-based handshake). No `Task`/`Parallel.Invoke` anywhere on this path.
- **Per-tick execution**: `Stage.Simulation`'s waves run in order. Each wave's systems are
  dispatched to workers; the scheduler does a **full join** before starting the next wave, and
  another full join before the stage's structural barrier fires. The barrier itself is unchanged
  — one call, on the owner thread, only after every system in every wave has completed.
- At worker-pool creation, the pool's thread ids populate the sanctioned-thread set on both
  `GameWorld` and `SimCommandQueue` (D2).

### `GameWorld.AssertOwnerThread()` (src/Agapanthe.World/GameWorld.cs) and `SimCommandQueue`'s copy (src/Agapanthe.Engine/SimCommandQueue.cs)

Each gains a `HashSet<int>?` sanctioned-worker-ids field and a setter the scheduler calls once at
pool creation. The assert passes if `Environment.CurrentManagedThreadId` equals the captured
owner id *or* is present in the sanctioned set; otherwise it throws exactly as it does today.
`[Conditional("DEBUG")]` unchanged.

### `PhysicsSystem` (src/Agapanthe.Engine/PhysicsSystem.cs)

`RequiresExclusiveExecution => true` — it calls `GameWorld.StepPhysics`, which owns the shared
physics broadphase scratch (`_cellHead`/`_cellNext` in `src/Agapanthe.World/GameWorld.Physics.cs`
— D4).

### Stale doc comments

`ISystem`'s XML doc and `SystemScheduler`'s class doc currently state, respectively, that the
scheduler "parallelises nothing" and that it is "single-threaded... a job system is a separate
design." Both become false the moment this sub-milestone lands and must be updated as part of
this work, not left to silently rot.

## Testing strategy

- **Real-parallelism proof**: two fake systems with disjoint `Reads`/`Writes` and an artificial
  busy-wait delay (not `Thread.Sleep`, which can mask thread-pool starvation) — assert wall-clock
  time is less than the sum of both delays. Proves actual concurrent execution, not merely "did
  not crash."
- **Correctness proof**: running the two systems through the parallel scheduler produces the same
  final state as running them sequentially, for a scripted scenario with a known expected
  outcome.
- **Conflict detection**: two systems declaring an overlapping `Writes` type are placed in
  different waves and never run concurrently — asserted either via a shared synchronization
  primitive both systems touch, or by inspecting the wave-grouping algorithm's output directly.
- **`RequiresExclusiveExecution` isolation**: a system with the flag set never shares a wave with
  any other system.
- **`AssertOwnerThread` regression guard**: a sanctioned worker thread passes; an unrelated,
  unsanctioned thread still throws — D2 must not accidentally disable the check for everyone.
- **0-alloc-after-warmup** on `Tick` with active parallel waves (mirrors every other 0-alloc test
  in this codebase).
- **`PhysicsSystem.RequiresExclusiveExecution == true`** regression pin.

## Documentation to update

`ISystem`'s XML doc and `SystemScheduler`'s class doc (see "Stale doc comments" above) must be
rewritten to reflect that `Stage.Simulation` now executes in parallel waves — leaving them as-is
would ship a false doc comment on day one.

## Regression gate

This sub-milestone touches only `Agapanthe.Engine`/`Agapanthe.World` (headless, no rendering
surface). All 9 pinned Sandbox/TopDown captures and `HeadlessSim`'s snapshot hash are expected to
be completely unaffected — verified anyway via the standard `git stash` A/B technique against the
rollback point (current `HEAD`, `abdd1d5`). Full `dotnet test` green, `dotnet build` 0 warnings,
AOT publish + JIT == NativeAOT re-confirmed on Sandbox, TopDown, and HeadlessSim.

## Double audit

`csharp-lowlevel` + `engine-architect` — the project-standard pairing (no new Vulkan surface, so
no `graphics-3d` deviation). This is the single riskiest kind of change this project has ever
attempted — genuine multi-threading, first time in the codebase's history — so the audit is
expected to matter more than usual, particularly around thread-safety of the wave dispatch,
memory-visibility guarantees at wave/join boundaries, and correctness of the conflict-detection
algorithm.

## Out of scope

- Fine-grained resource modeling of `GameWorld`'s physics/query broadphase scratch and
  `SimCommandQueue` (sub-milestone 2). Today, any system touching this state just sets
  `RequiresExclusiveExecution = true`.
- A runtime-verified safety net proving a system's actual component access matches its declared
  `Reads`/`Writes` (sub-milestone 3). For now, a system that lies about its declared access is a
  silent, undetected hazard — the same posture `AssertOwnerThread` itself had for years before
  this sub-milestone.
- `Stage.Input`, `Stage.PostSimulation`, `Stage.Render`/`IRenderSystem`/`RenderSystemScheduler` —
  all remain fully sequential. Render in particular is GPU/Vulkan-queue-bound and a materially
  different problem.
- Any existing `Sandbox`/`Agapanthe.Platform.App` system (`LandingChallengeSystem`,
  `ProbeDropSystem`, `DriveControlSystemFactory`'s no-op system) — all keep the safe default
  (`RequiresExclusiveExecution == true`), unmodified.
- Fine-tuning worker-pool size, thread affinity, or NUMA-awareness — a fixed
  `ProcessorCount - 1` is the simplest correct starting point.

## Verification (end-to-end)

`dotnet build` 0 warnings; `dotnet test` green (new scheduler/threading tests); all 9 pinned
captures + `HeadlessSim` snapshot hash re-verified byte-identical via `git stash` A/B against
`abdd1d5`; AOT publish + JIT == NativeAOT on Sandbox/TopDown/HeadlessSim; double audit
`csharp-lowlevel` + `engine-architect`, findings applied and re-verified.

## Decision log

| # | Decision | Rationale |
|---|---|---|
| D1 | Decompose into gated sub-milestones; this one = Stage.Simulation only | Scope comparable to all of MP-0; MP-0/Contenu-3c precedent for human-gated decomposition |
| D2 | Sanctioned-worker-thread allowlist on `AssertOwnerThread`/`SimCommandQueue` | Cheapest correct fix to stop the existing Debug guard from immediately breaking; full verification deferred |
| D3 | `ISystem.Reads`/`Writes` as interface properties, not attributes | No reflection, no AOT/trimming risk |
| D4 | Coarse `RequiresExclusiveExecution` flag, default `true` | GameWorld's scratch state is invisible to a component-based conflict model; safe-by-default punts precision to sub-milestone 2 |
| D5 | Persistent worker pool + low-level signaling, not Task/Parallel.Invoke | Task allocates per call, breaking the locked 0-alloc-per-frame gate |
| D6 | `RequiresExclusiveExecution` defaults to `true` | Unaudited existing systems must never silently become parallel-eligible |
| D7 (added post-execution) | Worker pool created **lazily** — only the first time a wave needs 2+ concurrent systems — instead of unconditionally at the `_frozen` transition | Spinning up `ProcessorCount-1` threads for every `SystemScheduler` instance, including the hundreds a test run constructs, would leak live threads for zero benefit; production behavior is bit-for-bit unaffected since `Stage.Simulation` has at most one real system (`PhysicsSystem`, exclusive) anywhere in the codebase today |

## Outcome (post-execution, post-audit)

Double audit `csharp-lowlevel` (3.4/5) + `engine-architect` (3.5/5), both PASS-with-concerns — the single
riskiest change in the project's history (first real multi-threading). Both independently converged on the
same class of blocking defect from complementary angles: the sanctioned-thread allowlist (D2) was applied to
**all 25** `GameWorld.AssertOwnerThread` call sites, including every structural mutator (`Spawn`/`Despawn`/
`Save`/`Load`/`StepPhysics`/the broadphase-scratch query methods) — none of which are safe for concurrent
access — and the shipped test suite **pinned the dangerous case as the intended guarantee** (`Spawn` from a
worker not throwing), while a separate test made two genuinely concurrent `SimCommandQueue.Enqueue` calls on
the same queue, an actual data race reproduced blind.

**Fixed**: the guard was split into `AssertOwnerThread` (honors the allowlist, read-only surface — only
`IsAlive` uses it) and `AssertOwnerThreadStrict` (owner-only, never honors the allowlist — the other 24 sites,
every mutator and every broadphase-scratch query). Affected tests rewritten to pin the opposite (`Spawn` from a
sanctioned worker still throws). `SystemScheduler`/`SimulationHost` gained `IDisposable` (the worker pool and
its synchronization primitives were never being torn down — a real leak against the project's "IDisposable
partout" gate); every test that forces a multi-system wave now disposes its scheduler/host. `SetSanctionedWorkerThreads`
unions rather than replaces, publishes via `Volatile`, and `GameWorld`'s copy is `internal` (added
`InternalsVisibleTo("Agapanthe.Engine")`) rather than a public surface. `WorkerLoop`'s entire iteration is
wrapped in an outer catch (previously an exception outside a system's own `Execute` — e.g. from a disposed
semaphore — would kill a worker silently and hang the owner forever, indistinguishable from a hang); Debug
builds also time out a wave's join at 30s with a named diagnostic. Worker-pool size is capped by the widest
wave a scheduler will ever actually run, not a flat `ProcessorCount - 1`. `SystemScheduler` gained its own
Debug-only owner-thread assert, mirroring `GameWorld`/`SimCommandQueue`. Added exception-propagation tests
(single throw, two throws → `AggregateException`, a tick after a throw still ticks correctly) and a 17-system
wide-wave partition-coverage test — the exception path and the uneven-partition arithmetic had zero coverage
before; both verified by mutation.

**New finding during fix verification**: none of the tests disposed their schedulers, so every multi-system-
wave test leaked a live worker-thread pool for the rest of the test process. Proven empirically by A/B
stress-testing (23/23 clean on the pre-Job-1 baseline vs. occasional cross-test interference with Job-1's
pool-creating tests present — an unrelated pre-existing test, `CopySyncStateTests.PlanFrame_IsZeroAlloc_InSteadyState`,
flaked at roughly 1-in-10 to 1-in-15 runs due to background OS thread churn). Fixed by disposing every scheduler/
host in tests — closed the majority of the interference; a much lower residual flake rate may remain, inherent
to any suite that creates and tears down real OS threads near time-sensitive 0-alloc tests. Production is
unaffected either way.

**Accepted, not fixed** (documented, versed to `docs/BACKLOG.md` §4quater): the owner thread idles during a
wave instead of running one partition itself (a real perf/utilization nicety on low core counts, correctness-
neutral); worker-thread allocations are invisible to `SimulationHost.LastFrameAllocatedBytes` (a per-thread
counter) — harmless today since no real system ever triggers pool creation in production, explicitly flagged
as debt for whichever future sub-milestone adds a second real `Stage.Simulation` system.

**Verification**: 966 tests (+29), 0 warnings, 0 regressions. All 9 pinned Sandbox/TopDown captures re-verified
byte-identical via `git stash` A/B against the `abdd1d5` rollback point (before AND after the audit-fix pass).
JIT == NativeAOT confirmed on Sandbox, TopDown, and HeadlessSim (default `dbe9ed91…` and `--drive` `1c760d7f…`
snapshots unchanged) before and after the audit-fix pass. 0 leaks, 0 validation messages.
