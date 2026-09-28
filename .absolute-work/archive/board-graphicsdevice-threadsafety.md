# Absolute Work Board — GraphicsDevice thread-safety (bounded 2-thread case)

Status: **completed** (2026-09-28) — all 9 tasks done, all gates passed, spec implemented in full.

Spec: `docs/plans/2026-09-27-graphicsdevice-thread-safety-design.md` (4.20/5 PASS, 3 review rounds,
pre-approved — INTAKE/BRAINSTORM and SPEC phases skipped by explicit user instruction).

Blocks: `docs/plans/2026-09-27-scene-management-sequential-switching-design.md` (v4, approved,
cannot be implemented until this lands).

## Rollback Point

Commit `a23deb236e89939a7e89e54d81403806d223168a` (clean tree except the two untracked spec docs
above, which predate this board and are not touched by it).

## Project Conventions (detected)

- .NET 10, `dotnet build` / `dotnet test` from repo root.
- Lock primitive: `System.Threading.Lock` (`DeletionQueue.cs` precedent — private `Lock _lock` +
  `lock (_lock) { ... }`), not `Monitor`/`SemaphoreSlim`.
- Owner-thread guard shape: `GameWorld.AssertOwnerThread`/`AssertOwnerThreadStrict`
  (`src/Agapanthe.World/GameWorld.cs:231-274`) — two-level split (sanctioned-or-owner vs
  owner-only), `Volatile.Read`/`Write` on the sanctioned-thread field, `[Conditional("DEBUG")]`
  EXCEPT where a prior audit promoted a guard to always-on (Job-2's `SimCommandQueue
  .AssertOwnerThread` — this spec's own D2 cites that precedent and is always-on too, not
  Debug-only).
- Windowed diagnostic-tool precedent: `samples/Sandbox/Tools/IblTestTool.cs` — a real 64×64
  `EngineWindow` + `GraphicsDevice` built for a one-shot run, dispatched from
  `samples/Sandbox/Program.cs` via an env var (`AGAPANTHE_IBL_TEST=<prefix>`), teardown in a
  `finally` calling `DeletionQueue.FlushAll()` + `Dispose()` + `ResourceTracker.Report()`.
- `GpuAllocator` test seam: `internal GpuAllocator(IMemoryBackend, Func<uint, MemoryDomain, uint>)`
  ctor, `[InternalsVisibleTo("Agapanthe.Tests")]` (`GpuAllocator.cs:7`) — GPU-free test possible.
- Gate culture: 0 validation message / 0 leak / byte-identical pinned captures is the standing
  bar; `TreatWarningsAsErrors` on; never work around a failing gate.
- Commits: **never** run `git commit`/`git push` — only the user does, on request.

## Verified against real code (not assumed)

- `WaitIdle()` lives in `GraphicsDevice.cs:230` (not `.Commands.cs`).
- `QueueSubmit2` lives in `GraphicsDevice.Commands.cs:78`.
- `GpuReadback.cs:111` — raw `vk.QueueSubmit(device.GraphicsQueue, 1, &submit, fence)` confirmed.
- `Swapchain.cs:112` — `_device.KhrSwapchain.QueuePresent(_device.PresentQueue, &presentInfo)`
  confirmed, `Result` inspected for `ErrorOutOfDateKhr`/`SuboptimalKhr` before `VkCheck.ThrowIfFailed`.
- `DeletionQueue.cs` already thread-safe (`Lock _lock` guards every mutation) — no change needed,
  only a lock-ordering doc comment (D7).
- `GpuAllocator.cs` — confirmed **zero** locking today; `Allocate`/`Free`/`GetStats`/`LogStats`/
  `Dispose` all touch the unguarded `Dictionary<uint, FreeListAllocator> _allocators`.
- `GameWorld.cs:67` — `_ownerThreadId` captured via `Environment.CurrentManagedThreadId` at field
  init, the exact pattern to mirror in `GraphicsDevice`.

## Task DAG

```
Wave 1 (parallel — disjoint files)
  AW-001 GpuAllocator lock (D3)              AW-002 GraphicsDevice core contract (D1/D2/D4c/D6/D8)
        [GpuAllocator.cs]                          [GraphicsDevice.cs]
              |                                          |
              |                                          v
              |                                  Wave 2
              |                                  AW-003 Commands.cs submit/present (D4a/D4b/D5)
              |                                          [GraphicsDevice.Commands.cs]
              |                                          |
              |                          -----------------------------
              |                          |                           |
              |                          v                           v
              |                  Wave 3 (parallel — disjoint files)
              |                  AW-004 Swapchain.cs        AW-005 GpuReadback.cs
              |                  routes QueuePresent         routes QueueSubmit
              |                          |                           |
              +--------------------------+---------------------------+
                                          |
                                          v
                              Wave 4
                              AW-006 windowed diagnostic tool
                              (ThreadSafetyProbeTool + Program.cs dispatch)
                              covers D4c / two guard levels / D8
                                          |
                                          v
                              Wave 5 (sequential tail, mandatory)
                              AW-007 Self Code Review
                                          |
                              AW-008 Requirements Validation (D1-D9 checklist)
                                          |
                              AW-009 Full Project Verification
                              (full test suite, manual Sandbox run, byte-identical captures)
```

## Tasks

### AW-001 — `GpuAllocator` real lock (D3) [M] — ✅ DONE

`Lock _lock` added, `Allocate`/`Free`/`GetStats`/`LogStats`/`Dispose` all wrapped, disposed-check
moved inside the lock in `Allocate`/`Free` (round-3 fix). Doc comment updated with the new contract
+ D7 lock-ordering invariant. Test `GpuAllocatorConcurrencyTests.cs` written first (TDD), confirmed
RED against the unlocked code (`InvalidOperationException: Collection was modified`,
`NullReferenceException` — real `List<Region>` corruption in `FreeListAllocator`, not a benign
race), then GREEN after the lock. **Verified by mutation**: reverted to the pre-lock file via
`git checkout`, re-ran the test — failed with `NullReferenceException` inside `Dispose()` (same
corrupted-list class of failure) — then restored the locked version, all 12 `GpuAllocator*Tests`
green again. One test-harness bug found and fixed along the way (not a product bug): the
overlap-detector's bookkeeping removed from its tracking dictionary *after* calling `Free`, which
raced with a legitimately-freed offset being reused by the other thread and produced a false
"double issue" report — fixed by reordering to remove-then-free.

### AW-002 — `GraphicsDevice` core thread contract (D1, D2, D4c, D6, D8) [M] — ✅ DONE

All items landed: `_ownerThreadId`/`_sanctionedLoaderThreadId` (plain `int`, `0` = unset sentinel —
`int?` doesn't compile with `Volatile.Read<T>`/`Write<T>`, which require `T : class`; a boxed
`object?` was considered and rejected to keep `AssertCallerThread` allocation-free on what may
become a hot path) + `_queueLock` fields; `SetSanctionedLoaderThread`/`ClearSanctionedLoaderThread`
(both `AssertOwnerThreadStrict`-guarded, throw on double-set, `ObjectDisposedException` after
dispose); `AssertCallerThread`/`AssertOwnerThreadStrict` (always-on); `WaitIdle()` now takes
`_queueLock`; `CurrentFrameIndex` is `Interlocked`-backed (getter via `Interlocked.Read`,
`AdvanceFrame` via `Interlocked.Increment` + `AssertOwnerThreadStrict`); `Dispose()`'s D8 check
placed exactly after the existing `if (_disposed) return;`, before `_disposed = true`. Full solution
build 0 warnings/0 errors; full test suite **1015/1015 green** (1014 baseline + the new
`GpuAllocatorConcurrencyTests`). No standalone unit test for the guard/D8 behavior — by design, see
the task's own note; exercised by AW-006's windowed tool.

**Original plan (for reference)** — `src/Agapanthe.Graphics/Memory/GpuAllocator.cs` (modify); new
`tests/Agapanthe.Tests/GpuAllocatorConcurrencyTests.cs`. Deps: none. Wave 1 (parallel with AW-002).

- Private `System.Threading.Lock _lock = new();` field.
- Wrap `Allocate`, `Free`, `GetStats()`, `LogStats()`, `Dispose()` bodies in `lock (_lock) { ... }`.
- Move the existing `ObjectDisposedException.ThrowIf(_disposed, this)` in `Allocate`/`Free`
  **inside** the lock (round-3 non-blocking fix — closes the exact check-then-act gap the lock
  exists to prevent).
- Doc comment: replace "Not thread-safe (phase-1 rendering is single-threaded)" remark with the
  new contract + the lock-ordering invariant (D7: DeletionQueue → Allocator, never the reverse;
  this lock is never held while acquiring `DeletionQueue`'s, and vice versa).
- **Test (TDD, write first)**: `GpuAllocatorConcurrencyTests.cs` using the existing GPU-free
  `internal GpuAllocator(IMemoryBackend, Func<uint, MemoryDomain, uint>)` ctor (mock backend, no
  GraphicsDevice). Two threads share **at least one common memory-type index** (round-3 finding:
  the discriminator is contention on one `FreeListAllocator`, not the dictionary's own growth,
  which only inserts once or twice regardless of thread count) and race
  `Allocate`/`Free`/`GetStats()` in a loop. Invariant asserted: every live allocation's byte range
  never overlaps another's within the same block, and `GetStats().UsedBytes`/`AllocationCount`
  return to exactly zero once every allocation is freed.
  **Verify by mutation before closing this task**: temporarily remove the lock, confirm the test
  actually fails (not just "ran green a few times") — then restore the lock and confirm green.
  Record the mutation result on this board entry when done.

### AW-002 — `GraphicsDevice` core thread contract (D1, D2, D4c, D6, D8) [M]
**Files**: `src/Agapanthe.Graphics/GraphicsDevice.cs` (modify).
**Deps**: none. **Wave**: 1 (parallel with AW-001).

- `_ownerThreadId` field: `private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;`
  (mirrors `GameWorld.cs:67` exactly).
- `_sanctionedLoaderThreadId` field: `private int? _sanctionedLoaderThreadId;` — read/written via
  `Volatile.Read`/`Volatile.Write` only (round-2 finding — both threads read it).
- `private readonly Lock _queueLock = new();` field (consumed by AW-003/AW-004's `WaitIdle`
  change below and by AW-003's Commands.cs methods via partial-class field sharing). Doc comment:
  declares it a **leaf lock** — nothing held while holding `_queueLock` ever acquires
  `DeletionQueue`'s or `GpuAllocator`'s lock, and nothing while holding either of those acquires
  `_queueLock` (D7).
- `public void SetSanctionedLoaderThread(int threadId)`: guarded by `AssertOwnerThreadStrict()`
  (below); throws `InvalidOperationException` if a loader is already registered ("at most one",
  now enforced not just stated — round-3 finding); throws `ObjectDisposedException` if disposed.
- `public void ClearSanctionedLoaderThread()`: same three guards (owner-only, no-op is not
  allowed — round-3: unregister must be explicit), `ObjectDisposedException` if disposed.
- `private void AssertCallerThread([CallerMemberName] string caller = "")`: **always-on, not
  `[Conditional("DEBUG")]`** (D2 — mirrors Job-2's promotion of `SimCommandQueue.AssertOwnerThread`
  for the same reasoning: a non-overlapping cross-thread call is otherwise invisible in Release,
  and a missed lock here is native-crash-class). Owner **or** sanctioned loader passes.
- `private void AssertOwnerThreadStrict([CallerMemberName] string caller = "")`: always-on, owner
  **only** — used by `QueuePresent`/`AdvanceFrame`/`SetSanctionedLoaderThread`/
  `ClearSanctionedLoaderThread`.
- `WaitIdle()` (already exists at line 230): wrap body in `lock (_queueLock) { ... }` (D4c — the
  most serious gap in the original draft; `vkDeviceWaitIdle` requires external sync against any
  concurrent submit on any thread, and this is called mid-loop from `Swapchain.Recreate`/
  `Swapchain.Dispose`, `Renderer.cs:1561`, `IblGenerator.cs:284`, `FrameRenderer.cs:366`,
  `AppHost.cs:389`/`407` — not just at final teardown).
- `CurrentFrameIndex`: backing field becomes `Interlocked`-managed (`Interlocked.Increment` in
  `AdvanceFrame`, `Interlocked.Read`/`Volatile.Read` for the getter — a `long` needs
  `Interlocked.Read` on 32-bit, `Volatile.Read` suffices on 64-bit register width per .NET's own
  guarantee, but use `Interlocked.Read` for portability since the type is public API). `AdvanceFrame`
  gains `AssertOwnerThreadStrict()` (D2 — a loader never advances the frame counter).
- `Dispose()`: immediately after the existing `if (_disposed) { return; }` early-return, **still
  before** `_disposed = true;` (round-3 placement fix, exact line), add:
  `if (Volatile.Read(ref _sanctionedLoaderThreadId) is { } stillRegistered) { throw new
  InvalidOperationException($"GraphicsDevice.Dispose was called while loader thread
  {stillRegistered} is still sanctioned. Call ClearSanctionedLoaderThread() after Join()-ing it
  first."); }`.
- **No standalone unit test for this task** — per the spec's own Verification §3, the guard
  levels and D8's check are only meaningfully exercised against a **real** `GraphicsDevice`
  (thread ids, `ObjectDisposedException` timing), which is what AW-006's windowed tool does.
  Noted here so the tail "Requirements Validation" task doesn't mistake the absence of a unit
  test file for a missed requirement.

### AW-003 — `GraphicsDevice.Commands.cs`: locked submit/present (D4a, D4b, D5) [S] — ✅ DONE

`QueueSubmit2` gained `AssertCallerThread()` + `lock (_queueLock)`. New `internal void QueueSubmit(Queue,
SubmitInfo*, Fence)` (D4b) and `internal Result QueuePresent(Queue, PresentInfoKHR*)` (D5, returns
`Result`, guarded by `AssertOwnerThreadStrict`, calls `KhrSwapchain.QueuePresent` under the same lock).
Builds clean.

### AW-004 — `Swapchain.cs` present routes through `GraphicsDevice.QueuePresent` (D5) [S] — ✅ DONE

One-line call-site swap at the former line 112; `ErrorOutOfDateKhr`/`SuboptimalKhr` branching
untouched.

### AW-005 — `GpuReadback.cs` submit routes through `GraphicsDevice.QueueSubmit` (D4b) [S] — ✅ DONE

One-line call-site swap; `vk` local confirmed still used elsewhere in the method (buffer/pool/fence/
image-transition calls), no unused-variable warning.

**Wave 2+3 checkpoint**: full solution build 0 warnings/0 errors, full test suite **1015/1015 green**.

### AW-003 — `GraphicsDevice.Commands.cs`: locked submit/present (D4a, D4b, D5) [S]
**Files**: `src/Agapanthe.Graphics/GraphicsDevice.Commands.cs` (modify).
**Deps**: AW-002 (needs `_queueLock`, `AssertCallerThread`, `AssertOwnerThreadStrict`).
**Wave**: 2.

- `QueueSubmit2`: wrap body in `lock (_queueLock)`, call `AssertCallerThread()` at entry (before
  the lock — matches `GameWorld`'s pattern of asserting before touching guarded state).
- New `internal void QueueSubmit(Queue queue, SubmitInfo* submit, Fence fence)`: same
  `AssertCallerThread()` + `lock (_queueLock)`, body is
  `VkCheck.ThrowIfFailed(_vk.QueueSubmit(queue, 1, submit, fence), "vkQueueSubmit")` — the exact
  call `GpuReadback.cs:111` makes today, moved here so AW-005 can route through it.
- New `internal Result QueuePresent(Queue queue, PresentInfoKHR* info)`: `AssertOwnerThreadStrict()`
  (not `AssertCallerThread` — a loader never presents, D5) + `lock (_queueLock)`, body is
  `return _vk.KhrSwapchain.QueuePresent(queue, info);` — wait, `KhrSwapchain` is the extension
  object already exposed via `KhrSwapchain` property; call
  `return KhrSwapchain.QueuePresent(queue, info);` and **return the raw `Result`**, never throw
  (round-1 fix — `Swapchain.Present` must keep inspecting `ErrorOutOfDateKhr`/`SuboptimalKhr`
  itself).

### AW-004 — `Swapchain.cs` present routes through `GraphicsDevice.QueuePresent` (D5) [S]
**Files**: `src/Agapanthe.Graphics/Swapchain.cs` (modify).
**Deps**: AW-003. **Wave**: 3 (parallel with AW-005 — disjoint file).

- Line 112: `_device.KhrSwapchain.QueuePresent(_device.PresentQueue, &presentInfo)` →
  `_device.QueuePresent(_device.PresentQueue, &presentInfo)`. The following `if (result is
  Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)` / `VkCheck.ThrowIfFailed(result, ...)` logic
  is **unchanged** — this is a one-line call-site swap, nothing else in `Swapchain.cs` moves.

### AW-005 — `GpuReadback.cs` submit routes through `GraphicsDevice.QueueSubmit` (D4b) [S]
**Files**: `src/Agapanthe.Graphics/GpuReadback.cs` (modify).
**Deps**: AW-003. **Wave**: 3 (parallel with AW-004 — disjoint file).

- Line 111: `VkCheck.ThrowIfFailed(vk.QueueSubmit(device.GraphicsQueue, 1, &submit, fence),
  "vkQueueSubmit");` → `device.QueueSubmit(device.GraphicsQueue, &submit, fence);` (the new
  wrapper already does the `VkCheck.ThrowIfFailed` internally with the same error label — round-2
  clarification in the spec). Confirm the `vk` local becomes otherwise-unused only where expected
  (it is still used elsewhere in the same method for buffer/pool/fence creation) — do not remove
  the `var vk = device.Api;` local.

### AW-006 — Windowed diagnostic tool: exercises D4c + both guard levels + D8 [M] — ✅ DONE

**Blocking discovery, not scope creep**: `QueuePresent`/`AdvanceFrame`/`PresentQueue` are `internal`, and
`Agapanthe.Graphics`'s only `InternalsVisibleTo` grants were `Agapanthe.Tests`/`ShaderPrecompiler` —
`Sandbox` had no access. Added `[assembly: InternalsVisibleTo("Sandbox")]` to `GraphicsDevice.cs`
(mirrors the existing `ShaderPrecompiler` grant pattern) plus `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`
to `Sandbox.csproj` (the `QueuePresent(Queue, PresentInfoKHR*)` call site needs unsafe context).

New `samples/Sandbox/Tools/ThreadSafetyProbeTool.cs` + 2-line `Program.cs` dispatch
(`AGAPANTHE_THREAD_SAFETY_TEST=<N>`). All 4 checks implemented exactly as specced: baseline concurrent
upload (two never-shared `GpuUploader`s, D9), D4c (owner `WaitIdle()` raced against the loader's in-flight
uploads, no synchronization point — that's the point), guard-level discrimination (the sanctioned loader
thread itself, having just proven it legitimately passes `AssertCallerThread` via its own uploads,
rejected by `AssertOwnerThreadStrict` on `QueuePresent`/`AdvanceFrame`), D8 (synthetic sanction +
`Dispose()` must throw, then cleared so the tool's own teardown stays clean).

**Live run, locked**: `AGAPANTHE_THREAD_SAFETY_TEST=2000` → all 4 checks PASS, `ResourceTracker: no leaks
(2032 resources)`, exit 0. Re-run 3× at N=5000 after the mutation test below — all green each time
(5032 resources, 0 leak each run).

**Mutation-verified live, not just by unit test** — the spec's own centerpiece fix (D4c): temporarily
removed `lock (_queueLock)` from `WaitIdle()`'s body only, rebuilt, ran the probe 3× — **every single run
crashed immediately** via the project's Debug `FailFast`-on-validation-error callback:
`vkDeviceWaitIdle(): THREADING ERROR : object of type VkQueue is simultaneously used in current thread
<owner> and thread <loader>`. This is Vulkan's own validation layer catching the exact race D4c exists to
close, on real hardware (NVIDIA RTX 5070 Ti), not a synthetic assertion. Restored the lock, re-verified
green 3×. This is the strongest evidence in the whole spec that the fix is load-bearing, not
belt-and-suspenders.

Full solution build 0 warnings/0 errors after Wave 4; full test suite **1015/1015 green** (unchanged from
Wave 1-3 — this wave touched no test-suite code, only the new Sandbox tool + the `InternalsVisibleTo`/
`AllowUnsafeBlocks` additions).


**Files**: new `samples/Sandbox/Tools/ThreadSafetyProbeTool.cs`; modify
`samples/Sandbox/Program.cs` (2-line dispatch, mirrors the `AGAPANTHE_IBL_TEST` block).
**Deps**: AW-001, AW-002, AW-003, AW-004, AW-005 (needs every lock/guard actually wired).
**Wave**: 4.

Mirrors `IblTestTool.Run`'s exact shape: a real 64×64 `EngineWindow` + `GraphicsDevice` built for
a one-shot diagnostic run, dispatched via a new env var `AGAPANTHE_THREAD_SAFETY_TEST=<N>` (N =
upload iterations per thread), teardown in a `finally` (`DeletionQueue.FlushAll()` → `Dispose()`
→ `ResourceTracker.Report()`), returns `0`/`1`.

Inside `window.Loaded`, after the device exists:

1. **Baseline concurrent upload (happy path)**: two `GpuUploader` instances (one per thread,
   never shared — D9), each running N `Upload` calls against the same `GraphicsDevice`, on a
   background thread sanctioned via `device.SetSanctionedLoaderThread(loaderThread.ManagedThreadId)`
   before it starts. Owner thread stays on the main/window thread throughout. After `Join()`,
   `device.ClearSanctionedLoaderThread()`, then `ResourceTracker.Report()` must stay clean —
   Debug build only (validation layer + `FailFast`-on-error callback are Debug-only); no extra
   config needed (thread-safety checking is on by default in `VK_LAYER_KHRONOS_validation`,
   unlike synchronization validation which needs `VK_EXT_validation_features`).
2. **D4c — WaitIdle concurrent with an in-flight loader upload**: while the loader thread is
   mid-`Upload` (loop running), the owner thread calls `device.WaitIdle()` (simulating a resize)
   at least once. Assert: no validation message fires (the debug callback's `FailFast` would
   already kill the process — absence of a crash plus a clean `ResourceTracker.Report()` after
   `Join()` is the pass condition), no corruption.
3. **Guard-level discrimination (round-3 correction — the actually discriminating case)**: from
   the **sanctioned loader thread** itself (not a third, unsanctioned thread — that would only
   prove *a* guard fires, not that the two levels differ), call `device.QueuePresent(...)` (a
   synthetic/dummy `PresentInfoKHR*` is acceptable — the assertion under test is that
   `AssertOwnerThreadStrict` throws before any Vulkan call happens) and separately
   `device.AdvanceFrame()` — while that same thread is still validly sanctioned (so it legitimately
   passes `AssertCallerThread` on submits) — confirm **both throw**
   `InvalidOperationException`. This is the one case that actually proves `AssertCallerThread`
   (owner-or-loader) and `AssertOwnerThreadStrict` (owner-only) are genuinely different gates.
4. **D8 — Dispose precondition**: after the loader thread from step 1 has exited and been
   `Join()`-ed, **skip** `ClearSanctionedLoaderThread()` on purpose and confirm a subsequent
   `Dispose()` throws `InvalidOperationException` naming the still-registered thread id (a
   synthetic check is enough per the spec — no live thread needs to be running at this instant).
   Then, in a **separate** device instance (or reset state) for the rest of the tool's own clean
   teardown, do call `ClearSanctionedLoaderThread()` properly so the tool's own final
   `ResourceTracker.Report()` stays clean — this step must not leave the process's real teardown
   dirty.

Log a clear pass/fail line per numbered check (mirrors `IblTestTool`'s `Log.Info` style) so a
human reading the console output can see all 4 checks passed distinctly, not just an aggregate
exit code.

### AW-007 — Self Code Review (tail, mandatory) — ✅ DONE

Read every diff (`git diff --stat` matched the spec's own "Files touched" list exactly — no
unexpected file touched). Full `GraphicsDevice.cs`/`.Commands.cs`/`GpuReadback.cs`/`Swapchain.cs`
diffs re-read line by line against D1-D9. **1 real gap found**: D7 says the lock-ordering invariant
is "documented on all three types" (`DeletionQueue`, `GpuAllocator`, `GraphicsDevice`) — the first
two and `GraphicsDevice`'s `_queueLock` field comment had it, but `DeletionQueue.cs` itself never
got a doc comment. Fixed: added a `<para>` to its class summary stating its lock is independent of
`_queueLock` and is acquired before `GpuAllocator`'s, never the reverse. Rebuilt + full suite
re-run green after the fix (1015/1015). Confirmed the "what did NOT need fixing" list (`ResourceTracker`,
`SubmitImmediate`, `GraphicsQueue`/`PresentQueue`/etc.) was not touched, per `git diff --stat`.

### AW-008 — Requirements Validation (tail, mandatory) — ✅ DONE

Walked the spec's own 4-item Verification list:
1. Full suite green, behavior-preserving — 1015/1015.
2. `GpuAllocatorConcurrencyTests` mutation-verified (AW-001).
3. Windowed host covers D4c + both guard levels + D8 — all 4 checks pass live (AW-006), D4c
   additionally mutation-verified on real hardware (see AW-006 entry — `THREADING ERROR`
   reproduced 3/3 without the lock, RTX 5070 Ti).
4. Manual Sandbox run: 0 validation, 0 leak (167 resources), **capture hash
   `9a010fc311dd51b74f755d306d4a819f`** matches the pinned `model` baseline exactly (`CLAUDE.md`'s
   Audio-1 entry: `9a010fc3…`) — confirms this spec's changes are invisible to the rendered pixel
   output, as expected for a pure thread-safety hardening pass.

AW-002's "no standalone test" note confirmed intentional — the guard/D8 behavior IS exercised, by
AW-006, not skipped.

### AW-009 — Full Project Verification (tail, mandatory) — ✅ DONE

- `dotnet build` (full solution): 0 warnings, 0 errors.
- `dotnet test` (full solution): **1015/1015 green**.
- Manual Sandbox run (`MetalRoughSpheres.glb`, `AGAPANTHE_MAX_FRAMES=1`): 0 validation messages,
  `ResourceTracker: no leaks`, `AppHost: clean shutdown, no GPU resource leaks`, capture
  byte-identical to the pinned baseline (see AW-008 item 4).
- `AGAPANTHE_THREAD_SAFETY_TEST=3000` run **3× consecutively**: all 4 checks PASS every time, 0
  leak every time (3032 resources each run).

**No regressions, no deferred fixes, no known gaps left in this spec's own scope.** The three items
in the spec's own "Deferred" section (background-loader lifecycle/abstraction, arbitrary N-thread
access, a second-pass audit of remaining touchpoints) remain explicitly out of scope, as designed —
the first is `scene-management`'s own job now that this prerequisite is built.

## Deferred Work (out of scope, per spec's own "Deferred" section)

- Background-loader lifecycle/abstraction (thread creation, completion signaling, result hand-off)
  — the scene-management spec's own job (`docs/plans/2026-09-27-scene-management-sequential-switching-design.md`).
- Arbitrary N-thread concurrent access (no driver exists today).
- A full second-pass audit of every remaining `GraphicsQueue`/`GpuAllocator` touchpoint beyond
  `SubmitImmediate`/`GpuUploader`/`GpuReadback` (spec's own recommendation, flagged as a distinct
  future task, not silently absorbed here).
- A thread-affinity guard on `ResourceRegistry` itself (D9's noted future need, explicitly out of
  scope for this spec).
