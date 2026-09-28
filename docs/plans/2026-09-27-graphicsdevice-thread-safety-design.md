# GraphicsDevice thread-safety — bounded 2-thread case (owner + 1 loader)

## Summary

A prerequisite for a paused scene-management brainstorm (sequential scene switching — splash →
menu → game): async scene loading needs to construct a new `GameWorld`/`ResourceRegistry` (which
uploads GPU resources) on a background thread while the render thread keeps drawing the current
scene. Scoped narrowly, on purpose, after an explicit interview: not arbitrary N-thread access
(no concrete driver exists in this codebase today — Job-1/2 deliberately exclude GPU access from
worker threads, and P3-M4's GPU-driven indirect rendering already avoids the classic "parallel
command-buffer recording" bottleneck) — exactly **one** loader thread alongside the owner/render
thread.

**Revision note**: two independent review rounds. Round 1 (2.80/5, NEEDS WORK) found the original
draft's central premise wrong — it assumed the loader thread's uploads go through
`GraphicsDevice.SubmitImmediate`. Verified against the real code: they do not. The actual upload
path (`ResourceRegistry.Load` → `SceneBuilder` → `GpuUploader.Upload`) owns its own reused command
pool/buffer/fence and calls `GraphicsDevice.QueueSubmit2` directly, never through `SubmitImmediate`.
Round 1 also found a missed submission call site (`GpuReadback`, raw `vkQueueSubmit`), `WaitIdle()`
called mid-loop from several places (not just at final teardown), an unlocked
`GpuAllocator.GetStats()`, and a `QueuePresent` design that would break swapchain-recreation error
handling. Round 2 (3.45/5, NEEDS WORK) found the round-1 fix for D3 was itself incomplete
(`LogStats`/`Dispose` still unlocked, and the justification for leaving them so was a non-sequitur
— a real use-after-free scenario), a genuinely blocking bug (the sanction API was `internal` but
`Agapanthe.Graphics` has no `InternalsVisibleTo` reaching any future consumer of it), a missing
two-level guard (owner-strict vs owner-or-loader), an underspecified disposal precondition (no
throw semantics stated, no way to actually clear the registration), and a test plan whose cited
precedent doesn't build a real `GraphicsDevice` and whose allocator test has no real invariant
oracle. Round 3 (4.20/5, **PASS**) confirmed every round-2 fix landed and found three remaining
one-line gaps (D8's throw placement relative to the existing idempotency early-return, three
unstated behaviors on the sanction API, and a windowed-test case that didn't actually discriminate
between the two guard levels) plus three non-blocking refinements — all applied, no further review
round required. See "Corrections from review round 1/2/3" at the end.

## Decisions

- **D1 — Contract, corrected visibility**: `GraphicsDevice` supports its owner thread (constructor
  thread) plus **at most one** additional sanctioned loader thread, set via a new **`public`** (not
  `internal` — round-2 finding: `Agapanthe.Graphics` only grants `InternalsVisibleTo` to
  `Agapanthe.Tests` and `ShaderPrecompiler`; neither the future scene-loading consumer in
  `Agapanthe.App`/`Engine.Render`/`Rendering` nor this spec's own new windowed test program could
  ever have called an `internal` version) `void SetSanctionedLoaderThread(int threadId)`, paired
  with a new **`public void ClearSanctionedLoaderThread()`** — round-2 finding: the original draft
  had no way to un-register, but D8's disposal precondition requires exactly that after the loader
  thread is joined. **Round-3 corrections (three one-line gaps, PASS granted subject to fixing
  before implementation, no further review round needed):** both methods are guarded by
  `AssertOwnerThreadStrict` (D2) — only the owner thread may sanction or clear a loader, a loader
  can never sanction itself or another thread; `SetSanctionedLoaderThread` throws
  `InvalidOperationException` if a loader is already registered ("at most one" was stated but not
  enforced); both throw `ObjectDisposedException` if called after `Dispose()`.
- **D2 — Guard placement and two levels, corrected**: the guard must sit where **every** submission
  path actually converges — `GraphicsDevice.QueueSubmit2` and the new `QueueSubmit` wrapper (D4a/
  D4b) — not in `SubmitImmediate`, which the real upload path (`GpuUploader`) never calls.
  `_ownerThreadId` (captured at construction) + `_sanctionedLoaderThreadId` (nullable int,
  **`Volatile`-published** — round-2 finding, mirrors `GameWorld._sanctionedWorkerThreadIds`'s own
  `Volatile.Read`/`Write` pattern since both threads read it — singular here since the need is
  bounded to one loader). Two levels, mirroring `GameWorld.AssertOwnerThread` (owner **or**
  sanctioned) vs `AssertOwnerThreadStrict` (owner **only**) exactly — round-2 finding, the original
  draft used one undifferentiated guard everywhere: **`AssertCallerThread`** (owner or loader) on
  `QueueSubmit2`/`QueueSubmit`, where a loader legitimately submits; **`AssertOwnerThreadStrict`**
  (owner only, new name mirrors `GameWorld`'s) on `QueuePresent` and `AdvanceFrame` — a loader
  thread never presents a frame or advances the frame counter, so a loader calling either is itself
  a bug worth catching, not a legitimate path to allow. **Promoted to always-on, not
  `[Conditional("DEBUG")]`**: Job-2 (`docs/plans/2026-09-21-job-system-submilestone2-design.md`,
  `SimCommandQueue.AssertOwnerThread`, confirmed real —
  `src/Agapanthe.Engine/SimCommandQueue.cs:269-297`) promoted an analogous guard from Debug-only to
  always-on after finding a non-overlapping cross-thread call could go completely undetected in
  Release. The same reasoning applies here — a thread-id comparison is cheap even once per
  submission, and a missed lock in this area is a native-crash-class bug, not a benign logic error.
- **D3 — `GpuAllocator` gets a real lock, on every method touching shared state, including
  `LogStats`/`Dispose`**. Verified: no lock exists today, and the doc comment already says "Not
  thread-safe." Add a private `System.Threading.Lock` (same primitive as `DeletionQueue`); wrap
  `Allocate`, `Free`, `GetStats()` (round-1 finding: `GetStats` enumerates `_allocators`, a plain
  `Dictionary`, unguarded — concurrent with `Allocate`'s `GetOrCreateAllocator` adding an entry,
  that is a collection-modified-during-enumeration hazard), **and `LogStats()`/`Dispose()` as well**
  — round-2 finding: the round-1 draft argued these two were "covered by D8's precondition... they
  run after `WaitIdle` has already serialized against any in-flight submitter," which is a
  non-sequitur — `Allocate`/`Free` never take `_queueLock` (D4a/b), so `WaitIdle` serializes nothing
  against them. The real, concrete hazard: `Allocate` passes its `_disposed` check (outside any
  lock), then `GpuAllocator.Dispose` runs concurrently, clears `_allocators` and frees the backing
  blocks (`vkFreeMemory`), then the original `Allocate` call suballocates into a block that no
  longer exists — a `VkDeviceMemory` use-after-free. Locking `Dispose`'s body (not just relying on
  D8's *caller-side* precondition) closes this regardless of whether the precondition was actually
  honored, at zero extra cost (`Dispose` runs once).
- **D4a — `GraphicsDevice.QueueSubmit2` gets a lock**: private `Lock _queueLock`; wrap the body,
  plus `AssertCallerThread` (D2) at entry.
- **D4b — new locked `QueueSubmit` (legacy, non-sync2)**: round-1 finding — `GpuReadback.cs:111`
  calls `vk.QueueSubmit` (the legacy `SubmitInfo`, not `SubmitInfo2`) directly on `GraphicsQueue`,
  bypassing `QueueSubmit2` entirely. New `internal void QueueSubmit(Queue queue, SubmitInfo* submit,
  Fence fence)` — replaces `vk.QueueSubmit(device.GraphicsQueue, 1, &submit, fence)`
  (`GpuReadback.cs:111`) exactly, keeping the same `VkCheck.ThrowIfFailed(..., "vkQueueSubmit")`
  error label (round-2 clarification) — same `_queueLock`, same `AssertCallerThread`.
- **D4c — `GraphicsDevice.WaitIdle()` takes the same lock**: round-1 finding, the most serious gap
  in the original draft — `vkDeviceWaitIdle` itself requires external synchronization against any
  concurrent `vkQueueSubmit`/`vkQueueSubmit2` on any thread, and `WaitIdle` is **not** a
  teardown-only call: it runs mid-loop from `Swapchain.Recreate`/`Swapchain.Dispose` (a plain window
  resize), `Renderer.cs:1561`, `IblGenerator.cs:284`, `FrameRenderer.cs:366`, and
  `AppHost.cs:389/407` (round-3 addition — the complete list; the first draft's list was a
  representative sample, not exhaustive), all on the owner thread during normal operation — exactly
  the scenario a loader thread would be concurrent with. Original
  D7 only guarded final `Dispose()`, which does not cover any of these. `WaitIdle()`'s body is
  wrapped in `lock (_queueLock)`: any submit attempted concurrently blocks on the same lock until
  `vkDeviceWaitIdle` returns, which is the correct serialization, not just a symptom-masking stall
  (contrast with the Club Architect milestone's earlier `WaitIdle` band-aid, which stalled the
  whole device to paper over a missing barrier — here the lock **is** the actual fix, not a
  workaround, because the Vulkan spec's own requirement is a host-side ordering constraint, which a
  mutex is the correct primitive for). **Contract, made explicit (round-2 finding)**: the instant
  `WaitIdle` releases the lock, the loader thread may resume submitting — "idle" only ever meant
  "idle with respect to work submitted before this call returned," never a lasting guarantee. Every
  current caller (`Swapchain.Recreate`/`Dispose`, `Renderer.cs:1561`, `IblGenerator.cs:284`,
  `FrameRenderer.cs:366`, `AppHost.cs:389/407`) only destroys resources the *owner* thread created (swapchain images,
  HDR/depth targets, a transient IBL command pool) — verified safe — but this is now a standing
  contract for any future caller: never destroy something the loader thread might still reference
  just because `WaitIdle` returned.
- **D5 — new `GraphicsDevice.QueuePresent`, corrected signature**: round-1 finding — the original
  draft's `QueuePresent(SwapchainKHR, PresentInfoKHR*)` both duplicated data already in
  `PresentInfoKHR` (which carries the swapchain) and, modeled on `QueueSubmit2`'s throw-on-failure
  shape, would have broken `Swapchain.Present`'s existing handling of
  `ErrorOutOfDateKhr`/`SuboptimalKhr` (it inspects the raw `Result`, never throws on those two).
  Corrected: `internal Result QueuePresent(Queue queue, PresentInfoKHR* info)` on `GraphicsDevice`
  (not `Swapchain` — the lock and the guard both live on `GraphicsDevice`) — takes the queue
  explicitly (mirrors `QueueSubmit2`), **returns** `Result` instead of throwing, takes the same
  `_queueLock`. `Swapchain.cs:112` calls this and keeps its own `Result`-based branching unchanged.
  Guarded by **`AssertOwnerThreadStrict`** (D2), not `AssertCallerThread` — a loader thread never
  presents. **One shared `_queueLock` for both submit and present is deliberate, not an
  approximation** (round-2 finding, worth stating so a future "optimization" doesn't split it):
  `PresentQueue` and `GraphicsQueue` are the same `VkQueue` handle in this engine's own device
  selection (`TryFindQueueFamilies` picks one family supporting both, specifically to avoid
  concurrent sharing on the swapchain) — a per-queue lock would silently stop protecting anything
  the moment that aliasing is (correctly) relied upon elsewhere.
- **D6 — `CurrentFrameIndex` becomes atomic**: `Interlocked`/`Volatile` around the backing field.
  `AdvanceFrame` additionally gets **`AssertOwnerThreadStrict`** (D2, round-2 addition) — a loader
  thread never advances the frame counter, so a loader calling it is a bug, not a race to merely
  tolerate.
- **D7 — Lock ordering invariant, documented, `_queueLock` declared a leaf lock (round-1 finding,
  sharpened in round 2)**: `DeletionQueue.Flush` executes queued destructors **while holding its own
  lock**, and a buffer destructor (`GpuBuffer.DestroyDeferred`) calls `Allocator.Free`, which takes
  `GpuAllocator`'s lock (D3). Lock acquisition order is therefore always DeletionQueue → Allocator,
  never the reverse — no cycle exists today because `Allocate`/`Free` never enqueue anything into
  `DeletionQueue`. **`_queueLock` (D4a/b/D5) is declared a leaf lock, independent of the other two**:
  nothing while holding `_queueLock` ever acquires `DeletionQueue`'s lock or `GpuAllocator`'s lock,
  and nothing while holding either of those acquires `_queueLock` — round-2 finding: this held true
  in the reviewed code but was only true "by accident," not stated as an invariant a future change
  must preserve. Documented on all three types.
- **D8 — Disposal precondition, corrected (round-2 finding: the round-1 draft only said "keeps its
  always-on check," but no such check exists anywhere in `GraphicsDevice.Dispose()` today — this is
  new, not preserved)**: `GraphicsDevice.Dispose()` gains a check that `_sanctionedLoaderThreadId`
  (D1) is `null` — **`throw new InvalidOperationException`** if not, naming the still-registered
  thread id. **Round-3 correction (placement, one line)**: the real `Dispose()` already starts with
  `if (_disposed) { return; }` for idempotency, *then* `_disposed = true;` — the new check goes
  **immediately after that existing early-return, still before `_disposed = true`**, not literally
  before the early-return itself. Placed before the early-return, a second `Dispose()` call after a
  *successful* first one (which already cleared the loader) would be harmless, but a second call
  after a call that legitimately no-opped for other reasons could re-throw for a condition already
  resolved — placing it right after preserves both the existing idempotency contract and "a failed
  Dispose can be corrected and retried" (the loader thread is still logically un-registered on a
  path that never got far enough to check it, so nothing is lost by checking here instead). The
  caller's contract: call the new `ClearSanctionedLoaderThread()` (D1) after `Join()`-ing the loader
  thread, before disposing the device. A thrown exception here is loud by design: `AppHost`'s
  teardown isolates each step in its own try/catch (`AppHost.cs`), so a violation is logged and the
  device leaks rather than crashing the process outright — noisy (the leak gate fails), which is the
  intended outcome for a caller that broke the contract, not a silent pass. D4c's lock on `WaitIdle`
  is the actual data-race fix for the common case; this check catches the *logical* mistake (a
  caller who forgot to join/clear its loader thread) loudly rather than relying on timing luck.
  **Desirable, non-blocking (round 3)**: `GpuAllocator.Allocate`/`Free`'s existing
  `ObjectDisposedException.ThrowIf(_disposed)` checks should move *inside* the lock (D3) — the
  exact use-after-free race D3 closes could otherwise still be entered between the check and the
  lock acquisition.
- **D9 — What a loader thread may safely do, stated precisely (round-1 finding: the original
  "Deferred" section was quietly dishonest here)**: safe — building an entirely **new**
  `ResourceRegistry` (a fresh instance the render thread never touches until an explicit hand-off)
  and calling `Load` on it. Verified in round 2, and the isolation is even stronger than round 1
  stated: `ResourceRegistry.Load` → `SceneBuilder.Build` already constructs its own
  per-call `GpuUploader`, `DescriptorAllocator` (an isolated pool) and `SamplerCache`
  (`SceneBuilder.cs:56`) — a caller never supplies or shares any of these, so the isolation is
  structural, not a discipline the loader thread has to maintain by hand. The one object touched
  that is NOT per-call is `Renderer.MaterialSetLayout` (a read-only handle,
  `vkAllocateDescriptorSets` needs no external synchronization on the layout itself, only on the
  pool it allocates from — which is per-call) — **the loader thread's `Renderer` (or at least its
  `MaterialSetLayout`) must outlive the load**, a lifetime dependency worth stating plainly. **Not
  safe, not covered by this spec**: a loader thread calling `Load` on the **active**
  `ResourceRegistry` the render thread is already reading every frame (`Renderer.cs` reads it for
  every draw) — its `SlotTable`s, `_models` list and `_keyIndex` are mutated by `Load` with no lock
  of their own, and `Renderer` never expected concurrent readers. This is not deferred as a vague
  "audit item" — it is a real, load-bearing constraint on how any future scene-loading design may
  use this work: it must build in isolation and hand off, never mutate the live registry from a
  second thread. **Two more constraints for that future design to know about, surfaced here rather
  than left implicit (round-2 finding)**: `FrameOrchestrator`'s own registry field is `readonly`
  (`FrameOrchestrator.cs:38`) — there is no hand-off mechanism today, swapping in a loader-built
  registry needs a new `FrameOrchestrator` (or a non-readonly seam), not just a reference
  assignment. And the "never touch the active registry" rule is enforced only by this document today
  — a cheap thread-affinity guard on `ResourceRegistry` itself (mirroring `GameWorld`'s own posture)
  would be a natural, low-cost addition for whichever milestone actually builds the loader, not
  designed here since `ResourceRegistry` threading is explicitly out of scope for this spec.

## What did NOT need fixing (verified, not assumed)

- **`DeletionQueue`** — already thread-safe (`System.Threading.Lock` guards every mutation).
- **`ResourceTracker`** — already thread-safe (`ConcurrentDictionary` + `Interlocked`).
- **`SubmitImmediate`** — safe as written for what it actually does (fresh transient command pool +
  fence per call), but is **not** the path a scene loader will use in practice — `GpuUploader` is.
  Kept safe by the same D4a lock regardless, since it also calls `QueueSubmit2`.
- **`GraphicsQueue`/`PresentQueue`/`HasVulkan13Core`/`_khrDynamicRendering`/`_khrSynchronization2`**
  — written only once, in the constructor, before any second thread could exist; safe to read
  concurrently without further guarding (publication happens-before `Thread.Start`).

## Files touched

- `src/Agapanthe.Graphics/Memory/GpuAllocator.cs` — lock field; `lock` in `Allocate`, `Free`,
  `GetStats`, `LogStats`, `Dispose`; doc comment describing the new contract and the lock-ordering
  invariant (D7).
- `src/Agapanthe.Graphics/GraphicsDevice.cs` — `_ownerThreadId`/`_sanctionedLoaderThreadId`
  (`Volatile`-published) fields, **`public`** `SetSanctionedLoaderThread`/`ClearSanctionedLoaderThread`,
  `AssertCallerThread` + `AssertOwnerThreadStrict` (both always-on), `_queueLock` field,
  `CurrentFrameIndex` becomes `Interlocked`-backed (`AdvanceFrame` gets `AssertOwnerThreadStrict`),
  `WaitIdle()` takes `_queueLock` (D4c), `Dispose()` throws on a still-registered loader thread as
  its first statement (D8).
- `src/Agapanthe.Graphics/GraphicsDevice.Commands.cs` — `QueueSubmit2` takes `_queueLock` +
  `AssertCallerThread`; new `QueueSubmit` (legacy, D4b, same guard) and `QueuePresent` (D5,
  `AssertOwnerThreadStrict`) methods, same lock.
- `src/Agapanthe.Graphics/Swapchain.cs` — present call site routes through `GraphicsDevice.QueuePresent`.
- `src/Agapanthe.Graphics/GpuReadback.cs` — submit call site routes through `GraphicsDevice.QueueSubmit`.
- New windowed test host (mirrors `samples/Sandbox/Tools/IblTestTool.cs`'s pattern of a real
  `GraphicsDevice` built for a diagnostic run, not a GPU-free unit test) + one new GPU-free test in
  `tests/Agapanthe.Tests/` — see Verification.

## Verification

1. Full existing test suite stays green — behavior-preserving for the single-threaded case.
2. **GPU-free unit test** for `GpuAllocator`'s lock, using its internal mockable
   `IMemoryBackend`-based constructor, **with a real invariant oracle (round-2 finding)**: the
   round-1 draft's "two threads Allocate/Free/GetStats in a loop" has no assertion that would
   actually fail without the lock — `_allocators` (the `Dictionary` `GetStats`/`GetOrCreateAllocator`
   share) only grows on the FIRST allocation per memory type, so most iterations never touch the
   racy path at all, and this project has hit exactly this "green test that proves nothing" trap
   before (S42's shape queries, Job-2's `SimCommandQueue` guard). Corrected test: force
   `GetOrCreateAllocator` to actually race by using **at least two distinct memory-type indices**
   (so entries are still being added partway through the run, not just once at the start) and assert
   a concrete invariant that a lost update would violate — e.g. every live allocation's byte range
   never overlaps another's within the same block, and `GetStats()`'s `UsedBytes`/`AllocationCount`
   return to exactly zero once every allocation has been freed. **Round-3 refinement**: the
   `Dictionary`'s own growth barely matters as a race target (only one or two insertions total even
   with several memory types) — what actually makes the test discriminating is **at least one
   memory-type index shared by both threads**, so they both allocate/free through the same,
   genuinely non-thread-safe `FreeListAllocator` concurrently; that shared-type contention, not the
   dictionary insert, is what the invariant above is really exercising. **Verified by mutation**
   (temporarily remove the lock, confirm the test actually fails, not just "run it a few times and
   see" — round-1 finding, reconfirmed in round 2 as still the right bar).
3. **Real windowed-host test, corrected precedent (round-2 finding: `AotComponentProbe` is GPU-free
   and cannot build a `GraphicsDevice` — it requires a real `IVkSurface`/GLFW window)**: a new small
   diagnostic program mirroring `samples/Sandbox/Tools/IblTestTool.cs`'s actual pattern (a real
   window + `GraphicsDevice` built for a one-shot diagnostic run). Two separate `GpuUploader`
   instances (one per thread, never shared — D9), both calling `Upload` against the same
   `GraphicsDevice` concurrently, for N iterations, while `ResourceTracker.Report()` stays clean
   after `Join()`. Must run as a **Debug build** — Vulkan's validation layer (and this project's
   `FailFast`-on-error debug callback) are Debug-only. Thread-safety checking is **on by default**
   in `VK_LAYER_KHRONOS_validation` (round-2 correction: no extra flag/config needed, unlike
   synchronization validation which this project explicitly opts into via
   `VK_EXT_validation_features` for a different class of bug — missing barriers, not concurrent
   queue access). The test must additionally, explicitly (round-2 finding: the round-1 plan only
   exercised the lock's happy path):
   - **Cover D4c**: have the owner thread call something that triggers `WaitIdle` (e.g. a simulated
     resize) *while* the loader thread is mid-`Upload`, and confirm no validation message fires and
     no corruption results — this is "the most serious gap" the whole revision centers on, and
     nothing in the original plan actually exercised it.
   - **Cover both guard levels distinctly (round-3 correction)**: an unsanctioned third thread
     throws from *either* `AssertCallerThread` or `AssertOwnerThreadStrict` — that alone doesn't
     prove the two levels actually differ. The discriminating case is the **sanctioned loader
     thread** itself calling `QueuePresent` or `AdvanceFrame` (legitimately allowed by
     `AssertCallerThread`, but not by `AssertOwnerThreadStrict`) and confirming *that* throws too —
     this is the only case that actually exercises the strict/non-strict distinction D2 introduces.
   - **Cover D8**: confirm `Dispose()` throws if `ClearSanctionedLoaderThread()` was never called
     while a loader thread is still registered (a synthetic call, not a real live thread, is enough
     to exercise the check itself).
4. Manual: run Sandbox unchanged, confirm 0 validation messages, 0 leak, byte-identical pinned
   captures — proves the single-threaded path's behavior is untouched.

## Deferred (not designed here, on purpose)

Background-loader lifecycle/abstraction (thread creation, completion signaling, result hand-off) —
the scene-management brainstorm's job once resumed, now with D9's precise "new registry, isolated
build, explicit hand-off" constraint to design against. Arbitrary N-thread concurrent access (no
driver exists today). A full audit of every remaining `GraphicsQueue`/`GpuAllocator` touchpoint
beyond the ones this review already surfaced (`SubmitImmediate`, `GpuUploader`, `GpuReadback`) —
recommended as an explicit task before this ships, since round 1 already found one this draft
missed entirely; a second pass by a different reviewer before merge is warranted given the pattern.

## Corrections from review round 1 (independent `csharp-lowlevel` review, weighted 2.80/5)

- **Central premise fixed**: the loader thread's real GPU work goes through `GpuUploader`, not
  `SubmitImmediate` — D2's guard placement corrected accordingly.
- **Missed submission site fixed**: `GpuReadback.cs:111`'s raw `vkQueueSubmit` now routed through a
  new locked `GraphicsDevice.QueueSubmit` (D4b).
- **Most serious gap fixed**: `WaitIdle()` is called mid-loop (resize, IBL, Renderer), not just at
  Dispose — original D7 only guarded Dispose. Now `WaitIdle()` itself takes the queue lock (D4c).
- **`GetStats()` unlocked, fixed**: added to D3's lock scope.
- **`QueuePresent` design fixed**: was going to break `ErrorOutOfDateKhr`/`SuboptimalKhr` handling
  by throwing like `QueueSubmit2`; now returns `Result`, matches `Swapchain.Present`'s existing
  contract, and takes `Queue` explicitly instead of a redundant `SwapchainKHR` parameter.
- **Lock ordering now documented**: DeletionQueue → Allocator (D7), previously unstated.
- **"Deferred" section's honesty fixed**: D9 states precisely what a loader thread may safely do
  (build an isolated new `ResourceRegistry`) versus what it must never do (mutate the active one) —
  previously vague enough to hide a real constraint.
- **Guard promoted to always-on**: mirrors Job-2's own precedent for exactly this reasoning
  (non-overlapping cross-thread calls invisible in Release otherwise) — not a round-1 finding per
  se, but adopted while fixing D2's placement.
- **Test plan rewritten**: the original "allocate/free a small buffer in a loop" test never
  actually reached `QueueSubmit2` (`GpuAllocator` doesn't call it) and its resources were disposed
  through the deferred `DeletionQueue`, so nothing was ever freed concurrently — replaced with a
  GPU-free mutation-verified allocator test plus a real windowed-host test that genuinely exercises
  concurrent `GpuUploader.Upload`, under the correctly-named Vulkan validation feature
  (thread-safety checking, not synchronization validation).

## Corrections from review round 2 (independent `csharp-lowlevel` review, weighted 3.45/5)

- **🔴 blocking bug fixed**: `SetSanctionedLoaderThread` was `internal`, but `Agapanthe.Graphics`'s
  only `InternalsVisibleTo` grants are `Agapanthe.Tests` and `ShaderPrecompiler` (verified directly
  — `GpuAllocator.cs:7`, `ShaderCompiler.cs:10`) — no future consumer of this API could ever have
  called it. Now `public`, paired with a new `public ClearSanctionedLoaderThread()` (D1) that didn't
  exist at all in round 1, despite D8's precondition requiring a way to satisfy it.
- **Fixed, non-sequitur justification removed**: D3 originally argued `LogStats`/`Dispose` were
  "covered by `WaitIdle` having already serialized against any in-flight submitter" — false,
  `Allocate`/`Free` never take `_queueLock`. Both methods now take `GpuAllocator`'s own lock
  directly, closing a real `VkDeviceMemory` use-after-free scenario the round-1 draft's reasoning
  had talked past rather than closed.
- **Fixed, two-level guard**: the original single `AssertCallerThread` (owner-or-loader) was applied
  uniformly; `QueuePresent` and `AdvanceFrame` now use a stricter `AssertOwnerThreadStrict`
  (owner-only) since a loader thread calling either is a bug, not a path to permit — mirrors
  `GameWorld`'s own `AssertOwnerThread`/`AssertOwnerThreadStrict` split, which the round-1 draft cited
  as its precedent but did not actually follow.
- **Fixed, `Volatile` publication**: `_sanctionedLoaderThreadId` is read from both threads; now
  `Volatile`-published, matching `GameWorld._sanctionedWorkerThreadIds`'s own pattern.
- **Fixed, D8 fully specified**: round 1 said Dispose "keeps its always-on check" — none existed;
  now explicit (`throw`, placed after the existing `if (_disposed) return;` idempotency check but
  still before `_disposed = true` — corrected again in round 3, see below — paired with the new
  `Clear` API and a stated caller contract).
- **Fixed, `_queueLock` declared a leaf lock + `WaitIdle` post-condition contract documented** (D7).
- **Fixed, `PresentQueue`/`GraphicsQueue` aliasing justification made explicit** (D5) — a future
  "optimization" splitting the lock per queue would silently reopen the exact hazard this spec closes.
- **Fixed, test precedent corrected**: `AotComponentProbe` is GPU-free and cannot construct a
  `GraphicsDevice` (needs a real `IVkSurface`); the real precedent is
  `samples/Sandbox/Tools/IblTestTool.cs`.
- **Fixed, allocator test given a real oracle**: the original test could pass identically with or
  without the lock (`_allocators` only grows once per memory type) — this project has hit exactly
  this failure mode before (S42, Job-2's own guard test) — now forces the racy path with multiple
  memory types and asserts a concrete invariant.
- **Fixed, windowed test now covers D4c/the guard/D8 explicitly**, not just the lock's happy path.
- **Fixed, D9 strengthened and two new constraints surfaced**: the isolation `ResourceRegistry.Load`
  provides is structural (verified: `SceneBuilder.cs:56` builds its own uploader/allocator/cache per
  call), not caller-maintained discipline as round 1 implied; `MaterialSetLayout`'s lifetime
  dependency, `FrameOrchestrator`'s `readonly` registry field (no hand-off mechanism exists today),
  and the lack of a real thread-affinity guard on `ResourceRegistry` are now stated as concrete
  constraints for the scene-management brainstorm, not left implicit.

## Corrections from review round 3 (independent `csharp-lowlevel` review, weighted 4.20/5, PASS)

Round 3 confirmed all seven round-2 corrections landed correctly, converged on **PASS**, and found
three remaining one-line gaps plus three non-blocking refinements — all fixed here, no further
review round is needed per the round-3 verdict itself.

- **Fixed (blocking-before-implementation #1)**: D8's check must go *after* the real `Dispose()`'s
  existing `if (_disposed) return;`, not literally before it as "first statement" could be
  (mis)read — placed before that early-return, a second `Dispose()` call following a successful
  first one would still re-throw, breaking `IDisposable`'s idempotency contract. Corrected: the
  check sits right after the existing early-return, still before `_disposed = true`.
- **Fixed (blocking-before-implementation #2)**: `SetSanctionedLoaderThread`/
  `ClearSanctionedLoaderThread` had three unstated behaviors — which thread may call them
  (`AssertOwnerThreadStrict`, D2 — a loader must never (de)sanction itself or another thread),
  what happens on a second `Set` while a loader is already registered ("at most one" now enforced
  by a throw, not just stated), and their behavior after `Dispose()` (`ObjectDisposedException`,
  matching every other public member).
- **Fixed (blocking-before-implementation #3)**: the windowed test's "third unsanctioned thread"
  case threw from *either* guard level, proving nothing about the strict/non-strict distinction
  D2 introduces. The actually discriminating case — the **sanctioned loader thread** calling
  `QueuePresent`/`AdvanceFrame` and being rejected by `AssertOwnerThreadStrict` despite being a
  legitimate caller of `AssertCallerThread` elsewhere — is now the test's explicit assertion.
- **Fixed (desirable, non-blocking)**: `GpuAllocator.Allocate`/`Free`'s existing
  `ObjectDisposedException.ThrowIf` checks move inside the lock, closing the exact
  check-then-act gap D3 exists to prevent.
- **Fixed (desirable, non-blocking)**: the allocator test's real discriminator is a memory-type
  index shared by both threads (contention on one `FreeListAllocator`), not the dictionary's own
  growth — now stated explicitly rather than left to be inferred from "at least two types."
- **Fixed (desirable, non-blocking)**: D4c's list of `WaitIdle()` callers was a representative
  sample, not exhaustive — `Swapchain.Dispose` and `AppHost.cs:389/407` added to make it complete.
