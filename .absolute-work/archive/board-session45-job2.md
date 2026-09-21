# Board — Job-2: SimCommandQueue concurrency guard

Spec: `docs/plans/2026-09-21-job-system-submilestone2-design.md` (approved 4.70/5, 2 review rounds
— round 1 found a blocking design flaw in the original physics-scratch half, fixed by descoping it
honestly rather than shipping something that would throw/race in production).

## Rollback Point

`a408c47d4f36319ae184a799ef6b1219dbe2213a` (Net-1, committed + pushed, tree clean before this
board's Wave 1 touches anything).

## Project Conventions (detected)

- .NET 10 solution (`Agapanthe.slnx`), test project `tests/Agapanthe.Tests` (xUnit), run via
  `dotnet test`. Build via `dotnet build` (0 warnings required, `TreatWarningsAsErrors`).
- Existing test file to extend: `tests/Agapanthe.Tests/SimCommandQueueTests.cs` — already has a
  `RunOnAnotherThread` helper with an optional `sanctionThisThread` callback (verified during spec
  review, lines ~173-191) — the new tests extend this pattern directly, no new test infrastructure
  needed.
- Commits/pushes only on explicit user request (per CLAUDE.md § Préférences de travail).
- Double audit (`csharp-lowlevel` + `engine-architect`) before closing, same pairing as Job-1.

## Scope (from spec)

**In scope**: `SimCommandQueue` (`src/Agapanthe.Engine/SimCommandQueue.cs`) gains an active,
always-on (Debug + Release) concurrency guard around `Enqueue`/`DrainUpTo` — detects genuine
cross-thread concurrent entry and throws immediately, while still permitting the existing
legitimate same-thread reentrancy (a `DrainUpTo` handler calling `Enqueue`).

**Explicitly out of scope** (see spec's "Descoped"/"Out of scope" sections): `PhysicsSystem`/
`GameWorld.StepPhysics` fine-grained resource modeling (genuinely architecture-blocked, not a
shortcut — `AssertOwnerThreadStrict` has no worker-thread relaxation story at all); query
broadphase scratch (same constraint, also confirmed unreachable from `Stage.Simulation`); a second
real `Stage.Simulation` system; Job-1's sub-milestone 3; `SimCommandQueue` flood control. **No
file outside `src/Agapanthe.Engine/SimCommandQueue.cs` and its test file should change** —
`PhysicsSystem.cs`/`GameWorld.Physics.cs`/`GameWorld.Queries.cs` stay byte-identical.

## Task Graph

```
JOB2-001 (test)  ──▶  JOB2-002 (code)  ──▶  JOB2-003 (Self Code Review)  ──▶  JOB2-004 (Req. Validation)  ──▶  JOB2-005 (Full Verification)
```

Fully sequential — one shared file (`SimCommandQueue.cs`) across every task, no parallelism
opportunity exists here; safety-first serialization per the skill's own default.

### Wave 1 — JOB2-001: Write failing tests for the concurrency guard

- **Type**: test · **Size**: S · **Deps**: none
- File: `tests/Agapanthe.Tests/SimCommandQueueTests.cs` (extend, using the existing
  `RunOnAnotherThread` helper pattern).
- New tests (per spec's Testing strategy):
  1. `ConcurrentEnqueue_FromTwoRealThreads_OneThrows` — two threads, synchronized via a manual
     barrier (`ManualResetEventSlim`/`Barrier`, never `Thread.Sleep`) to force genuine overlapping
     entry into `Enqueue`; assert exactly one throws `InvalidOperationException`, the other
     completes and the queue ends up with exactly one more command than before.
  2. `HandlerEnqueuingDuringDrain_StillDoesNotThrow` — regression: a `DrainUpTo` handler calling
     `Enqueue` (same thread) must NOT throw under the new guard (this exact behavior is already
     tested pre-guard; extend/duplicate to prove the new guard doesn't break it).
  3. `PostFailureIntegrity_ALosingThreadsThrowDoesNotCorruptTheWinner` — T1 calls `DrainUpTo` with
     a handler that blocks on a `ManualResetEventSlim` (holds the critical section); signal T2 to
     attempt `Enqueue`, assert it throws; then, still inside T1's blocked handler (same thread,
     same call stack), have the handler itself call `Enqueue` — assert this succeeds (proves T2's
     failed, thrown attempt left T1's ownership/depth state untouched).
  4. `SanctionedWorkerAlone_StillSucceeds` — regression pin: a single sanctioned worker thread
     calling `Enqueue` with no concurrent caller still succeeds unchanged (Job-1 precedent).
  5. `EnqueueAndDrainUpTo_AreZeroAllocAfterWarmup_OnOwnerAndWorkerThread` — 0-alloc gate, measured
     on **both** the owner thread and a sanctioned-worker-thread call path (mirrors
     `SystemScheduler`'s own `_workerAllocatedBytesForTest` precedent from Job-1 — the guard's
     actual intended future use is a worker thread calling `Enqueue`, not just the owner thread).
- **Acceptance**: all 5 tests written, compiling against the *planned* `EnterCriticalSection`/
  `ExitCriticalSection` API (not yet implemented — tests 1 and 3 are expected to fail/not compile
  meaningfully until Wave 2; tests 2, 4, 5 should already pass against today's code and serve as
  regression pins going in).

### Wave 2 — JOB2-002: Implement the concurrency guard

- **Type**: code · **Size**: S · **Deps**: JOB2-001
- File: `src/Agapanthe.Engine/SimCommandQueue.cs` only.
- Add `private const int FreeSentinel = -1;`, `_criticalSectionThreadId`, `_criticalSectionDepth`
  fields; `EnterCriticalSection([CallerMemberName] string caller = "")` and
  `ExitCriticalSection()` per the spec's Architecture section (exact CAS logic, exact exception
  message).
- Wrap `Enqueue`'s and `DrainUpTo`'s existing bodies: `EnterCriticalSection()` call **before** the
  `try` block (the ordering hazard the spec calls out explicitly — a losing thread's thrown
  conflict must never pair with a `finally`'s `ExitCriticalSection()`), existing body inside
  `try`, `ExitCriticalSection()` in `finally`. Called after the existing `AssertOwnerThread()`
  call, unchanged.
- Do NOT touch `_draining`, `AssertOwnerThread`, `SetSanctionedWorkerThreads`, or any other
  existing member — this task is additive only.
- **Acceptance**: all 5 Wave 1 tests pass; `dotnet build` 0 warnings; no other file in the repo
  shows a diff (`git status --short` shows only `SimCommandQueue.cs` + the test file).

### Wave 3 — JOB2-003: Self Code Review (tail, mandatory)

- **Type**: code · **Size**: S · **Deps**: JOB2-002
- Full diff self-review against the spec: confirm `EnterCriticalSection()` is called strictly
  before every `try` it guards (the exact hazard a future maintainer could reintroduce); confirm
  `PhysicsSystem.cs`/`GameWorld.Physics.cs`/`GameWorld.Queries.cs` are byte-identical to
  `a408c47` (`git diff a408c47 -- src/Agapanthe.Engine/PhysicsSystem.cs src/Agapanthe.World/GameWorld.Physics.cs src/Agapanthe.World/GameWorld.Queries.cs`
  must be empty); confirm no `PhysicsStepScratch` type or reference exists anywhere in the diff
  (grep); confirm the guard is NOT `[Conditional("DEBUG")]` (must be always-active per D3);
  confirm `_criticalSectionDepth` is never touched outside the thread that owns the section.

### Wave 4 — JOB2-004: Requirements Validation (tail, mandatory)

- **Type**: code · **Size**: S · **Deps**: JOB2-003
- Walk the spec's Testing strategy and Out-of-scope sections line by line; confirm every listed
  test exists and passes; confirm every out-of-scope item (physics scratch modeling, query
  scratch, second real `Stage.Simulation` system, sub-milestone 3, flood control) has NOT crept
  in.

### Wave 5 — JOB2-005: Full Project Verification (tail, mandatory)

- **Type**: code · **Size**: M · **Deps**: JOB2-004
- `dotnet build` 0 warnings; `dotnet test` full suite green; regression gate — all 9 pinned
  Sandbox/TopDown captures + `HeadlessSim`'s snapshot hash re-verified byte-identical (expected
  trivially unaffected: this sub-milestone touches zero rendering/simulation code, only
  `Agapanthe.Engine/SimCommandQueue.cs`); AOT publish + JIT == NativeAOT re-confirmed on
  `HeadlessSim`/`DedicatedServer` (both link `Agapanthe.Engine`).
- Dispatch the double audit (`csharp-lowlevel` + `engine-architect`), apply findings, re-verify
  anything the findings touch.

## Wave 5 results — CLOSED

`dotnet build` 0 warnings; `dotnet test` 997/997 green; regression gate trivially satisfied (diff
confined to `SimCommandQueue.cs` + one `Systems.cs` doc-comment + the test file — no `GameWorld`
touch at all, confirmed byte-identical against `a408c47` for `PhysicsSystem.cs`/
`GameWorld.Physics.cs`/`GameWorld.Queries.cs`); AOT publish + JIT == NativeAOT re-confirmed on
`HeadlessSim`.

Double audit `csharp-lowlevel` (3.7/5) + `engine-architect` (4.0/5), both PASS-with-concerns, no
🔴 from architecture. **1 🔴 from csharp-lowlevel, found by mutation, fixed**: the flagship
regression test (`PostFailureIntegrity_...`) didn't actually discriminate the ordering-hazard bug
it claimed to guard — verified by literally reproducing the mutation and watching all 13 tests
stay green. Fixed with a genuine discriminating third-thread assertion + two `Debug.Assert`s in
`ExitCriticalSection`, both independently re-verified against the same mutation (now red, as
required). Full findings list and the "genuinely blocked" → "the plan is blocked, not the goal"
reframing are in the spec's Outcome section.

See `docs/plans/2026-09-21-job-system-submilestone2-design.md`'s Outcome section for the complete
findings list and fixes.

## Deferred Work (out of scope, not dropped)

Per spec: `PhysicsSystem`/`GameWorld.StepPhysics` fine-grained resource modeling (architecture-
blocked — needs a separately-designed, provably-safe relaxation of `AssertOwnerThreadStrict` for a
system whose entire declared access is a disjoint private resource); query broadphase scratch
modeling (same constraint); a second real `Stage.Simulation` system; Job-1's sub-milestone 3
(runtime declared-vs-actual access verification); `SimCommandQueue` flood/backpressure control.
