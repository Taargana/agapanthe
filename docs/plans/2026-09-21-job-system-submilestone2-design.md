# Job system — sub-milestone 2: `SimCommandQueue` concurrency guard (Job-2)

## Summary

Job-1 (session 43) shipped `ISystem.Reads`/`Writes`/`RequiresExclusiveExecution` and real wave-
parallel execution for `Stage.Simulation`, but explicitly punted on two pieces of shared mutable
state invisible to the component-based conflict model: `GameWorld`'s physics broadphase scratch
and `SimCommandQueue`. This sub-milestone was originally scoped to close both — it now closes only
one, for a real, code-verified reason discovered during spec review (not glossed over — see
"Descoped: `PhysicsStepScratch`" below).

**`SimCommandQueue`** is fully in scope and ships this session: its own Job-1 doc-comment already
admits "[being sanctioned] does not arbitrate concurrent callers" — a genuine, documented gap this
sub-milestone closes with a runtime concurrency guard, independent of the scheduler.

**The physics broadphase scratch** (`PhysicsSystem`/`GameWorld.StepPhysics`) is explicitly
descoped: a spec-review pass found that `GameWorld.StepPhysics` calls `AssertOwnerThreadStrict()`
(`GameWorld.Physics.cs`), a guard whose own doc-comment (`GameWorld.cs:197-202`, added in Job-1)
states in plain text that it "stays owner-thread-only no matter what is sanctioned" for exactly
this kind of state — **there is no worker-thread story for it at all**, by explicit Job-1 design.
Declaring `PhysicsSystem.Writes: [typeof(PhysicsStepScratch)]` and flipping
`RequiresExclusiveExecution` to `false` (the sub-milestone's original plan) would not close this
gap — it would relabel the escape hatch while leaving the actual constraint in place (the
illustrative name `PhysicsStepScratch` below never became a real type — this is a description of
the abandoned plan, not of anything that exists in the codebase today): the day a
disjoint `Stage.Simulation` system shares a wave with `PhysicsSystem`,
`SystemScheduler.RunWaveInParallel` dispatches **every** member of a 2+-system wave onto a worker
thread (verified: `SystemScheduler.cs:219-230`, no wave member of size >1 ever runs inline on the
owner thread), and `StepPhysics`'s `AssertOwnerThreadStrict()` throws in Debug — or silently races
unguarded scratch arrays in Release, where the assert is compiled away.

**Precision added post-audit (engine-architect)**: the plan that is blocked is specifically
*relaxing `AssertOwnerThreadStrict`* — the underlying GOAL (letting `PhysicsSystem` run concurrently
with a disjoint system) is not inherently blocked, and framing it as "genuinely blocked" overstated
the case. `RequiresExclusiveExecution` conflates two orthogonal axes a mature job system keeps
separate (resource exclusivity vs. thread affinity — Bevy's `NonSend`/`exclusive_system`, Unity's
main-thread affinity vs. job dependency are both prior art for this split). `PhysicsSystem` needs
thread AFFINITY (always run on the owner thread, where `StepPhysics`'s strict guard already lives),
not resource EXCLUSIVITY (never share a wave with anyone) — a system that must run on the owner
thread can still legitimately overlap with OTHER systems running on workers, exactly as two workers
already overlap each other today. A concrete, unexplored design: add `bool RequiresOwnerThread`
alongside the existing flag; `RunSimulationWaves` already runs a solo wave inline
(`SystemScheduler.cs:219-227`) — extending that to run the one owner-pinned member of a mixed wave
inline while dispatching the rest to workers is a small, additive change; `AssertOwnerThreadStrict`
stays completely untouched, becoming the exact invariant the design relies on rather than an
obstacle to it. This is real, separate design work — properly out of scope for this sub-milestone —
but it is the recommended **first candidate** for a future physics-focused sub-milestone, not a
dead end (see Out of scope).

## Decisions

- **D1**: `SimCommandQueue` gets an **active runtime concurrency guard**, independent of the
  scheduler's `Reads`/`Writes` vocabulary (there is no `Stage.Simulation` call site for the
  scheduler to conflict-check against — verified: `DrainUpTo` runs in `SimulationHost.Tick` before
  any stage dispatch; every current `Enqueue` caller — `InputTranslation.Emit` (MP-0d's normal
  input path) and `DedicatedServer`'s network-receive callback (Net-1) — runs on the owner thread,
  never from inside a `Stage.Simulation` `ISystem.Execute`).
- **D2**: The guard **detects and throws immediately** on genuine cross-thread concurrent entry
  into `Enqueue`/`DrainUpTo` — never a blocking `lock`. A blocking lock would silently serialize a
  real bug instead of surfacing it, the opposite of this project's established posture
  (`AssertOwnerThreadStrict`, `DrainUpTo`'s own existing same-thread-reentrancy throw).
  **Known interaction, accepted (both audits flagged this)**: on `DedicatedServer`'s current
  `Enqueue` call site (`Program.cs`'s `channel.Received` callback), `NetChannel`'s own catch-all
  (added in Net-1 specifically to stop a malformed WIRE packet from killing the process) would also
  catch this guard's `InvalidOperationException` and count it as `MalformedPacketCount` — a real
  concurrency bug there would surface as a wire-decode metric, not a crash. Verified unreachable
  today (`Received` fires synchronously inside `PollEvents()` on the single owner thread — no
  production path currently overlaps). Not fixed here: narrowing `NetChannel`'s catch to exclude
  `InvalidOperationException` would reopen the exact crash Net-1's own catch-all exists to prevent
  (`PacketCodec`'s malformed-input reads also throw `InvalidOperationException`, verified during
  Net-1's own audit) — a real fix needs a way to distinguish the two `InvalidOperationException`
  sources, which is its own small piece of design, deferred rather than rushed here.
- **D3**: The guard is **always active, Debug and Release** — not `[Conditional("DEBUG")]` like
  `AssertOwnerThread`. `AssertOwnerThread` is a developer-discipline reminder (redundant with
  correct calling convention in Release); a genuine concurrent `Enqueue`/`DrainUpTo` call would
  corrupt `_items`/`_count`/`_drainScratch` for real, in production, via a torn resize/shift — a
  correctness safety net, not a debug-time convenience. The guard itself (one
  `Interlocked.CompareExchange`) is cheap enough that Release cost is not a real concern.
- **D4 (descoped, not a decision to act on)**: Fine-grained modeling of `PhysicsSystem`'s scratch
  is deferred — see Summary and Out of scope. `PhysicsSystem.Writes`/`RequiresExclusiveExecution`
  are **unchanged** by this sub-milestone.
- **D5**: The query broadphase scratch (`GameWorld.Queries.cs`'s `_qGid`/`_qCenter`/`_qRadius`/…)
  stays explicitly OUT of scope, same reasoning as `PhysicsSystem` (also `AssertOwnerThreadStrict`-
  gated) — additionally confirmed never called from a `Stage.Simulation` `ISystem` at all (its only
  callers are `AppHost`'s raw `KeyPressed` handler, `AppHost.cs:250-297`), so there would be
  nothing live to model even if the strict-thread question were solved.

## Architecture

### `SimCommandQueue` concurrency guard (new)

```csharp
private const int FreeSentinel = -1;
private int _criticalSectionThreadId = FreeSentinel;
private int _criticalSectionDepth;

private void EnterCriticalSection([CallerMemberName] string caller = "")
{
    var current = Environment.CurrentManagedThreadId;
    var owner = Interlocked.CompareExchange(ref _criticalSectionThreadId, current, FreeSentinel);
    if (owner == FreeSentinel || owner == current)
    {
        _criticalSectionDepth++;
        return;
    }

    throw new InvalidOperationException(
        $"SimCommandQueue.{caller} was entered by thread {current} while thread {owner} was " +
        "already inside Enqueue/DrainUpTo on this instance — they are not thread-safe against " +
        "each other, only against a single owner (plus a scheduler's sanctioned workers taking " +
        "turns, never running concurrently on the SAME queue instance).");
}

private void ExitCriticalSection()
{
    if (--_criticalSectionDepth == 0)
    {
        Volatile.Write(ref _criticalSectionThreadId, FreeSentinel);
    }
}
```

`Enqueue` and `DrainUpTo` each wrap their existing body as:
```csharp
EnterCriticalSection();
try
{
    /* existing body, unchanged */
}
finally
{
    ExitCriticalSection();
}
```
**Ordering hazard, called out explicitly (spec-review finding)**: `EnterCriticalSection()` MUST be
called *before* the `try` block, never inside it. If a losing thread's `EnterCriticalSection` call
threw (a genuine conflict) from *inside* a `try` whose `finally` calls `ExitCriticalSection()`, that
`finally` would still run and corrupt the *winning* thread's still-in-progress ownership (clearing
or decrementing state it does not own). Placed before the `try` (as above), a thrown conflict never
pairs with an `Exit` at all — exactly the desired behavior. Ordered **after** the existing
`AssertOwnerThread()` call (unchanged) — `AssertOwnerThread` still rejects an unsanctioned thread
outright; this guard catches the narrower, more dangerous case of two *sanctioned* callers racing
each other, which `AssertOwnerThread` alone cannot see.

Reentrancy correctness: `DrainUpTo`'s handler loop legitimately calls `Enqueue` (documented
behavior — a handler enqueuing a derived command) — same thread, so `owner == current` on the
nested `EnterCriticalSection` call, depth increments, no throw. `_criticalSectionDepth` is only
ever touched by the thread that currently "owns" the section (established by the
`CompareExchange`), so no additional synchronization is needed on the depth field itself. The
existing `_draining` flag (a *different*, same-thread re-entrant-drain guard) is unchanged.

## Testing strategy

- **Cross-thread conflict detection**: two real threads, synchronized via a manual barrier
  (`Barrier`/`ManualResetEventSlim` — not timing-dependent sleep) to force genuine overlapping
  entry into `Enqueue`/`DrainUpTo`; assert exactly one throws `InvalidOperationException` and the
  other completes.
- **Same-thread reentrancy regression**: a `DrainUpTo` handler calling `Enqueue` does **not**
  throw (the legitimate case the guard must not break).
- **Post-failure integrity (spec-review finding — the test the original plan was missing)**: T1
  enters and holds the critical section (via a controllable fake handler that blocks); T2's
  concurrent `Enter` attempt throws, as expected; then, with T1 still holding, a third probe from
  T1's own thread confirms T1's ownership is still intact (its own nested call succeeds, depth
  correctly tracked) — proving T2's failed, thrown attempt left T1's state untouched, not merely
  that T2 itself threw.
- **A sanctioned worker thread calling `Enqueue` alone (no concurrent caller) still succeeds**
  (Job-1 precedent unchanged, regression guard).
- **0-alloc-after-warmup** on the guarded `Enqueue`/`DrainUpTo` paths (mirrors every other 0-alloc
  gate in this codebase — the guard must not introduce per-call allocation), measured on **both**
  the owner thread and a sanctioned-worker-thread call path (mirroring `SystemScheduler`'s own
  `_workerAllocatedBytesForTest` precedent from Job-1) — the guard's actual intended future use is
  a worker thread calling `Enqueue`, not just the owner thread.

**Regression gate**: this sub-milestone touches only `Agapanthe.Engine` (`SimCommandQueue.cs`) —
no rendering surface, no `GameWorld` change at all. All 9 pinned Sandbox/TopDown captures and
`HeadlessSim`'s snapshot hash are expected unaffected; verify anyway. Full `dotnet test` green,
`dotnet build` 0 warnings, AOT publish + JIT == NativeAOT re-confirmed.

**Double audit**: `csharp-lowlevel` + `engine-architect` — same pairing as Job-1 (real concurrency
primitive, no new Vulkan surface).

## Out of scope

- **`PhysicsSystem`/`GameWorld.StepPhysics` fine-grained resource modeling** (the original
  headline half of this sub-milestone) — the specific *plan* (relax `AssertOwnerThreadStrict`) is
  blocked; the *goal* is not (see the "Precision added post-audit" paragraph in the Summary — a
  thread-affinity vs. resource-exclusivity split is a concrete, unexplored path). Building either
  version safely is real, separate design work — the exact kind of premature-relaxation risk Job-1's
  own audit fought to prevent (a whitelist that was too broad, a test that pinned the dangerous case
  as correct). A future sub-milestone, once independently warranted and designed on its own terms —
  recommended candidate: the `RequiresOwnerThread` split above, not a relaxation of
  `AssertOwnerThreadStrict`.
- Query broadphase scratch (`GameWorld.Queries.cs`) — same `AssertOwnerThreadStrict` constraint,
  additionally confirmed unreachable from `Stage.Simulation` today.
- Adding a second real `Stage.Simulation` system to exercise the job system live — deferred to
  whenever one is independently warranted.
- Job-1's sub-milestone 3 (a runtime safety net proving a system's actual component access matches
  its declared `Reads`/`Writes`) — unrelated scope, separate sub-milestone.
- Flood/backpressure control on `SimCommandQueue` (already-documented pre-existing deferral,
  unrelated to concurrency).

## Verification (end-to-end)

`dotnet build` 0 warnings; `dotnet test` green (new `SimCommandQueue` concurrency tests); all 9
pinned captures + `HeadlessSim` snapshot hash re-verified unchanged (expected, no `GameWorld`
change at all); AOT publish + JIT == NativeAOT; double audit `csharp-lowlevel` +
`engine-architect`, apply findings.

Once approved: task board, then execute.

## Outcome (closed)

Delivered as revised: `SimCommandQueue` gained an always-on concurrency guard
(`EnterCriticalSection`/`ExitCriticalSection`, `Interlocked.CompareExchange`-based thread-id +
reentrancy-depth tracking), and `AssertOwnerThread` was promoted from `[Conditional("DEBUG")]` to
always-on (a fix applied post-audit, see below). 997 tests, 0 warning, 0 regression (diff confined
to `SimCommandQueue.cs`, one doc-comment in `Systems.cs`, and the test file — `git diff` against
the `a408c47` rollback point confirms `PhysicsSystem.cs`/`GameWorld.Physics.cs`/
`GameWorld.Queries.cs` are byte-identical). AOT publish + JIT == NativeAOT re-confirmed on
`HeadlessSim`.

Double audit `csharp-lowlevel` (3.7/5) + `engine-architect` (4.0/5), both PASS-with-concerns, no
🔴 from architecture, **1 🔴 from csharp-lowlevel, found by mutation and fixed**:
`PostFailureIntegrity_ALosingThreadsThrowDoesNotCorruptTheWinner`'s original assertion (a losing
thread's failed attempt doesn't corrupt the winner's same-thread reentrant probe) did not actually
discriminate — moving `EnterCriticalSection()` inside the `try` (the exact ordering bug the test
exists to catch) left all 13 tests green, because the mutation's real effect (the loser's stray
`Exit` wrongly freeing the winner's section) was invisible to a same-thread probe that just
re-acquires from scratch. Fixed two ways, both verified independently effective by re-running the
same mutation: (1) a genuine discriminating assertion — a THIRD thread's concurrent attempt, which
would wrongly succeed if the section had been freed, now also asserted to throw; (2) two
`Debug.Assert`s in `ExitCriticalSection` (owner-thread check, non-zero-depth check) that make any
future unbalanced Enter/Exit pairing fail loudly in Debug rather than silently wedge the section
forever (also fixed a related latent bug the audit found: a fresh acquisition now always sets depth
to exactly 1 instead of incrementing whatever depth a previous leak might have left behind).

Other findings applied: `AssertOwnerThread` promoted to always-on (engine-architect) — the
guard's own most-likely real hazard, a non-overlapping cross-thread `Enqueue` call between ticks on
`DedicatedServer`'s receive path, was undetected in Release because only the NEW overlapping-guard
was always-on; three foreground-`Thread` test bodies wrapped in try/catch so an unexpected throw
fails the test instead of crashing the test host (csharp-lowlevel); the throw path extracted to a
`[MethodImpl(NoInlining)]` static method to keep it out of the hot, always-on guard body; three
stale doc-comments updated (`SetSanctionedWorkerThreads`, `ISystem.RequiresExclusiveExecution`,
the class summary) that described the gap this sub-milestone exists to close as still open;
`Count`'s cross-thread read semantics documented explicitly (best-effort/diagnostic only off the
owner thread — pre-existing, not a Job-2 regression, but newly relevant given the class's other
members are now guarded). The physics-scratch descope's framing was corrected
(engine-architect): the original spec said the goal was "genuinely blocked" — it is not; the
specific plan (relaxing `AssertOwnerThreadStrict`) is blocked, and a concrete, unexplored
thread-affinity-vs-resource-exclusivity design (`RequiresOwnerThread`, prior art in Bevy/Unity) is
recorded as the recommended first candidate for a future physics-focused sub-milestone rather than
a dead end.

One finding accepted, not fixed (documented in D2): `NetChannel`'s Net-1-era catch-all (built to
stop a malformed wire packet from killing `DedicatedServer`) would also catch this guard's
exception on the one live `Enqueue` call site that goes through it, counting a genuine concurrency
bug as a wire-decode metric instead of surfacing it. Verified unreachable today (`Received` fires
synchronously on the single owner thread). Not fixed here because `PacketCodec`'s own
malformed-input paths also throw `InvalidOperationException` — narrowing the catch would reopen
Net-1's own crash-prevention fix; a real solution needs a way to distinguish the two
`InvalidOperationException` sources, deferred as its own small design question.
