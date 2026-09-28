# Scene management — sequential async scene switching (v4)

## Summary

Today, `AppHost.SelectRecipe` picks exactly one `ISceneRecipe` once at startup. Motivating need:
splash → menu → game, sequential switching without a process restart. Sequential only —
simultaneous coexistence is a distinct, later project.

Second half of a two-part brainstorm; part one designed `GraphicsDevice` thread-safety
(`docs/plans/2026-09-27-graphicsdevice-thread-safety-design.md`, 3 rounds, 4.20/5 PASS) — approved,
not yet implemented, a hard prerequisite here.

**Revision history**: v1 scored 2.45/5 MAJOR GAPS — its core flaw was constructing the new
`GameWorld`/`SimulationHost` on the loader thread, impossible because their owner-thread guards are
permanent (`readonly`, set at construction, no rebind API). v2 fixed that (ECS population moved
entirely to the main thread) but scored 3.10/5 NEEDS WORK on round 2 — three real 🔴 bugs
(use-after-free disposing the old `ResourceRegistry` without a prior `WaitIdle`; stale
`CopySyncState`/`PersistentInstanceBuffer` state on the surviving `Renderer` causing the GPU cull
to read the wrong scene's candidates after a switch; `HostOptions with { ... }` doesn't compile —
`with` requires a `record`, not a plain `sealed class`), plus a threading bug in `D9.1`
(`RequestSceneSwitch` must never itself spawn/join a thread — it can be called from a
non-owner-thread `ISystem`), a hole in `Build` failure handling after teardown, missing
`CancellationToken` plumbing, opt-in (leaky) subscription cleanup, and a silent double-decode of
every model. v3 fixed those and scored 3.60/5 NEEDS WORK on round 3 (the last automated round this
process allows) — five remaining, bounded gaps, all editorial/precision fixes rather than new
architectural problems per the reviewer's own assessment: the exact reset call chain for D3 was
unnamed; the `Func<AssetKey, ModelAsset>` overload was misattributed to `SceneLoader.LoadHeadless`
instead of the real `SceneMaterializer.Materialize`; `ScopedWindow`'s `Dispose`/`Run` semantics and
a same-frame multicast-snapshot race were unaddressed; the `sim`/`world`/`registry`/`orchestrator`
hoisting needed to be stated explicitly instead of assumed; and `Camera`/`FreeCameraController`
being "reset per switch" (D2) added closure-staleness risk for no real benefit, since
`SceneCameraApplier.Apply` already fully re-poses them per scene. All fixed in this v4 (the
reviewer's own recommendation: apply directly, no further automated round). See "Corrections from
review round 3" at the end.

## Decisions

- **D1 — Sequential only.** Unchanged.
- **D2 — Full reset per switch.** `GameWorld`, `SimulationHost`, `ResourceRegistry` destroyed and
  rebuilt every switch; a fresh `FrameOrchestrator` wraps them (D9).
- **D3 — What survives the whole process, corrected**: `GraphicsDevice`, `Swapchain`, `Renderer`,
  `FrameRenderer`, the window, `AudioDevice`. **Correction (round-2 finding)**: `Renderer` is *not*
  free of per-scene state — `PersistentInstanceBuffer`/`CopySyncState` (verified:
  `CopySyncState.cs`, `_copyVersion[]` initialized to `uint.MaxValue` **only in the constructor**)
  retain the *previous* scene's structural-version bookkeeping. A new scene's `SceneCandidateSet`
  starts its own version counter independently; if it happens to coincide with a value the old
  `CopySyncState` slot was last written with, `PlanFrame` wrongly concludes "already current" and
  skips the copy — the GPU cull would read the *old* scene's candidate data against the *new*
  scene's batch/handle tables. **New (round-3: exact call chain named)**: `Renderer.ResetSceneState()`
  calls a new internal `PersistentInstanceBuffer.ResetSync()`, which calls a new `CopySyncState
  .Reset()` — clearing `_copyVersion[]` back to `uint.MaxValue` and resetting `_countdown`/`_active`/
  `_replay` to the same state the constructor sets (the constructor now delegates to `Reset()` too,
  so "freshly reset" has exactly one definition). Called at hand-off (D9.3), before `recipe.Build`.
  **Also (round-3: the rest of `Renderer`'s scene-scoped state)**: three more fields are per-scene
  but were left unaddressed — the IBL environment (`SetEnvironment` is not called at all for
  `SceneEnvironmentMode.None`/a missing HDRI file), `Lights.Directional` (only overwritten if the new
  scene's TOML declares one), and `ShadowDistance` (only written if the new scene's camera declares
  `> 0`, the `MoveSpeed`/`ShadowDistance` sentinel convention from Contenu-3c-1). Rather than force
  an unconditional reset of fields with no well-defined "empty" value (a scene omitting a directional
  light has no meaningful default to fall back to), `ResetSceneState()` resets only what has one — the
  IBL environment, to `BlackEnvironment.Build()` (the same default `ClubArchitectGame`'s
  `EmptySceneRecipe` already uses) — and this spec states plainly, rather than glosses over, that
  `Lights.Directional`/`ShadowDistance` are **inherited from the previous scene unless the new
  scene's `Build` explicitly overwrites them**. Every existing `SceneRecipe`-driven scene already
  declares both unconditionally (verified: every `content/scenes/*.toml` sets `[camera]` and either
  a light or `environment.mode`) — a documented latent gap for a hypothetical future recipe that
  omits them, not a live bug today.
- **D4 — Trigger, corrected placement**: `SimSceneContext.RequestSceneSwitch(string sceneName)`
  **only records the request** (a private pending field, exactly like `RequestRestore`'s existing
  `_pending`) — it does **not** spawn a thread, call `SetSanctionedLoaderThread`, or block. **Fixes
  a round-2 finding**: a system can legitimately call this from inside `Tick`, which may run on a
  non-owner thread under Job-1's scheduler; spawning/joining/sanctioning there would hit
  `AssertOwnerThreadStrict` or block inside a simulation wave. All actual orchestration (D9) runs
  from `AppHost`'s `window.Rendered` poll, at the same frame boundary `resizePending` already uses
  — never from inside `Tick`.
- **D5 — Client-only.** Unchanged.
- **D6 — Async via the bounded loader thread.** Unchanged.
- **D7 — `ISceneRecipe` gains one method; `Build` gains one parameter**, unchanged in shape from
  v2, **with two corrections**:
  ```csharp
  public interface ISceneRecipe
  {
      string Name { get; }
      bool Matches(string? sceneToken) => ...;

      // Safe on the loader thread (or inline on the main thread for a synchronous load). GPU-free
      // content decode + GPU upload into background.Registry (a fresh registry, D8). Must observe
      // background?.CancellationToken between per-model iterations (round-2 finding — was entirely
      // absent). Never touches GameWorld/SimulationHost/Renderer — unreachable from `background`.
      object? PrefetchBackground(AssetCatalog catalog, BackgroundPresentationContext? background) => null;

      // MUST run on the owner/main thread. GameWorld/SimulationHost are constructed fresh HERE,
      // by the orchestration, immediately before this call — never handed across threads.
      void Build(object? prefetched, SimSceneContext sim, PresentationSceneContext? presentation);
  }
  ```
  **Correction (round-2 scope finding)**: `PrefetchBackground` now has a **default implementation**
  (`=> null`) — an interface default method, the same pattern this interface already uses for
  `Matches`. `EmptySceneRecipe`, `ThinClientSceneRecipe`, and the test fakes need **zero changes**;
  only `SceneRecipe` (the one implementation with real prefetch work) overrides it. This removes
  v2's overstated "real breaking API change" for every trivial recipe — `Build`'s new leading
  parameter is the only unavoidable signature change, and it is mechanical (existing bodies are
  unchanged, they simply ignore the new parameter unless they call `PrefetchBackground` themselves).
- **D8 — `BackgroundPresentationContext`, gains a `CancellationToken`** (round-2 finding — was
  missing entirely, making D9's "checked once per model" claim unimplementable):
  ```csharp
  public sealed class BackgroundPresentationContext
  {
      public required GraphicsDevice Device { get; init; }
      public required ResourceRegistry Registry { get; init; }
      public required DescriptorSetLayout MaterialSetLayout { get; init; }
      public required CancellationToken CancellationToken { get; init; }
  }
  ```
- **D9 — Switch orchestration, corrected in five places (round-2 findings), plus a hoisting
  clarification (round-3 finding)**. All of this runs from `AppHost`'s `window.Rendered` poll
  (never from `Tick`, per D4's correction). **Concrete hoisting**: `RunClient` already declares
  `camera`, `controller`, `world`, `registry`, and (via `FrameOrchestrator.CreateDefault`) the
  orchestrator as plain mutable `var` locals at its top level, before `window.Loaded` — this is
  unchanged from today's code (verified: `AppHost.cs:64-65`, `var camera = new Camera(); var
  controller = new FreeCameraController();`). The only NEW local is `sim` (a `SimSceneContext`,
  currently constructed inline inside the `window.Loaded` closure and never referenced outside it)
  — it is hoisted to the same top-level scope as the others, assigned once at first load and
  reassigned at every hand-off (D9.3), so the `window.Rendered` poll can read `sim`'s pending
  switch request (D4) without capturing a stale closure over the first scene's instance. No other
  closure needs to change: `world`/`registry`/`orchestrator` are reassigned by ordinary variable
  assignment inside the poll (the same pattern `resizePending`'s handling already uses for
  `swapchain`), not by rebuilding the closures that capture them.
  **`camera`/`controller` are deliberately NOT in that reassigned set — round-3 correction of an
  earlier draft that rebuilt them per switch.** `SceneCameraApplier.Apply` (verified:
  `SceneCameraApplier.cs:19-34`) already takes the existing `camera`/`controller` by reference and
  fully re-poses every field a scene can declare (`FovY`, `Position`, `MoveSpeed`,
  `ShadowDistance`, projection) — there is nothing left over from the old scene for a fresh
  instance to avoid inheriting. Reconstructing them per switch would instead have been a net
  negative: every demo key handler that closes over `camera`/`controller` today (`KeyPressed`'s
  `F`/`G`/`H` raycast/overlap probes, `PageUp`/`Home` exposure adjustment, `FramebufferResized`'s
  aspect-ratio update) captures them once at subscription time — rebuilding the instances per
  switch would silently stale those closures (the exact class of bug D11 exists to close for
  `window.Updated`/`KeyPressed`, reintroduced here for no benefit). Keeping them process-lifetime
  means **zero closure code needs to change** for this design to land.
  1. **Starting a load**: when a pending `RequestSceneSwitch` exists and no load is currently in
     flight, resolve the recipe, create a `CancellationTokenSource`, spawn the loader thread,
     `SetSanctionedLoaderThread`, call `recipe.PrefetchBackground(catalog, background)` on it
     (`background.Registry` a fresh `ResourceRegistry`).
  2. **A new request arriving while a load is in flight**: cancel the in-flight token; **do not**
     start the new load yet — record it as the next-pending request. Once the in-flight loader
     thread observes cancellation and exits (checked in the same per-frame poll, non-blocking — no
     `Join()` call happens inside a single frame's worth of main-thread work), dispose its
     (never-activated, so no `WaitIdle` needed — see D9.6) `background.Registry`, `Join()`,
     `ClearSanctionedLoaderThread()`, then start the next-pending request per step 1. An
     acknowledged wait exists between attempts (`SetSanctionedLoaderThread` throws if a loader is
     already registered) but it never blocks a frame — it spans however many frames the
     cancellation takes to be observed.
  3. **On successful completion** (polled per frame): `Join()`, `ClearSanctionedLoaderThread()`.
     Dispose the **old** scene's `ScopedWindow` (D11 — unsubscribes everything it wired up).
     **`frameRenderer.WaitIdle()`** (round-2 🔴 fix — `ResourceRegistry.Dispose` frees
     `DescriptorAllocator` pools **synchronously**, not through the deferred `DeletionQueue`;
     `DescriptorAllocator.cs:136`'s own doc comment: "Only valid after `GraphicsDevice.WaitIdle`:
     the persistent sets are not deferred... so no frame may still reference them" — disposing the
     active registry without this first is a use-after-free on in-flight frames' descriptor sets).
     Dispose the *old* `ResourceRegistry`/`GameWorld`/`SimulationHost` (not `FrameOrchestrator` — it
     owns nothing, not `IDisposable`). Construct fresh `GameWorld`/`SimulationHost` **on the main
     thread, right here**. Reassign the `registry` method-local to `background.Registry` (the
     loader thread's fresh, already-uploaded one). **`camera`/`controller` are NOT reconstructed**
     (round-3 correction — see below). Construct a **new** `PresentationSceneContext`
     (`Registry = registry`, `Orchestrator = ` the new `FrameOrchestrator`, the **same**
     `Camera`/`FreeCameraController` instances, `Window = ` a **new `ScopedWindow` wrapping the
     same raw `IWindow`** (D11 — corrects D9.3's earlier "same Window" wording, which contradicted
     D11's per-scene `ScopedWindow`), the *same* `Renderer`/`RenderList`/`SceneSystemFactories`).
     Re-register the *same* `DebugOverlaySystem`/`UiRenderSystem` instances on the new
     `FrameOrchestrator` (D13). Call `renderer.ResetSceneState()` (D3). Call `recipe.Build(prefetched,
     sim, presentation)` — this is where `SceneLoader.LoadHeadless` populates the new world and
     `ResolveMeshRefs` resolves against the now-active registry, exactly as `Build` already does
     today (round-2 finding: v2 wrongly placed `ResolveMeshRefs` in the orchestration, before a
     world even existed to resolve against — corrected, it stays inside `Build`, unchanged from
     today's code).
  4. **`PrefetchBackground` failure**: log, dispose its `background.Registry` (never activated, no
     `WaitIdle` needed), keep the current scene running (D10).
  5. **A `Build` failure after teardown has already happened** (round-2 finding — v2 had no answer
     here): **fatal**, by design, not recoverable — there is no old scene left to fall back to once
     its `GameWorld`/`ResourceRegistry` are disposed (step 3). Mirrors today's actual behavior for a
     startup failure (an exception from `window.Loaded` already propagates to `RunClient`'s
     catch-all → `failed = true` → clean teardown, exit 1). `PrefetchBackground` should validate
     whatever it safely can (recipe/system-factory existence, content-key resolution) to shrink
     this fatal window, but `Build` itself failing post-teardown is an unrecoverable error, stated
     plainly rather than implying a resilience this design cannot actually provide.
  6. **Window close while a load is in flight**: cancel the token, wait (blocking is acceptable
     here — this is shutdown, not a frame-budget-sensitive path) for the loader thread to exit,
     dispose its registry, `Join()`, `ClearSanctionedLoaderThread()` — a new `AppHost` teardown
     step, before device disposal (the thread-safety spec's `Dispose()` throws otherwise).
- **D10 — Error handling.** Unchanged (see D9.4).
- **D11 — Per-scene subscription cleanup, redesigned (round-2 finding: the v2 `SceneSession`
  opt-in design was leaky — any future subscription site, including in an external package like
  Club Architect, that forgets to register with it reproduces the exact crash this exists to fix).
  New **`ScopedWindow : IWindow`** — wraps the real `IWindow`, forwards every member, but tracks
  every `+=` against its own events and exposes `Dispose()` to unsubscribe all of them at once,
  plus a `RegisterCleanup(Action)` for non-event disposables (e.g. `ThinClientSceneRecipe`'s
  `NetChannel`, closing that recipe's own long-standing "no ordered teardown hook" debt as a side
  effect). `PresentationSceneContext.Window` becomes a `ScopedWindow` instead of the raw `IWindow`
  — **zero changes needed at the five existing subscription call sites**
  (`SceneInput.EnableFreeFly`, `RecipeInput`, `LandingChallengeSystemFactory`,
  `DriveControlSystemFactory`, `ThinClientSceneRecipe`) — they already write `p.Window.Updated +=
  handler`, which now happens to go through the wrapper transparently. The orchestration disposes
  the old scene's `ScopedWindow` at D9.3, before constructing anything new. This makes the bug
  structurally hard to reintroduce, rather than a discipline every future recipe must remember.
  **Three precisions (round-3 findings, all previously unaddressed)**:
  - **`Dispose()` semantics**: `IWindow` extends `IDisposable` (verified: `IWindow.cs:19`) with
    `Run()`/`Close()` also on the interface — a naive full member-forward would let a scene's
    `Build`/a system call `presentation.Window.Dispose()` and kill the **real, process-lifetime**
    window. `ScopedWindow.Dispose()` **only** unsubscribes its tracked handlers and runs its
    `RegisterCleanup` callbacks — it never forwards to the wrapped `IWindow`'s own `Dispose`. A
    second, explicit `void CloseUnderlyingWindow()` is exposed for the one legitimate case that
    needs to actually end the process's window (not used by any scene switch path, only by
    `AppHost`'s own final teardown, which disposes the real `IWindow` directly rather than through
    a `ScopedWindow` at all).
  - **`Run()` semantics**: `Run()` blocks the calling thread running the frame loop — it makes no
    sense for a scene, which lives entirely inside frames `Run()` is already driving, to re-enter
    it. `ScopedWindow.Run()` throws `NotSupportedException` (reentrancy is a genuine misuse, not a
    case to silently forward or no-op).
  - **Multicast-snapshot race (trampoline)**: a .NET event's invocation list is snapshotted at the
    moment a multicast delegate is invoked — if `ScopedWindow.Dispose()` runs `Window.Updated -=
    _trackedHandler` *during* an in-flight `Window.Updated?.Invoke(dt)` call (e.g. the switch
    hand-off happens inside the same `window.Updated` tick that is still iterating its own,
    already-captured invocation list from before the unsubscribe), the already-in-flight call still
    reaches the just-removed handler — which may now touch a disposed `GameWorld`. `ScopedWindow`
    never subscribes the caller's raw handler directly; it wraps every forwarded handler in a small
    trampoline closure checking a private `_disposed` flag first (`Window.Updated += dt => { if
    (_disposed) return; handler(dt); };`) — sets `_disposed = true` as the *first* statement of
    `Dispose()`, before unsubscribing anything, so even an already-in-flight call becomes a no-op
    the instant `Dispose()` starts, closing the race regardless of which invocation list snapshot
    is executing.
- **D12 — Options are startup-only, corrected (round-2 🔴 fix — `with` does not compile on a plain
  `sealed class`, C# restricts it to `record`s)**: new `HostOptions.WithoutStartupOnlyPaths()`
  method (returns a copy with `LoadPath`/`SavePath` set to `null`, everything else unchanged) —
  used for `SimSceneContext.Options` on every `PrefetchBackground`/`Build` call after the very
  first one. **Also (round-2 finding)**: `AppHost.RunClient`'s own post-`Build` Save/Restore-apply
  steps (`AppHost.cs` around line 177, which reads the outer `options` local directly, not
  `sim.Options`) must themselves be gated to the first load only — a switch never re-triggers
  `AGAPANTHE_SAVE`/re-applies a pending restore, because D14 unifies the code path and that guard
  must travel with it.
- **D13 — `DebugOverlaySystem`/`UiRenderSystem` survive a switch, corrected (round-2 🔴 fix)**:
  `FrameStats` is owned by `SimulationHost` (`SimulationHost.cs:292`, `public FrameStats Stats {
  get; } = new();`) — a fresh `SimulationHost` per switch means a fresh `FrameStats`, but
  `DebugOverlaySystem` captured the *old* one permanently at construction
  (`AppHost.cs:115`, `orchestrator.Simulation.Stats`). Left as v2 described it, the profiler's
  history would silently freeze at the moment of the first switch — exactly the kind of wrong-but-
  not-crashing bug this project's own gate culture is built to catch, caught here instead of at
  runtime. Fix: `SimulationHost.CreateDefault` gains an optional `FrameStats?` parameter (defaults
  to `new()` if omitted — every other existing caller unaffected); `AppHost` owns one `FrameStats`
  for the whole process and passes it to every `SimulationHost.CreateDefault` call, first load and
  every switch alike. `DebugOverlaySystem`/`UiRenderSystem` themselves are the same surviving
  instances, re-registered on the new `FrameOrchestrator` (D9.3) — this part of v2 was correct.
  **(round-3 finding: compatibility confirmed, not just asserted)** the new parameter is optional
  and every existing caller of `SimulationHost.CreateDefault` — `HeadlessSim`, `DedicatedServer`,
  `AotComponentProbe`, `FrameOrchestrator.CreateDefault`'s own call, and the ~20 tests that
  construct a `SimulationHost` directly — passes none of the existing positional/named arguments
  it would clash with, so all of them keep compiling unchanged and each still gets its own private
  `new FrameStats()` exactly as before; only `AppHost` opts into sharing one.
- **D14 — First-load path uses the same mechanism, inline.** Unchanged: `PrefetchBackground` then
  `Build`, synchronously, no thread, for the very first scene.

## Reused prefetch → no silent double-decode (round-2 finding, new)

`SceneRecipe.PrefetchBackground` collects the target scene's referenced model keys (a new
extracted `SceneMaterializer.CollectModelKeys(def)` helper — avoids duplicating the existing
`DriveControl`/`ProbeModel.IsNone` guard logic between prefetch and materialize), decodes each via
`catalog.LoadModel(key)` (GPU-free), uploads each via `background.Registry.Load(...)`, and returns
a `Dictionary<AssetKey, ModelAsset>` as its `object?` result. **Correction (round-3 finding)**:
`SceneLoader.LoadHeadless` has exactly **one** overload (verified: `SceneLoader.cs:21-23`) — it
takes an `AssetCatalog`, not a `Func<AssetKey, ModelAsset>`, and simply forwards to
`SceneMaterializer.Materialize(def, catalog, world, fixedDeltaSeconds, spawnEntities)`; the
dictionary-backed overload lives only on `SceneMaterializer` itself (verified:
`SceneMaterializer.cs:34-35`, `Materialize(SceneDefinition def, Func<AssetKey, ModelAsset>
loadModel, GameWorld world, float fixedDeltaSeconds, bool spawnEntities = true)`).
`SceneRecipe.Build` therefore calls `SceneMaterializer.Materialize` **directly**, passing the
prefetched dictionary's indexer as the `loadModel` delegate — bypassing `SceneLoader.LoadHeadless`
entirely for this path (it stays as-is, unchanged, for the headless/dedicated-server callers that
have no prefetch dictionary to hand it) — instead of re-decoding every model from disk on the main
thread. This closes a hole where the entire async benefit would otherwise silently disappear (a
21 MB Deflate-compressed model, inflated twice, once per thread, defeats the whole point of
backgrounding it).

## Files touched (representative)

- `src/Agapanthe.App/ISceneRecipe.cs` — `PrefetchBackground` (DIM, default `null`), `Build` gains
  a parameter (D7).
- New `src/Agapanthe.App/BackgroundPresentationContext.cs` (D8).
- New `src/Agapanthe.App/ScopedWindow.cs` (D11 — trampoline-wrapped forwarding, `Dispose()` never
  reaches the real `IWindow`, `Run()` throws, `CloseUnderlyingWindow()` for `AppHost`'s own final
  teardown only).
- `src/Agapanthe.App/HostOptions.cs` — `WithoutStartupOnlyPaths()` (D12).
- `src/Agapanthe.Scene/SceneMaterializer.cs` — extract `CollectModelKeys` (reused prefetch section).
- `src/Agapanthe.App/Scene/SceneRecipe.cs` — implement `PrefetchBackground`; `Build` calls
  `SceneMaterializer.Materialize` directly with the prefetched dictionary (not
  `SceneLoader.LoadHeadless`, round-3 correction).
- `src/Agapanthe.App/Scene/ClientScenePresenter.cs` — the upload loop moves to `PrefetchBackground`;
  lights/environment/camera/input stay here, called from `Build`, unchanged.
- `src/Agapanthe.App/SimSceneContext.cs` — `RequestSceneSwitch` records-only (D4).
- `src/Agapanthe.Rendering/Renderer.cs` — new `ResetSceneState()`, delegating to
  `PersistentInstanceBuffer.ResetSync()` (D3); also resets the IBL environment to
  `BlackEnvironment.Build()`.
- `src/Agapanthe.Rendering/PersistentInstanceBuffer.cs` — new internal `ResetSync()` (D3).
- `src/Agapanthe.Rendering/CopySyncState.cs` — new `Reset()`, called by both the constructor and
  `PersistentInstanceBuffer.ResetSync()` (D3).
- `src/Agapanthe.Engine/SimulationHost.cs` — `CreateDefault` gains an optional `FrameStats?`
  parameter (D13).
- `src/Agapanthe.App/AppHost.cs` — hoist `sim` to `RunClient`'s top-level scope alongside the
  existing `camera`/`controller`/`world`/`registry` locals (D9, round-3).
- `src/Agapanthe.App/AppHost.cs` — orchestration (D9, entirely in the `window.Rendered` poll),
  options scoping (D12), shared `FrameStats` (D13), first-load unification (D14), teardown step for
  an in-flight load (D9.6).
- `samples/ThinClient/ThinClientSceneRecipe.cs` — optionally adopts `ScopedWindow.RegisterCleanup`
  for its `NetChannel` (closes a pre-existing, separately-documented debt item as a side effect,
  not required for this spec to land).

## Verification

1. Existing single-scene startup path byte-identical.
2. A real, live switch: press a key the OLD scene wired up after switching away — confirm nothing
   happens (D11, via `ScopedWindow`, not a crash). New scene renders with its own lights/
   environment. 0 leak, 0 validation message throughout, **including at the hand-off's
   `WaitIdle`** (D9.3) and while the loader thread is mid-upload concurrent with the render thread
   — the first real exercise of the thread-safety spec under contention.
3. Switch between two *structurally static* scenes (single rebuild each, matching structural
   versions) — confirm `AGAPANTHE_CULL_VERIFY` shows GPU-visible == CPU-visible for the *new*
   scene's own candidates post-switch (round-2 finding: this is the specific case that would have
   silently rendered the wrong data without D3's `ResetSceneState` fix).
4. Concurrent-switch-request case: request A then B before A's prefetch completes — confirm no
   frame ever blocks waiting for A's cancellation (D9.2), A's registry is disposed once observed,
   only B ends up live.
5. `AGAPANTHE_LOAD` at startup: first scene restores; switching away does not re-trigger a restore
   or a save (D12) — verified against `sim.Options`, not just the recipe's own read of it.
6. Switch away from and back to a scene with `F3` visible: profiler history is continuous (D13,
   verified against the shared `FrameStats` instance, not just "no crash").
7. Close the window mid-load: clean 0-leak shutdown (D9.6).
8. A `PrefetchBackground` failure: current scene keeps running, logged, no leak (its registry
   disposed without needing `WaitIdle`, since it was never activated).
9. A `Build` failure after teardown: confirm the process exits cleanly (code 1, matching an
   existing startup failure) rather than an unhandled crash or corrupted state — D9.5's fatal
   contract, tested rather than left implicit.

## Dependency

Blocked on `docs/plans/2026-09-27-graphicsdevice-thread-safety-design.md` being implemented.

## Deferred (explicitly out of scope)

Simultaneous scene coexistence (D1). Server-side scene/map switching (D5). Sub-model cancellation
granularity finer than "once per model" (D7). A generic loading-screen UI. Recovering from a
`Build` failure after teardown (D9.5 — fatal by design, not a gap left to close later without
rethinking the teardown-then-build ordering itself, which is out of scope for this milestone).

## Corrections from review round 2 (independent `engine-architect` review, weighted 3.10/5)

- **🔴 fixed**: disposing the active `ResourceRegistry` without a prior `WaitIdle` — a genuine
  use-after-free on in-flight frames' descriptor sets (`DescriptorAllocator.cs:136`'s own doc
  comment names this exact precondition). D9.3 now calls `frameRenderer.WaitIdle()` first.
- **🔴 fixed**: `Renderer`'s surviving `CopySyncState`/`PersistentInstanceBuffer` retaining stale
  structural-version bookkeeping across a switch, verified directly (`CopySyncState.cs`'s
  `uint.MaxValue` fill runs only in the constructor) — could make the GPU cull silently read an old
  scene's candidates. New `Renderer.ResetSceneState()` (D3), called at hand-off.
- **🔴 fixed**: `FrameStats` is per-`SimulationHost` (`SimulationHost.cs:292`), not per-process —
  `DebugOverlaySystem`'s captured reference would go stale on the first switch, silently freezing
  the profiler. `SimulationHost.CreateDefault` now accepts a host-owned, persistent `FrameStats`.
- **🔴 fixed**: `HostOptions with { ... }` does not compile — `with` requires a `record`. New
  `WithoutStartupOnlyPaths()` method.
- **🔴 fixed**: `RequestSceneSwitch` doing real orchestration (spawning/joining a thread,
  sanctioning) — could run from inside `Tick` on a non-owner thread. Now records-only; all
  orchestration moved to the `window.Rendered` poll.
- **Fixed, conceptual gap**: no stated behavior for a `Build` failure after the old scene's
  teardown already happened — stated as fatal, matching existing startup-failure behavior, rather
  than implying a recovery this design cannot actually offer.
- **Fixed**: no `CancellationToken` anywhere — added to `BackgroundPresentationContext`.
- **Fixed, leaky design**: `SceneSession`'s opt-in cleanup replaced by `ScopedWindow`, which makes
  the missing-unsubscribe bug structurally unreachable rather than a discipline every future
  subscriber (including external packages) must remember — and needs zero changes at the five
  existing subscription call sites.
- **Fixed**: silent double-decode of every model (once per thread) — the prefetch's decoded/
  uploaded models are now threaded through to `Build` via `SceneMaterializer.Materialize`'s
  `Func<AssetKey, ModelAsset>` overload, instead of being thrown away and re-decoded. (Round-3
  caught that this bullet, and the "Reused prefetch" section itself, had misattributed the
  overload to `SceneLoader.LoadHeadless` — see round-3 corrections below.)
- **Fixed, misplaced call**: `ResolveMeshRefs` moved back inside `Build` (v2 wrongly placed it in
  the orchestration, before the world it resolves against even existed).
- **Fixed, scope**: `PrefetchBackground` is now a default-interface method (`=> null`), not a
  mandatory override — `EmptySceneRecipe`/`ThinClientSceneRecipe`/test fakes need zero changes,
  correcting v2's overstated breaking-change claim.
- **Recorded, not designed further**: orphaned-registry disposal on cancel/failure (D9.2/D9.4) is
  now explicit (dispose immediately, no `WaitIdle` needed — never activated, so no frame ever
  referenced its descriptor sets) — the distinction between "active registry needs `WaitIdle`" and
  "never-activated registry doesn't" is now stated as a general rule, not just asserted per case.

## Corrections from review round 3 (independent review, weighted 3.60/5 NEEDS WORK — last automated
round this process allows; reviewer's own recommendation was to apply these directly rather than
dispatch a fourth round)

- **Fixed, unnamed mechanism**: D3's `Renderer.ResetSceneState()` named no call chain — now
  `Renderer.ResetSceneState()` → `PersistentInstanceBuffer.ResetSync()` → `CopySyncState.Reset()`
  (the constructor now delegates to `Reset()` too, so there is exactly one definition of "freshly
  reset"). Also closed: the reviewer's broader point that `CopySyncState`/`PersistentInstanceBuffer`
  weren't `Renderer`'s only scene-scoped state — the IBL environment, `Lights.Directional`, and
  `ShadowDistance` are addressed explicitly rather than left implicit (the first reset to
  `BlackEnvironment.Build()`, the latter two documented as inherited from the previous scene unless
  the new scene's `Build` overwrites them, since neither has a well-defined "empty" value and every
  scene shipped today declares both anyway).
- **Fixed, misattributed reference**: the "Reused prefetch" section and one round-2 correction
  bullet both claimed `SceneLoader.LoadHeadless` has a `Func<AssetKey, ModelAsset>` overload — false
  (verified: it has exactly one overload, taking an `AssetCatalog`). The real overload is on
  `SceneMaterializer.Materialize` directly; `SceneRecipe.Build` now calls that, not
  `SceneLoader.LoadHeadless`, for this path.
- **Fixed, unaddressed semantics**: `ScopedWindow`'s `Dispose()`/`Run()` were unspecified — a naive
  forward of `Dispose()` would kill the real, process-lifetime window. `Dispose()` now only
  unsubscribes/runs cleanup callbacks, never forwards to the real `IWindow`; `Run()` throws
  (reentrancy is misuse). Also fixed: an unaddressed multicast-invocation-list-snapshot race where
  an in-flight event call could still reach a handler `Dispose()` just removed — closed via a
  trampoline (every forwarded handler checks a `_disposed` flag, set as `Dispose()`'s first
  statement, before any actual unsubscribe happens).
- **Fixed, unstated mechanism**: D9's hoisting was implicit — now explicit: `camera`, `controller`,
  `world`, `registry` are the same top-level `RunClient` locals as today (verified:
  `AppHost.cs:64-65`); only `sim` is newly hoisted to that scope, reassigned at every hand-off so
  the `window.Rendered` poll can read its pending switch request without a stale closure.
- **Fixed, unnecessary churn**: `camera`/`controller` were previously described as "freshly-built"
  per switch. Reverted to process-lifetime (never reconstructed) — `SceneCameraApplier.Apply`
  (verified: fully re-poses every field a scene can declare) already makes a fresh instance
  pointless, and reconstructing them would have staled every closure that captures them today
  (`KeyPressed`'s `F`/`G`/`H` demos, `PageUp`/`Home`, `FramebufferResized`) — the exact class of bug
  D11 exists to close, reintroduced here for zero benefit. Zero closure code needs to change.
- **Fixed, self-contradiction**: D9.3 said the new `PresentationSceneContext` reuses "the same
  ... Window" while D11 disposes and reconstructs a `ScopedWindow` per scene. Now stated correctly:
  a **new** `ScopedWindow` wrapping the **same** raw `IWindow`.
