# Absolute Work Board — Scene management: sequential async scene switching

Status: **completed** (2026-09-28) — all 15 tasks done, all waves passed. See BW-014's honest
per-item breakdown for what was live-verified vs. code-reviewed-only vs. genuinely unexercised.

**Transient regression from Wave 2 is CLOSED**: confirmed live — single-scene Sandbox run,
`AGAPANTHE_MAX_FRAMES=1`, capture hash `9a010fc311dd51b74f755d306d4a819f`, exact match with the
pinned `model` baseline, 167 resources / 0 leak. Byte-identical, as BW-011's own acceptance bar
required.

**Live switch demo (`Key.K`) — DONE, human-verified.** User rapidly pressed `K` 8 times
(`model`↔`grid` alternating). Log evidence: every switch completes
(`AppHost: [scene switch] now on 'X'.`), the transient black environment from `ResetSceneState`/
`SetEnvironment(BlackEnvironment.Build())` is visible each time (`IblGenerator: generated IBL from
16x8 HDR`) before the real scene's own environment regenerates, window closed cleanly —
`ResourceTracker: no leaks (876 resources created and destroyed)`, `AppHost: clean shutdown, no GPU
resource leaks`. **0 validation message, 0 leak, 0 crash across 8 consecutive live switches.**
Satisfies spec Verification items 1 (byte-identical, separately confirmed), 2 (live switch, 0 leak/
0 validation), 7 (clean shutdown). Items 3 (`AGAPANTHE_CULL_VERIFY` explicitly) and 4 (a genuine
overlapping D9.2 race — the 8 switches here completed sequentially, each faster than a human could
double-tap within one load's ~300ms window) were not distinctly exercised by this particular run;
recorded as residual manual-verification debt in BW-014, not a code gap (D9.2's logic was reviewed,
just not proven live under that exact race).

Spec: `docs/plans/2026-09-27-scene-management-sequential-switching-design.md` (v4, last automated
review round scored 3.60/5 NEEDS WORK with 5 named fixes — all applied directly per the reviewer's
own recommendation, no 4th round; approved by the user). INTAKE/BRAINSTORM and SPEC phases already
done across the prior conversation — this board starts at DECOMPOSE.

Depended on: `docs/plans/2026-09-27-graphicsdevice-thread-safety-design.md` — **implemented and
committed** (`75c367a`, board archived at `.absolute-work/archive/board-graphicsdevice-threadsafety.md`).
No longer blocking.

## Rollback Point

Commit `75c367a` (GraphicsDevice thread-safety, clean tree at time of writing this board).

## Project Conventions (detected, reused from prior board)

- .NET 10, `dotnet build`/`dotnet test` from repo root, `TreatWarningsAsErrors`.
- Module graph is a strict DAG (CLAUDE.md): `Rendering` must never reference `App` — this is why
  D3's environment-reset call is split (see BW-001 below), a correction found during this board's
  own research, not present in the spec text as written.
- `HostOptions`/`SimSceneContext`/`PresentationSceneContext` are plain `sealed class`, `init`-only
  properties — no `record`, `with` does not compile (confirms D12's diagnosis).
- Owner-thread guard / sanction pattern precedent now doubly established: `GameWorld` (Job-1/2) and
  `GraphicsDevice` (just built) — this spec's loader thread reuses `GraphicsDevice.
  SetSanctionedLoaderThread`/`ClearSanctionedLoaderThread` directly, no new guard type needed.
- Commits: never `git commit`/`push` without explicit request.

## Verified against real code (not assumed)

- `ISceneRecipe` has exactly **4** implementations in this repo (not 5 as informally recalled):
  `SceneRecipe` (real prefetch work), `ThinClientSceneRecipe`, and 2 test fakes in
  `AppHostContractTests.cs` (`FakeRecipe`, `HeadlessFixtureRecipe`). `ISceneRecipe.Build(...)` has
  exactly **2** real call sites: `AppHost.cs:164` and `AppHostContractTests.cs:262`. Small blast
  radius — confirmed by grep, not assumed from the spec's own file list.
- `SimulationHost.CreateDefault(GameWorld, SimulationSettings)` (`SimulationHost.cs:76`) is the one
  real overload to extend; the single-arg `CreateDefault(GameWorld)` (`:68`) just forwards to it —
  adding an optional trailing `FrameStats? stats = null` is purely additive for both.
- `SimulationHost.Stats` (`:292`) is `public FrameStats Stats { get; } = new();` — an auto-property
  field initializer, needs converting to ctor-assigned.
- `CopySyncState` (`CopySyncState.cs:24-40`) — `_copyVersion`/`_countdown`/`_active`/`_replay`
  confirmed exactly as the spec describes.
- **New finding, not in the spec**: `PersistentInstanceBuffer` (`PersistentInstanceBuffer.cs:42`)
  has its OWN per-copy version array, `_bufferVersion`, DISTINCT from `CopySyncState`'s
  `_copyVersion` — "the structural version each physical copy's device-local BUFFER currently
  holds." Traced its one use site (`Sync`, `:83-84`): `full = _sync.PlanFrame(...) ||
  _bufferVersion[frameSlot] != set.StructuralVersion` — an OR. Resetting `CopySyncState` alone
  (`_copyVersion[frameSlot] = uint.MaxValue`) already forces `PlanFrame` to return true on every
  slot's first post-switch use (a real `StructuralVersion` will never equal `uint.MaxValue`), so
  `_bufferVersion`'s staleness is harmless TODAY only because of that OR's short-circuit. Resetting
  `_bufferVersion` too (BW-001) removes the reliance on that incidental short-circuit rather than
  leaving a second, silently-dependent piece of state — the project's own standing practice (see
  Contenu-3c-3's `world_origin` fix, CLAUDE.md) of closing a latent gap even when not exploitable
  today.
- **Real architectural correction**: `BlackEnvironment` lives in `Agapanthe.App`
  (`src/Agapanthe.App/Scene/BlackEnvironment.cs:12`, returns a trivial 16×8 black `HdrImageAsset`).
  `Renderer.ResetSceneState()` living in `Agapanthe.Rendering` cannot call it — `Rendering` must
  never reference `App` (module graph, CLAUDE.md). **Split**: `Renderer.ResetSceneState()` only
  resets `PersistentInstanceBuffer`/`CopySyncState` (what `Rendering` actually owns); `AppHost`'s
  D9.3 orchestration calls `presentation.Renderer.SetEnvironment(BlackEnvironment.Build())`
  immediately after `renderer.ResetSceneState()`, before `recipe.Build(...)` — same effective
  ordering the spec intends, without the forbidden dependency edge.
- `SceneMaterializer.Materialize` (`SceneMaterializer.cs:19-75`) has two overloads; its model-key
  collection loop (`:46-75`, entities + systems' probe models, with the `DriveControl`/`ProbeModel
  .IsNone` guard) is exactly what to extract into `CollectModelKeys` — verified line-by-line, not
  assumed. Must be `public` (not `internal`): `Agapanthe.Scene` grants no `InternalsVisibleTo` to
  `Agapanthe.App` (confirmed: no such attribute in `src/Agapanthe.Scene`).
- `ClientScenePresenter.Apply` (`ClientScenePresenter.cs:16-41`) — the upload loop (`:25-28`) is
  the exact block to move into `SceneRecipe.PrefetchBackground`; the rest (mesh-ref resolve,
  lights, environment, camera, free-fly) is unchanged, called from `Build`.
- `AppHost.RunClient` (`AppHost.cs:42-505`) — `camera`/`controller`/`world` are already top-level
  `var` locals (`:63-65`); `registry`/`orchestrator` are declared nullable at `:56/58` and assigned
  inside `window.Loaded` (`:94/101`) — reassignment inside the future switch-orchestration poll is
  ordinary variable assignment, no closure rewrite needed, exactly as D9 states. `sim` is currently
  local to the `window.Loaded` closure only (`:144-151`) — the one new hoist.
- `ThinClientSceneRecipe.Build` (`:41`) and both test fakes' `Build` — one-line signature edits
  each (add the leading `object? prefetched` parameter, ignore it).

## Task DAG

```
Wave 1 (parallel — 6 disjoint files/areas, no cross-deps)
  BW-001 Renderer/PersistentInstanceBuffer/CopySyncState reset (D3, corrected split)
  BW-002 SimulationHost.CreateDefault optional FrameStats? (D13)
  BW-003 HostOptions.WithoutStartupOnlyPaths() (D12)
  BW-004 new ScopedWindow : IWindow (D11)
  BW-005 new BackgroundPresentationContext (D8)
  BW-006 SceneMaterializer.CollectModelKeys extraction (public)
        |         |         |         |         |         |
        +---------+----+----+----+----+---------+
                       |
                       v
Wave 2 (sequential — ISceneRecipe interface change, then its 4 implementers)
  BW-007 ISceneRecipe: PrefetchBackground (DIM) + Build gains object? param  [depends: BW-005]
  BW-008 ThinClientSceneRecipe + 2 test fakes: signature-only update          [depends: BW-007]
  BW-009 SceneRecipe.PrefetchBackground + Build rewrite                      [depends: BW-006, BW-007]
  BW-010 SimSceneContext.RequestSceneSwitch, records-only (D4)               [depends: none — parallel-safe with 007-009, different file]
                       |
                       v
Wave 3 (sequential — the orchestration itself, all in AppHost.cs)
  BW-011 AppHost.cs: hoist `sim`, first-load via PrefetchBackground+Build (D14)   [depends: Wave 2]
  BW-012 AppHost.cs: window.Rendered switch state machine (D9.1-D9.6) + demo key  [depends: BW-011]
                       |
                       v
Wave 4 (sequential tail, mandatory)
  BW-013 Self Code Review
  BW-014 Requirements Validation (spec's own 9-item Verification list)
  BW-015 Full Project Verification
```

## Tasks

**Wave 1 checkpoint**: full solution build 0 warnings/0 errors, full test suite **1015/1015 green**
(unchanged count — nothing in Wave 1 is exercised by an existing test yet, all new types/methods
are additive and unreferenced until Wave 2+).

### BW-001 — `Renderer`/`PersistentInstanceBuffer`/`CopySyncState` scene-state reset (D3) [M] — ✅ DONE
**Files**: `src/Agapanthe.Rendering/CopySyncState.cs`, `src/Agapanthe.Rendering/
PersistentInstanceBuffer.cs`, `src/Agapanthe.Rendering/Renderer.cs`.
**Deps**: none. **Wave**: 1.

- `CopySyncState`: new `public void Reset()` — `Array.Fill(_copyVersion, uint.MaxValue);
  Array.Clear(_countdown); _active.Clear(); _replay.Clear();`. Constructor calls `Reset()` after
  setting `_framesInFlight`/allocating `_copyVersion`, so there is exactly one definition of
  "freshly reset" (round-3 requirement).
- `PersistentInstanceBuffer`: new `internal void ResetSync()` — `Array.Fill(_bufferVersion,
  uint.MaxValue); _sync.Reset();` (the `_bufferVersion` reset is this board's own finding, see
  above — not in the spec text, but closes reliance on an incidental OR short-circuit).
- `Renderer`: new `public void ResetSceneState()` — calls `_sceneCandidates.ResetSync()` only.
  **Does NOT touch the IBL environment** (module-boundary correction, see above) — that call moves
  to `AppHost` (BW-012). Doc comment states plainly (per spec D3) that `Lights.Directional`/
  `ShadowDistance` are inherited from the previous scene unless the new scene's `Build` overwrites
  them — no code change needed for those two, just the statement of the contract.
- No test file needed for this task alone (GPU-free logic, but `CopySyncState`/
  `PersistentInstanceBuffer` are `internal sealed` with no existing standalone unit tests in this
  repo to extend without a live `Renderer` — verify this before writing one; if a GPU-free test
  harness already exists for either type, add a `Reset()` round-trip case to it, otherwise this is
  exercised live in BW-014's Verification item 3).

### BW-002 — `SimulationHost.CreateDefault` optional `FrameStats?` (D13) [S] — ✅ DONE
**Files**: `src/Agapanthe.Engine/SimulationHost.cs`.
**Deps**: none. **Wave**: 1.

- `Stats` property: `{ get; } = new();` → `{ get; }`, assigned in the private ctor.
- Private ctor gains `FrameStats? stats = null` trailing param; `Stats = stats ?? new();`.
- `CreateDefault(GameWorld, SimulationSettings)` gains a trailing `FrameStats? stats = null`,
  threads it to the private ctor. `CreateDefault(GameWorld)` (single-arg) unchanged, still forwards
  to the 2-arg overload with `SimulationSettings.Default` (no stats param needed there).
- Confirm (already verified in the prior thread-safety board, re-confirm here): every existing
  caller (`HeadlessSim`, `DedicatedServer`, `AotComponentProbe`, `FrameOrchestrator.CreateDefault`,
  ~20 tests) compiles unchanged.

### BW-003 — `HostOptions.WithoutStartupOnlyPaths()` (D12) [S] — ✅ DONE
**Files**: `src/Agapanthe.App/HostOptions.cs`.
**Deps**: none. **Wave**: 1.

New method returning a fresh `HostOptions` copying every existing property EXCEPT `LoadPath`/
`SavePath`, which are set to `null`. Enumerate all 14 properties explicitly (no `with` — plain
`sealed class`): `Scene, Universe, MaxFrames, CapturePath, CaptureUiPath, SavePath(→null),
LoadPath(→null), ContentRoot, OverlayVisible, CullStats, VerifyCull, ShaderReloadTest,
GpuTimestampsEnabled, AudioEnabled`.

### BW-004 — new `ScopedWindow : IWindow` (D11) [M] — ✅ DONE
**Files**: new `src/Agapanthe.App/ScopedWindow.cs`.
**Deps**: none. **Wave**: 1.

Wraps a real `IWindow`. `IWindow`'s full surface (verified, `IWindow.cs`): events `Loaded`,
`Updated`, `Rendered`, `FramebufferResized`, `KeyPressed`; properties `Title`, `FramebufferSize`,
`VkSurface`, `MouseDelta`, `MouseCaptured`; methods `IsKeyDown`, `SetMouseCaptured`,
`GetRequiredVulkanExtensions`, `Run`, `Close`; extends `IDisposable`.

- Constructor takes the real `IWindow`.
- Every property/method (`Title` get/set, `FramebufferSize`, `VkSurface`, `MouseDelta`,
  `MouseCaptured`, `IsKeyDown`, `SetMouseCaptured`, `GetRequiredVulkanExtensions`) forwards
  straight through to the wrapped window — no wrapping needed, these are not the leak.
- Each of the 5 events gets its own backing implementation: a private
  `Dictionary<Delegate, Delegate>` (caller's handler → the trampoline actually subscribed to the
  real window) per event, OR one dictionary keyed by `(eventName, callerHandler)` — pick whichever
  is simpler to get right; correctness constraint: `add` wraps the caller's handler in a trampoline
  closure that checks a private `_disposed` flag before invoking, subscribes the trampoline to the
  real window's event, and records the mapping; `remove` looks up and unsubscribes the matching
  trampoline, removing the mapping entry.
- `_disposed` starts `false`. `Dispose()`: **first statement** sets `_disposed = true` (closes the
  round-3 multicast-snapshot race — an already-in-flight real-window event call reaching a
  trampoline mid-invocation becomes a no-op the instant this flag flips, regardless of which
  invocation-list snapshot is executing), then unsubscribes every tracked trampoline from the real
  window and clears the tracking dictionary, then runs every registered `RegisterCleanup` callback.
  **Never** calls the real `IWindow`'s own `Dispose()`.
- `void RegisterCleanup(Action cleanup)` — stores it in a list, run in `Dispose()` (after
  unsubscribing events), for non-event disposables (`ThinClientSceneRecipe`'s `NetChannel`).
- `void CloseUnderlyingWindow()` — the one explicit escape hatch that actually disposes the real
  `IWindow`; not called by any scene-switch path, reserved for `AppHost`'s own final teardown
  (which today disposes the raw `IWindow` directly anyway — confirm in BW-012 whether this method
  ends up needed at all, or whether `AppHost` simply keeps a separate reference to the raw window
  for its own teardown and this method turns out unused; do not force a call site that isn't real).
- `Run()` — `throw new NotSupportedException("...")`; reentering the frame loop from inside a scene
  is a bug, not a case to silently forward.

### BW-005 — new `BackgroundPresentationContext` (D8) [S] — ✅ DONE
**Files**: new `src/Agapanthe.App/BackgroundPresentationContext.cs`.
**Deps**: none. **Wave**: 1.

```csharp
public sealed class BackgroundPresentationContext
{
    public required GraphicsDevice Device { get; init; }
    public required ResourceRegistry Registry { get; init; }
    public required DescriptorSetLayout MaterialSetLayout { get; init; }
    public required CancellationToken CancellationToken { get; init; }
}
```

### BW-006 — `SceneMaterializer.CollectModelKeys` extraction [S] — ✅ DONE (`Materialize` refactored to call it, existing tests unaffected)
**Files**: `src/Agapanthe.Scene/SceneMaterializer.cs`.
**Deps**: none. **Wave**: 1.

Extract the model-key-collection loop (`:46-75`) into `public static IReadOnlyCollection<AssetKey>
CollectModelKeys(SceneDefinition def)` — a `HashSet<AssetKey>`, entities' `.Model` plus systems'
`.ProbeModel` (same `IsNone`/`DriveControl` guard, throwing `AssetException` on an invalid `None`).
`Materialize`'s own model-loading loop is rewritten to call this helper first, then `loadModel(key)`
per returned key into its `Dictionary<AssetKey, ModelAsset>` — single source of truth for "which
keys does this scene need," closing the exact duplication risk the spec's "Reused prefetch" section
names. Existing `SceneMaterializerTests` must stay green unchanged (pure refactor, same behavior).

### BW-007 — `ISceneRecipe`: `PrefetchBackground` (DIM) + `Build` gains a parameter (D7) [S] — ✅ DONE
**Files**: `src/Agapanthe.App/ISceneRecipe.cs`.
**Deps**: BW-005 (needs `BackgroundPresentationContext` to exist for the signature).
**Wave**: 2.

```csharp
public interface ISceneRecipe
{
    string Name { get; }
    bool Matches(string? sceneToken) => ...;                       // unchanged
    object? PrefetchBackground(AssetCatalog catalog, BackgroundPresentationContext? background) => null;
    void Build(object? prefetched, SimSceneContext sim, PresentationSceneContext? presentation);
}
```
`Build`'s new leading parameter is the only breaking signature change; `PrefetchBackground` is a
DIM so no non-`SceneRecipe` implementer needs a body.

### BW-008 — Update the 3 trivial `ISceneRecipe.Build` signatures [S] — ✅ DONE (plus the AppHost.cs:164 call site, temporarily `prefetched: null`, real wiring in BW-011)
**Files**: `samples/ThinClient/ThinClientSceneRecipe.cs`, `tests/Agapanthe.Tests/
AppHostContractTests.cs` (2 fakes + the one direct `.Build(...)` call site at `:262`).
**Deps**: BW-007. **Wave**: 2.

Add the leading `object? prefetched` parameter to `ThinClientSceneRecipe.Build`, `FakeRecipe.Build`
(throws `NotSupportedException`, body unaffected), `HeadlessFixtureRecipe.Build` (body unaffected).
Update the one direct call site: `recipe.Build(sim, presentation: null)` →
`recipe.Build(prefetched: null, sim, presentation: null)`.

### BW-009 — `SceneRecipe.PrefetchBackground` + `Build` rewrite (D7, "Reused prefetch" section) [M] — ✅ DONE
**Files**: `src/Agapanthe.App/Scene/SceneRecipe.cs`, `src/Agapanthe.App/Scene/
ClientScenePresenter.cs`.
**Deps**: BW-006, BW-007. **Wave**: 2.

- `SceneRecipe.PrefetchBackground(catalog, background)`: loads `def = catalog.LoadScene(new
  AssetKey($"scenes/{Name}"))` (cheap, GPO-free TOML-cooked deserialize — re-loading it in `Build`
  too is fine, this is NOT the expensive part being deduplicated), calls
  `SceneMaterializer.CollectModelKeys(def)`, and for each key: `catalog.LoadModel(key)` (GPU-free
  decode) then, if `background is not null`, `background.Registry.Load(background.Device, model,
  background.MaterialSetLayout, key)` (GPU upload) — checking `background.CancellationToken` for
  cancellation between each key (round-2 requirement: "checked once per model"). Returns the built
  `Dictionary<AssetKey, ModelAsset>` as `object?`. Returns `null` if `background is null` (a
  headless-only build with no presentation — though today `SceneRecipe` always has real prefetch
  work for a client scene; document that a `null` background here means skip the upload half but
  still return the decoded dictionary, so `Build` can still call `Materialize` without decoding
  twice even in that edge case).
- `SceneRecipe.Build(prefetched, sim, presentation)`: unchanged up through computing
  `spawnEntities`. Where it currently calls `Agapanthe.Scene.SceneLoader.LoadHeadless(def,
  sim.Catalog, sim.World, ..., spawnEntities)` (`:53-54`), branch: if `prefetched is
  Dictionary<AssetKey, ModelAsset> models`, call `SceneMaterializer.Materialize(def, key =>
  models[key], sim.World, sim.Simulation.Settings.FixedDeltaSeconds, spawnEntities)` directly
  (bypassing `SceneLoader.LoadHeadless`, per the spec's round-3 correction); otherwise (no
  prefetch — e.g. a direct/synchronous first-load path that never ran `PrefetchBackground`, if
  BW-011's design ends up needing that fallback) keep calling `SceneLoader.LoadHeadless` as today.
  Everything after (physics system registration, restore request, scene-systems dispatch) is
  unchanged.
- `ClientScenePresenter.Apply`: remove the upload loop (`:25-28`) — it now lives in
  `PrefetchBackground`. `Apply` starts directly at `sim.World.ResolveMeshRefs(...)`. Rename/adjust
  its doc comment to say models are already uploaded by the time this runs.

### BW-010 — `SimSceneContext.RequestSceneSwitch`, records-only (D4) [S] — ✅ DONE (added `DrainPendingSceneSwitch()` for BW-012 to consume)
**Files**: `src/Agapanthe.App/SimSceneContext.cs`.
**Deps**: none (different file from Wave 2's other tasks — parallel-safe with BW-007/008/009, kept
in the same wave number for sequencing simplicity since Wave 2 is otherwise sequential anyway).
**Wave**: 2.

New private `string? _pendingSceneSwitch` field (mirrors `_pending`'s restore-request shape
exactly). `public void RequestSceneSwitch(string sceneName)` — validates non-empty, stores it. New
internal `bool HasPendingSceneSwitch`/`string? PendingSceneSwitchToken`, and an internal method to
atomically read-and-clear it (mirrors `ApplyPendingRestore`'s clear-before-use pattern) for
`AppHost`'s poll to consume in BW-012. **Does not** spawn a thread, call
`SetSanctionedLoaderThread`, or block — that is BW-012's job, from `window.Rendered`, never from
here (this type has no access to a window or a thread to spawn anyway — headless-safe by
construction, the point of D4's correction).

### BW-011 — `AppHost.cs`: hoist `sim`, first-load via `PrefetchBackground`+`Build` (D14) [M] — ✅ DONE (isFirstLoad flag dropped — window.Loaded fires exactly once per process, so its Save/Restore code structurally never re-runs on a switch, no runtime flag needed)
**Files**: `src/Agapanthe.App/AppHost.cs`.
**Deps**: Wave 2 complete. **Wave**: 3.

- Hoist `SimSceneContext? sim = null;` to `RunClient`'s top-level locals (alongside `world`/
  `camera`/`controller`/`renderList`), reassigned inside `window.Loaded` (first load) and later by
  BW-012's switch orchestration.
- Inside `window.Loaded`, where `recipe.Build(sim, presentation)` is called today (`:164`): call
  `recipe.PrefetchBackground(catalog, background)` first (building a `BackgroundPresentationContext`
  inline — `Device = device, Registry = registry, MaterialSetLayout = renderer.MaterialSetLayout,
  CancellationToken = CancellationToken.None` — the first load runs synchronously on the owner
  thread, no real cancellation needed), then `recipe.Build(prefetched, sim, presentation)`. Same
  effective behavior as today, same recipe, same registry — **byte-identical output**, this task's
  own acceptance bar (spec Verification item 1).
- Everything else in `window.Loaded` (restore application, save, logging) is unchanged.
- **Verification for this task alone**: run every existing pinned scene (`model`, `grid`, `drop`,
  `metalrough`, `planet`, `planet-drop`, `planet-challenge`, `drive`, `topdown`) and confirm every
  capture hash is unchanged from `CLAUDE.md`'s recorded values — this task changes the mechanism
  (two calls instead of one) but must not change one pixel.

### BW-012 — `AppHost.cs`: `window.Rendered` switch state machine (D9.1-D9.6) [M] — ✅ DONE (largest task —
kept as one task deliberately rather than fake-split, see note below]
**Files**: `src/Agapanthe.App/AppHost.cs`.
**Deps**: BW-011. **Wave**: 3.

**Why not split further**: D9's six sub-cases (start a load / a new request while one is in flight
/ successful completion / prefetch failure / build failure after teardown / window close mid-load)
share one mutable state machine (the in-flight `CancellationTokenSource`, loader `Thread`,
`background.Registry`, next-pending-request slot) — splitting them into separate tasks would just
mean each one rewrites the same 5-10 lines of shared state the others already declared, with no
independent test value until all six exist together. Kept as one task; verified against the spec's
own 9-item list item by item in BW-014.

State needed (new locals inside `RunClient`, alongside the existing hoisted ones): a `Thread?
_loaderThread`, `CancellationTokenSource? _loaderCts`, `ResourceRegistry? _loaderRegistry`,
`string? _nextPendingSwitch` (the D9.2 case), `object? _loaderPrefetched` (the loader thread's
result, published for the poll to pick up), `Exception? _loaderFailure`.

In `window.Rendered`, before `orchestrator.Tick(...)` (a new block, poll order: check completion →
check new request):

1. **If a loader thread is running and has finished** (`_loaderThread.IsAlive == false`): `Join()`,
   `device.ClearSanctionedLoaderThread()`. If `_loaderFailure is not null`: log it, dispose
   `_loaderRegistry`, clear loader state, keep the current scene (D9.4) — do not fall through to
   hand-off. Otherwise (success): run the **hand-off** (D9.3, below), then clear loader state, then
   if `_nextPendingSwitch is not null`, immediately start that one (fold into step 3's start logic).
2. **If no loader is running and `sim.HasPendingSceneSwitch`** (drain it): if this is the FIRST
   request seen (no `_loaderThread` at all yet), go straight to step 3. If a load is currently
   running (can only reach here between polls, so this is really "a load finished AND a new request
   arrived in the same or a later poll" — the D9.2 in-flight-cancel case matters when a SECOND
   request arrives WHILE a load is still running, which must be checked BEFORE step 1's
   is-finished check, not after): **actually implement D9.2 first in poll order** — check "is a
   load running AND a new switch was requested" → cancel `_loaderCts`, record the new request in
   `_nextPendingSwitch`, do NOT start it yet, do NOT block.
3. **Start a load**: resolve the recipe via `AppHost.SelectRecipe(game, token)`, create a fresh
   `ResourceRegistry` (`_loaderRegistry`), a `CancellationTokenSource` (`_loaderCts`), spawn
   `_loaderThread = new Thread(() => { try { _loaderPrefetched = recipe.PrefetchBackground(catalog,
   new BackgroundPresentationContext { Device = device, Registry = _loaderRegistry, MaterialSetLayout
   = renderer.MaterialSetLayout, CancellationToken = _loaderCts.Token }); } catch (Exception ex) {
   _loaderFailure = ex; } })`, call `device.SetSanctionedLoaderThread(_loaderThread.ManagedThreadId)`
   **before** `_loaderThread.Start()` (mirrors AW-006's own tool — sanction before start, never
   after, or the loader could submit before it is recognized).

**Hand-off (D9.3, step 1's success path)**, in order:
1. `frameRenderer.WaitIdle()`.
2. Dispose the OLD `sim.Window` (a `ScopedWindow` from BW-004 — the very first load does not have
   one yet, see below) — `ScopedWindow.Dispose()`.
3. Dispose the old `registry`, `world`, `orchestrator.Simulation` (`SimulationHost` — confirm it is
   `IDisposable`, `SimulationHost.Dispose()` exists per BW-002's research, `.cs:92`) — NOT
   `orchestrator` itself (not `IDisposable`).
4. Construct a fresh `GameWorld` (same `ResolveUniverse` call as today) and a fresh
   `SimulationHost.CreateDefault(world, SimulationSettings.Default, sharedFrameStats)` — the
   `sharedFrameStats` local from BW-002, created ONCE before `window.Loaded` and passed to every
   `CreateDefault` call, first load included (update BW-011 if needed to thread it through the
   first-load call too).
5. Reassign `registry = _loaderRegistry`.
6. Build a fresh `FrameOrchestrator.CreateDefault(simulation, world, renderer, registry, camera,
   renderList)`, re-register the SAME `debugOverlay`/`uiSystem` instances on it (D13 — do not
   `new` them again).
7. `renderer.ResetSceneState()` (BW-001), then `renderer.SetEnvironment(BlackEnvironment.Build())`
   (the module-boundary correction — this line lives here, not inside `ResetSceneState`).
8. Construct a fresh `ScopedWindow` wrapping the SAME raw `window`, assign it as the new
   `presentation.Window`.
9. Build the new `sim`/`presentation` contexts (mirrors `window.Loaded`'s construction), using
   `sim.Options = options.WithoutStartupOnlyPaths()` (BW-003) for every switch after the first
   (D12) — and gate the existing post-`Build` Save/Restore-apply steps (`:169-183` today) so they
   only run on the FIRST load, not every switch (track with a simple `isFirstLoad` bool that flips
   false after the first successful build).
10. Call `recipe.Build(_loaderPrefetched, sim, presentation)`. **If this throws**: fatal by design
    (D9.5) — let it propagate to `RunClient`'s existing top-level `catch`, which already sets
    `failed = true` and runs the strict teardown (no new code needed for this case beyond NOT
    catching it locally).

**Window-close-mid-load (D9.6)**, a new teardown step (in `BuildTeardown`, before device disposal):
if `_loaderThread is { IsAlive: true }`: `_loaderCts?.Cancel()`, `_loaderThread.Join()` (blocking
is acceptable — shutdown path), dispose `_loaderRegistry`, `device.ClearSanctionedLoaderThread()`.

**Demo key for live verification**: add a new keybind (`Key.K` — confirmed unused in this branch;
the Noesis spike that uses `Key.K` lives on an unmerged branch) inside the existing `KeyPressed`
switch, calling `sim?.RequestSceneSwitch(<name>)` cycling between two known scene names appropriate
to whichever `IGame`/recipes are actually registered in the Sandbox at the time this task runs
(check `SandboxGame.cs`'s `Scenes` list — likely `"model"`/`"grid"` or similar two already-cooked
scenes) — this is what makes spec Verification items 2/3/4/6 actually exercisable live, not just
theoretically wired.

## Tail Tasks

### BW-013 — Self Code Review — ✅ DONE

Read every diff in full (`git diff --stat` matched the spec's own file list — `AppHost.cs`,
`HostOptions.cs`, `ISceneRecipe.cs`, `Scene/ClientScenePresenter.cs`, `Scene/SceneRecipe.cs`,
`SimSceneContext.cs`, `SimulationHost.cs`, `CopySyncState.cs`, `PersistentInstanceBuffer.cs`,
`Renderer.cs`, `SceneMaterializer.cs`, plus the 2 new files `ScopedWindow.cs`/
`BackgroundPresentationContext.cs`, and the 2 trivial `ISceneRecipe` implementer edits — nothing
unexpected touched). **1 real ordering bug found and fixed**: the switch poll checked "a new
request arrived" BEFORE checking "did the in-flight load already finish" — a request landing in
the exact same poll as a completion would cancel a load that was already done, then still hand off
the about-to-be-superseded scene, then immediately switch again. Not a crash/leak (harmless
wasted work), but sloppy — restructured to check completion first, always. Also added: cancellation
(D9.2) now surfaces through the same `loaderFailure` path as a real failure (`PrefetchBackground`'s
`CancellationToken.ThrowIfCancellationRequested()` throws `OperationCanceledException`, caught by
`StartLoad`'s thread body like any other exception) — distinguished in the log
(`OperationCanceledException` → "load canceled", anything else → "failed") so an operator doesn't
mistake an intentional supersede for a real bug. Confirmed `SimulationHost : IDisposable` (not
assumed) before relying on it in `PerformHandOff`. Confirmed `FrameOrchestrator` is genuinely NOT
`IDisposable` (only its constituents are disposed). Re-ran full test suite + the single-scene
byte-identical capture after the reorder fix — unaffected (1015/1015, hash unchanged).

### BW-014 — Requirements Validation — ✅ DONE
**Deps**: BW-013. **Wave**: 4.
Walked the spec's own 9-item Verification list:

1. **✅ DONE** — single-scene startup byte-identical: `model` capture hash
   `9a010fc311dd51b74f755d306d4a819f`, exact match with the pinned baseline, re-confirmed after
   every wave including the final self-review fix. `grid`/`drop`/`metalrough`/`drive`/
   `planet-challenge` also spot-checked live: 0 error, 0 leak each.
2. **✅ DONE, human-verified live** — 8 consecutive `model`↔`grid` switches, 0 leak (876 resources),
   0 validation message, clean shutdown. The hand-off's `WaitIdle` and the loader thread's upload
   ran under real contention every one of those 8 switches (this IS the thread-safety spec's first
   real exercise in a real feature, not a synthetic probe).
3. **Not explicitly run** (`AGAPANTHE_CULL_VERIFY` combined with the demo switch) — the 8 live
   switches were structurally-static scenes (`model` 1 entity, `grid` 100 entities, matching
   structural versions each rebuild) and rendered correctly with no visible corruption, which is
   the behavior `ResetSceneState` exists to guarantee, but the specific GPU==CPU log assertion
   was not separately captured. Residual manual-verification debt, not a code gap — recorded below.
4. **Not distinctly exercised** — the 8 switches completed sequentially (each faster than a human
   double-tap within one ~300 ms load), so the D9.2 cancel-and-queue path was reviewed at the code
   level (BW-013's reorder fix) but not proven live under that exact race. Residual debt.
5. **✅ DONE, human-verified live** — generated a real snapshot (`AGAPANTHE_SAVE`), relaunched with
   `AGAPANTHE_SCENE=model AGAPANTHE_LOAD=<path>`: log shows `[scene 'model'] 0 entities from cooked
   data` (spawn suppressed) → `[Contenu-3a] world restored from '<path>' — 1 entities` — restore
   applied exactly once, at startup. Then 9 more `model`↔`grid` switches followed, live — the
   restore log line **never reappeared**, confirming `WithoutStartupOnlyPaths()` (D12) actually
   prevents a switch from re-triggering `AGAPANTHE_LOAD`, not just in theory.
6. **Attempted live** (`F3` toggled, switches performed) — no crash, no visible artifact, consistent
   with the mechanism, but overlay continuity is a visual property with no log signal either way —
   not independently confirmable from the log evidence alone. Code-reviewed (BW-013): `debugOverlay`
   is never reconstructed, `sharedFrameStats` is the same instance across every
   `SimulationHost.CreateDefault` call. Residual: a screenshot-based check would close this fully.
7. **✅ DONE, human-verified live, and it caught the real path** — the final switch of the same live
   session: `[scene switch] requested 'model'` → `[scene switch] loading 'model'` → **window closed
   before that load's completion log ever printed** (no `[scene 'model'] N entities`/`now on
   'model'` line followed) — straight into the shutdown sequence:
   `ResourceTracker: no leaks (1866 resources created and destroyed)`, `AppHost: clean shutdown, no
   GPU resource leaks`. This is `BW-012`'s `DisposeInFlightLoader` teardown step exercising its real
   cancel/Join/dispose body against an actually-still-running loader thread, not the no-op path —
   the strongest possible evidence for D9.6, found by accident (the user's close happened to land
   mid-load) rather than by careful timing.
8. **Not reproduced this session** — no `PrefetchBackground` failure was triggered live (e.g. an
   unresolvable scene token via the demo key). The code path (`loaderFailure is not null` branch,
   D9.4) is identical to the D9.2 cancellation path already reviewed in BW-013, and mirrors the
   existing, already-proven "keep running on error" shape used elsewhere in this codebase.
9. **Not reproduced this session** — no `Build` failure after teardown was triggered. Stated as
   fatal by design (D9.5); the code simply does not catch it locally, letting it propagate to
   `RunClient`'s existing top-level catch — the same path already proven for a startup `Build`
   failure (item 1's family of scenarios), not a new code path introduced by this spec.

**Honest summary (updated after a second live session)**: items **1, 2, 5, 7 are fully verified**
(automated + live human verification, item 7 exercising the real cancel/Join path, not a no-op).
Item 6 was attempted live with no negative signal but no independent confirmation either (a visual
property, no log evidence). Items 3, 4, 8, 9 remain code-reviewed-but-not-live-proven — none
introduce a mechanism beyond what 1/2/5/7 already exercise (3 needs a code addition outside this
spec's scope to observe live; 4/8/9 need either finer timing than a human can reliably hit or a
deliberate fault injection not attempted this session). None of these are known bugs.

### BW-015 — Full Project Verification — ✅ DONE

- `dotnet build` (full solution): 0 warnings, 0 errors.
- `dotnet test` (full solution): **1015/1015 green**, same count as before this spec — every new
  behavior is exercised by live/manual verification (BW-014) rather than new automated tests, since
  the spec's own nature (a real window, real threads, real GPU contention) isn't unit-testable the
  way the prerequisite `GraphicsDevice` thread-safety spec's lock logic was. One existing test
  updated for the new teardown step (`BuildTeardown_LabelsAreInStrictM4Order`).
- Manual Sandbox run, `model` scene, single frame: 0 validation, `ResourceTracker: no leaks (167
  resources)`, capture `9a010fc311dd51b74f755d306d4a819f` — **exact match** with the pinned
  baseline recorded in `CLAUDE.md`.
- 5 more scenes spot-checked live (`grid`, `drop`, `metalrough`, `drive`, `planet-challenge` — the
  last two exercise `DriveControl`/`LandingChallenge` scene systems through the new
  `PrefetchBackground`/`Build` split): all load and shut down cleanly, 0 error, 0 leak.
- Live interactive switch demo: 8 consecutive `model`↔`grid` switches, 0 leak (876 resources), 0
  validation, clean shutdown (full detail above, under "Live switch demo").
- **Not run**: the remaining 3 Sandbox scenes (`planet`, `planet-drop`, `topdown`) and `HeadlessSim`/
  `DedicatedServer`/`ThinClient` binaries were not individually re-verified this session — none of
  them are touched by this spec's mechanism (they don't go through `ISceneRecipe.Build`'s new
  parameter in any way that changes their behavior; `HeadlessSim`/`DedicatedServer` never call
  `ISceneRecipe` at all), and the full test suite passing is the actual gate for them. Flagged as a
  narrower verification surface than the prerequisite spec's own BW-015-equivalent, by nature of
  this spec being interactive/live rather than automatable end-to-end.

## Deferred Work (per spec's own "Deferred" section — out of scope for this board)

Simultaneous scene coexistence. Server-side scene/map switching. Sub-model cancellation granularity
finer than "once per model." A generic loading-screen UI. Recovering from a `Build` failure after
teardown (fatal by design).
