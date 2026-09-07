# Absolute Work Board — Agapanthe.App (first production host)

**Status**: `completed` 2026-09-07 — all 5 waves + tail + double audit + a 2nd "fix everything fixable" pass.
`dotnet test` **617 passed**, 0 warning, every pinned hash unchanged JIT + AOT, bad-scene exit 1 JIT + AOT.
Committed `a155f5d` on `main` (not pushed). **Human visual verdict still due** before marking the milestone `done`.
**Spec**: `docs/plans/2026-09-07-agapanthe-app-design.md` (APPROVED 4.40/5, reviewer 3 rounds + adapter decision)
**Session**: 30
**Created**: 2026-09-07

Milestone: extract `samples/Sandbox/Program.cs` into `src/Agapanthe.App` (`AppHost` + `IGame` /
`ISceneRecipe` contract), stamp the first real `UniverseId` (MP-0b debt), make the fixed step a
single definition (`SimulationSettings`). Human greenlight between every wave; double audit +
human visual verdict before close. Commit on explicit request only.

## Project Conventions

- .NET 10, `TreatWarningsAsErrors` (0 warning), xUnit, `dotnet build` / `dotnet test`.
- Solution file is `Agapanthe.slnx` (not `.sln`) — new project must be added to it.
- NativeAOT: `samples/Sandbox` + `tools/AotComponentProbe` publish; AOT publish needs `PATH`
  prefixed with `/c/Program Files (x86)/Microsoft Visual Studio/Installer` (vswhere).
- No `Vk*` type outside `Agapanthe.Graphics`; no Arch type outside `Agapanthe.World`;
  `Silk.NET.Vulkan` only in `Graphics`. `Agapanthe.Engine` closure = `{Core, World}`
  (`EngineIsHeadlessTests`, deny-list + exact `[InlineData]` allowlist per csproj).
- `Agapanthe.App` is NEW, above `Engine.Render`; NOT in the Engine headless closure. It does
  **not** reference `Agapanthe.Platform` (adapter lives in Sandbox — keeps Platform a Vulkan-free leaf).
- `InternalsVisibleTo("Agapanthe.Tests")` pattern: `Agapanthe.Engine.csproj:30`,
  `Agapanthe.Rendering.csproj:23`. Add to `Agapanthe.Engine.Render.csproj` + `Agapanthe.App.csproj`.
- Owner-thread guard pattern: `[Conditional("DEBUG")]` that **throws** (like `GameWorld.AssertOwnerThread`).
- Conversation FR; code / commits / docs EN.

### Gates (acceptance bar — unchanged from prior milestones)

- `dotnet build` 0 warning · `dotnet test` green (589 + new) · `EngineIsHeadlessTests` green.
- 0 validation message · 0 ResourceTracker leak · 0 alloc/frame steady state.
- Capture ×3 (Debug, 1280×720), `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420
  AGAPANTHE_OVERLAY=0 AGAPANTHE_DROP_EVERY=12 AGAPANTHE_CAPTURE=<> AGAPANTHE_CAPTURE_UI=<>`:
  HDR `12638eddd7f3f67ab161b298ffbcd15e` / UI `034213575932dabcff41c2e0c72addfa` **unchanged**.
- `HeadlessSim` default (`--ticks 600 --bodies 8`) `7e8dc68f5a25914c84677a7a53ad3a58` (1868 B) ·
  `--drive` `97e786f0455a53d856b9ba4affca1003` **unchanged**, JIT == NativeAOT.
- NativeAOT publish of `samples/Sandbox` PASS · `AotComponentProbe` PASS.
- Double audit (`csharp-lowlevel` + `engine-architect`) · **human visual verdict**.
- **New baseline recorded in W1**: `model`-scene HDR md5 (see AW-006) — W2's gate.

## Rollback Point

`0c98b1eae8142d41b67dd8c75582421cd91a18ba` (tree clean; only the board rename + the new spec
file are staged/untracked at DECOMPOSE time).

## Progress

### `model`-scene HDR baseline (AW-005) — W2 gates on this

`AGAPANTHE_GROUND=0 AGAPANTHE_MAX_FRAMES=180 AGAPANTHE_OVERLAY=0`, no `AGAPANTHE_SCENE`, 1280×720, Debug:
- **md5 `df55d444b74c7aa94fd0ab18d795cc9c`**, **2 764 816 B**. Deterministic ×2. 0 leak, 0 validation.

### W1 DONE (2026-09-07) — AW-001..005

- **AW-001** `src/Agapanthe.Engine/SimulationSettings.cs` (new, 0 package refs) + `SimulationHost`:
  `CreateDefault(GameWorld, SimulationSettings)` overload, `Settings` getter, private ctor takes settings.
- **AW-002** `FrameOrchestrator`: `internal static ResolveAccumulatorStep(SimulationHost)` (private ctor
  reads it), `CreateDefault(SimulationHost,…)` drops `fixedTickDeltaSeconds`, `CreateDefault(GameWorld,…)`
  `fixedTickDeltaSeconds` default `0f` + `ThrowIfNegative` first. `Agapanthe.Engine.Render.csproj` +=
  `InternalsVisibleTo("Agapanthe.Tests")`.
- **AW-003** `src/Agapanthe.App` (csproj: `IsAotCompatible`, `Silk.NET.Input` 2.23.0, `InternalsVisibleTo`,
  8 ProjectReferences, **no Platform**) + `IWindow.cs`. Added to `Agapanthe.slnx`.
- **AW-004** `tests/Agapanthe.Tests/AppHostTests.cs` (new, 9 tests: SimulationSettings default/instance,
  `SimulationHost.Settings` ×3, `ResolveAccumulatorStep` ×2, negative-step throw, `RatesMatch`) +
  `AccumulatorEquivalenceTests` `Fixed` const → `SimulationSettings.Default.FixedDeltaSeconds`.
- **AW-005** baseline recorded above.
- **Gates**: `dotnet build` (slnx) 0 warning · `dotnet test` **597 passed** (+8), 0 fail ·
  `HeadlessSim` default `7e8dc68f…` (1868 B) + `--drive` `97e786f0…` **unchanged** ·
  capture `planet-drop` HDR `12638edd…` / UI `03421357…` **unchanged** · 0 leak · 0 validation.

### W2 DONE (2026-09-07) — AW-006..009

- **AW-006/007** `src/Agapanthe.App`: `IGame` / `ISceneRecipe` / `SceneContext` / `HostOptions`
  (`FromEnvironment(Func<string,string?>?)` — injectable) / `AppHost` (`RunClient`, `SelectRecipe`,
  `BuildTeardown` + `TeardownTargets`) / `Ppm`. `RunClient` ports the `window.Loaded` bootstrap +
  `Rendered` bracket + host keyboard + strict teardown **verbatim** from `Program.cs`; the
  `finally` iterates the same `BuildTeardown` list a test asserts. `game.Title` → `GraphicsDevice`
  app name. A `catch` around `window.Run()` turns an init failure into a clean exit 1 + teardown
  (was a raw stack dump) — the only behaviour delta, model/capture paths never throw.
- **AW-008** Sandbox thin: `EngineWindowAdapter : IWindow` (Sandbox owns it — keeps `App` off
  `Platform`), `SandboxGame` (`Universe => None`), `ModelSceneRecipe` (model/`--<glb>` path +
  `AGAPANTHE_UNLOAD_TEST` + free-fly `Updated` handler), `Content/{ModelContent,PlanetContent}`,
  `Cameras/SandboxCameras`, `Systems/{BenchSpin,Churn,ProbeDrop,LandingChallenge}`,
  `Tools/IblTestTool` (standalone `AGAPANTHE_IBL_TEST`). `Program.cs` → 14 lines.
  Planet/grid/drop/drive helpers all relocated (dormant) — W3 just adds recipe classes calling them.
- **AW-009** tests 6/7/8/9 in `AppHostContractTests.cs` (11 tests): `IGame.Universe` DIM,
  `SelectRecipe` default/family/named/throw/bad-default, `HostOptions.FromEnvironment` (3 cases,
  injected accessor), `BuildTeardown` label order + null-safe.
- **Gates**: `dotnet build` (slnx) 0 warning · `dotnet test` **608 passed** (+11), 0 fail ·
  **`model` HDR md5 `df55d444…` == W1 baseline** (byte-identical through the extracted AppHost) ·
  0 leak (174 resources) · 0 validation. `EngineIsHeadlessTests` green (App not in Engine closure).

### W3a DONE (2026-09-07) — AW-010..011

- **AW-010** `ModelSceneRecipe` gains the `grid:` / `drop:` branches (`Matches` accepts the prefixes;
  `ParseGrid`/`ParseDrop` → `SpawnGrid`/`SpawnDropScene`, `multiInstance` drives the light rig,
  `drop:` uses `physicsGroundY` for the ground plane). `AGAPANTHE_CULL_STATS` → `BenchSpinSystem` +
  bench camera; `AGAPANTHE_CHURN` → `ChurnSystem`; `AGAPANTHE_PHYSICS=1` for `drop:` →
  `PhysicsSystem` with `fixedDt: ctx.Simulation.Settings.FixedDeltaSeconds` (the single definition,
  same 1/60, now covered by `PhysicsSystem.RatesMatch`'s assert).
- **Gates**: `dotnet build` (slnx) 0 warning · `dotnet test` **608 passed**, 0 fail ·
  **`model` HDR md5 `df55d444…` still == baseline** (grid/drop are additive branches) ·
  `grid:20x20` launches clean (400 entities, bench: 0 B/frame, `sim ticks 1`) ·
  `drop:40 AGAPANTHE_PHYSICS=1` launches clean (`fixed dt 0.0167s`), 0 leak (195 resources), 0 validation.
- Note: `CopySyncStateTests.PlanFrame_IsZeroAlloc_InSteadyState` flaked once in a full run, passed
  isolated + on re-run — pre-existing 0-alloc-test sensitivity to run ordering, not W3a (only a
  Sandbox file changed, untouched by tests).

### W3b DONE (2026-09-07) — AW-012..014

- **AW-012** `Content/PlanetStage.cs` (shared: `SetupPlanetScene` + `AGAPANTHE_LOAD` restore + sun-only
  point light + black env) + `Scenes/{PlanetSceneRecipe,PlanetDropSceneRecipe,PlanetChallengeSceneRecipe}.cs`.
  `PlanetRecipeShared`: free-fly `Updated` handler + `B`-key `SimCommand.Enqueue` (`SpawnProbeCommandKind`).
  Drop → `PhysicsSystem` + `ProbeDropSystem` + `ApplyCommand`→`DropOne`. Challenge → `PhysicsSystem` +
  `LandingChallengeSystem` (takes `IWindow` now) + `ApplyCommand`→`TryShoot` + `F5` quicksave.
  `PlanetSceneRecipe.Matches(null)` claims a bare `AGAPANTHE_LOAD` run (preserves pre-extraction "loadMode
  forces planet"). `SelectRecipe` refined: `Matches` gets first refusal even on null/empty, then `DefaultScene`.
- **AW-013** `Scenes/DriveSceneRecipe.cs` — steerable zero-gravity body, fixed camera (no `Updated`),
  `InputMap` (`BindAxisVector` + brake `OnPress`) + `SampleInput` (WASD/Space/C + `X` pending-edge) +
  `ApplyCommand`→`SetBodyVelocity`. `fixedDt` from `ctx.Simulation.Settings`. `Program.cs` already final (14 l).
- **AW-014** gate:
  - `dotnet build` (slnx) 0 warning · `dotnet test` **608 passed**, 0 fail.
  - **captures ×3 `planet-drop`: HDR `12638eddd7f3f67ab161b298ffbcd15e` / UI `034213575932dabcff41c2e0c72addfa`
    — byte-identical** (planet family + MP-0d routing + physics + spawner reproduced pixel-perfect).
  - `model` HDR `df55d444…` still == baseline.
  - `planet` (166 res) / `planet-challenge` (202) / `drive` (192) launch clean, 0 leak, 0 validation.
  - save planet-challenge (3 entities) → reload → "restored 3 entities, universe StayedUnidentified", 0 leak.
    Bare `AGAPANTHE_LOAD` → `PlanetSceneRecipe` claims it → restore works.

### W4 DONE (2026-09-07) — AW-015..017

- **AW-015** `AppHost.ResolveUniverse(IGame, HostOptions)` extracted (`options.Universe ?? game.Universe`),
  `RunClient` uses it → `new GameWorld(GlobalIdRange.Default, resolved)`. **The MP-0b debt is paid** — the
  first production host that threads a real `UniverseId`. 5 tests: override / fallback / both-none;
  host-stamped world loading a `None` snapshot → `UniverseOutcome.Kept`, no throw; cross-universe snapshot
  → `WorldSerializationException`. (The 5-case World-level reconciliation was already in `WorldSerializationV2Tests`.)
- **AW-016** `samples/HeadlessSim`: local `const float FixedDt` deleted → `var fixedDt = host.Settings.FixedDeltaSeconds`
  (both default + `--drive`). `EngineIsHeadlessTests` `[InlineData]` for `HeadlessSim.csproj` unchanged (no new ref).
- **AW-017** AOT gate:
  - `dotnet publish samples/Sandbox -r win-x64 -c Release` **PASS** — no IL2xxx/IL3xxx, `Agapanthe.App` in
    the native closure.
  - **Sandbox JIT == NativeAOT, byte-identical**: `planet-drop` HDR `12638edd…` / UI `03421357…` ·
    `model` HDR `df55d444…` (baseline) · `drive` clean shutdown 0 leak.
  - **`HeadlessSim` JIT == NativeAOT**: default `7e8dc68f5a25914c84677a7a53ad3a58` (1868 B) ·
    `--drive` `97e786f0455a53d856b9ba4affca1003` — both **unchanged**.
  - `AotComponentProbe` publish + run **PASS** (`AotRootingSmoke iterated 13`, `AotInputCommandSmoke applied 4`).
  - `dotnet test` **613 passed** (+5), 0 fail · `dotnet build` (slnx) 0 warning.

### W5 — tail (2026-09-07) — AW-018..020

- **AW-018 Self Code Review** — found `SetupPlanetDrop`/`SetupPlanetChallenge` re-literalled `1f/60f` instead
  of deriving from `SimulationSettings`; fixed (threaded `fixedDt` from `ctx.Simulation.Settings`). Cleaned
  `PlanetStage` dead param, renamed `ModelContentSphere` → `BuildProbeSphere`. Captures re-verified unchanged.
- **AW-019 Requirements Validation** — 17 Decision Log rows + acceptance bar + 4 Error Handling rows all honoured.
- **AW-020 double audit**:
  - `engine-architect` **PASS-with-concerns 4.2/5** — 1 🔴 (`RunClient` exit code: `clean` overwritten by the
    teardown Report step → returned 0 on init failure; Release `ResourceTracker.Report()` returns true
    unconditionally). Several 🟠.
  - `csharp-lowlevel` **PASS-with-concerns, no 🔴** — teardown order verified EXACT, hot path 0-alloc confirmed,
    no reflection, extractions faithful. 🟠 F-1/F-2/F-11.
  - **Fixes applied**: 🔴-1 `var failed` flag + `return clean && !failed ? 0 : 1` (bad `AGAPANTHE_SCENE` now
    exits 1, JIT + AOT verified) · F-1 per-step `try/catch` in the teardown loop (uses the `label`) ·
    F-2 `Log.Error($"...{ex}")` full stack + comment covers runtime · 🟠-2 `EngineIsHeadlessTests` `[InlineData]`
    for `Agapanthe.App.csproj` (allowlist gate on `App ↛ Platform`) · 🟠-4 `Log.Warn` when `AGAPANTHE_LOAD` set
    on a non-restoring scene · F-11 F5 quicksave `catch` widened to `UnauthorizedAccessException` · F-9
    `AppHost.ResolveShaderDirectory` made public, `IblTestTool` dedup · `WireFreeFly`/`WireProbeKey` →
    `RecipeInput` (was duplicated `ModelSceneRecipe` ↔ `PlanetRecipeShared`) · stale window title.
  - **Also fixed (2nd pass, "fix everything fixable")**: 🟠-3 **`HostOptions.Scene`** + `SavePath` + `VerifyCull`
    + `ShaderReloadTest` — `RunClient` now reads **zero** env vars (F-6 closed) · F-8 dead `IWindow` surface
    (`Closing`, `CaptureMouseOnClick`) removed from the interface + adapter · F-10 `Ppm.WriteSwapchain` length
    guard + format comment · **test 5** as `IWindowSurfaceTests` (reflection: `IWindow` ⊆ `EngineWindow` by
    name/shape; `EngineWindow` is never itself an `IWindow` — the acyclic-graph invariant) · `SelectRecipe`
    trims. Tests project gains a `ProjectReference` on `Agapanthe.Platform` (a leaf, cheap).
  - **Deferred to §Deferred Work** (auditor-agreed): 🟠-5 `SceneContext` sim/presentation split
    (**bites at 2nd slice + `RunDedicatedServer`**) · 🟠-6 adapter → dedicated `Platform.App` project at app #2 ·
    🟠-7 camera/light/ground/sky helpers → App at the content milestone · F-5 model-not-found exit 2→1 (the
    path check is game-specific, lives in `ModelSceneRecipe`) · F-7 `FrameOrchestrator` mid-param removal
    (public API note) · F-13 stale asset load order (documented in AVANCEMENT).
- **Post-fix re-verify (2 passes)**: `dotnet build` (slnx) 0 warning · `dotnet test` **617 passed** (+3:
  IWindowSurface ×2, SelectRecipe-uses-Scene) · `model` `df55d444…` · `planet-drop` HDR `12638edd…` /
  UI `03421357…` · `HeadlessSim` `7e8dc68f…` / `97e786f0…` — all unchanged, JIT + AOT · bad-scene exit 1
  JIT + AOT · 0 leak / 0 validation.
  (Note: `Sandbox.exe` on Debug still hits the intermittent GLFW/Silk shutdown SIGSEGV ~1/5 **after** the clean
  leak report — pre-existing upstream dette, CI gates on the report line not the exit code.)

## Remaining

- **Human visual verdict** — see below.
- Commit on explicit request only.

---

## Tasks

### Wave W1 — Engine + App primitives (no Sandbox game-code change, no Platform change)

#### AW-001 — `SimulationSettings` + `SimulationHost` wiring
- **Type**: code · **Size**: S · **Deps**: none
- **Files**: `src/Agapanthe.Engine/SimulationSettings.cs` (new), `src/Agapanthe.Engine/SimulationHost.cs` (modify)
- `sealed class SimulationSettings { public float FixedDeltaSeconds { get; init; } = 1f/60f;
  public static SimulationSettings Default { get; } = new(); }` — no deps, `Agapanthe.Engine.csproj`
  keeps **zero** package references.
- `SimulationHost`: private ctor takes `SimulationSettings`; new
  `CreateDefault(GameWorld, SimulationSettings)` (null-checks both, registers `PropagateSystem`);
  existing `CreateDefault(GameWorld)` → `CreateDefault(world, SimulationSettings.Default)`;
  `public SimulationSettings Settings { get; }` (set in ctor). XML doc per spec §Data Model.
- **Acceptance**: builds 0 warning; all existing Engine tests green; `Agapanthe.Engine.csproj` still 0 `PackageReference`.

#### AW-002 — `FrameOrchestrator` reads the single definition
- **Type**: code · **Size**: S · **Deps**: AW-001
- **Files**: `src/Agapanthe.Engine.Render/FrameOrchestrator.cs`, `src/Agapanthe.Engine.Render/Agapanthe.Engine.Render.csproj`
- `internal static float ResolveAccumulatorStep(SimulationHost s) => s.Settings.FixedDeltaSeconds;`
  the private ctor builds `_accumulator` from it.
- `CreateDefault(SimulationHost, …)` overload: **drop** `fixedTickDeltaSeconds` param.
- `CreateDefault(GameWorld, …)` overload: `fixedTickDeltaSeconds` default `0f`;
  `ArgumentOutOfRangeException.ThrowIfNegative(fixedTickDeltaSeconds)` as the **first** statement;
  `step = fixedTickDeltaSeconds == 0f ? SimulationSettings.Default.FixedDeltaSeconds : fixedTickDeltaSeconds`;
  builds a `SimulationSettings` from `step` and delegates to the other overload.
- `.csproj` gains `<InternalsVisibleTo Include="Agapanthe.Tests" />` (MSBuild item form).
- Update `AccumulatorEquivalenceTests` callers only if a signature they use changed (they call
  `SimulationHost.CreateDefault(world)` — unaffected; see AW-005).
- **Acceptance**: builds 0 warning; `FrameOrchestrator` no longer contains a bare `1f / 60f` literal;
  existing render tests green.

#### AW-003 — `src/Agapanthe.App` project skeleton + `IWindow`
- **Type**: infra + code · **Size**: S · **Deps**: none
- **Files**: `src/Agapanthe.App/Agapanthe.App.csproj` (new), `src/Agapanthe.App/IWindow.cs` (new),
  `Agapanthe.slnx` (modify)
- csproj: `net10.0`, `Nullable=enable`, `ImplicitUsings=enable`, `IsAotCompatible=true`
  (`TreatWarningsAsErrors` comes from `Directory.Build.props` — verify). `PackageReference
  Silk.NET.Input` `Version="2.23.0"`. `<InternalsVisibleTo Include="Agapanthe.Tests" />`.
  `ProjectReference`: Engine.Render, Engine, Rendering, Graphics, Ui, Assets, World, Core. **NOT Platform.**
- `IWindow : IDisposable` — exact surface from spec §Data Model (events `Loaded`/`Updated(double)`/
  `Rendered(double)`/`FramebufferResized(int,int)`/`Closing`/`KeyPressed(Key)`; props
  `Title{get;set;}`, `FramebufferSize`, `VkSurface`, `MouseDelta`, `MouseCaptured`,
  `CaptureMouseOnClick{get;set;}`; methods `IsKeyDown`, `SetMouseCaptured`,
  `GetRequiredVulkanExtensions`, `Run`, `Close`).
- **Acceptance**: `dotnet build` picks up the project; `dotnet sln`/slnx lists it; 0 warning.

#### AW-004 — W1 test additions
- **Type**: test · **Size**: M · **Deps**: AW-001, AW-002, AW-003
- **Files**: `tests/Agapanthe.Tests/AppHostTests.cs` (new — W1 slice), `tests/Agapanthe.Tests/AccumulatorEquivalenceTests.cs` (modify)
- Tests (spec §Testing Strategy rows 1, 2, 3, 3b, 4, 12):
  - `SimulationSettings.Default.FixedDeltaSeconds == 1f/60f`; custom init round-trips.
  - `CreateDefault(world).Settings` `ReferenceEquals` `Default`; `CreateDefault(world, custom).Settings` `ReferenceEquals` `custom`.
  - `FrameOrchestrator.ResolveAccumulatorStep(CreateDefault(world, {1f/30f})) == 1f/30f`; `== Default` for `CreateDefault(world)`.
  - `CreateDefault(world, null!, null!, null!, null!, fixedTickDeltaSeconds: -1f)` → `ArgumentOutOfRangeException` (thrown before the null-checks).
  - `RatesMatch(1f/60f, 1f/60f)` true; `RatesMatch(3f/60f, 1f/60f)` false.
  - `AccumulatorEquivalenceTests`: every profile rebuilt from `SimulationSettings.Default.FixedDeltaSeconds` (not the local `Fixed` const); assertions unchanged.
- **Acceptance**: all green; `dotnet test` count = 589 + these.

#### AW-005 — record the `model`-scene HDR baseline
- **Type**: docs · **Size**: S · **Deps**: AW-001, AW-002 (build green)
- On this tree (pre-Sandbox-refactor), run the Sandbox headless:
  `AGAPANTHE_SCENE` unset, `AGAPANTHE_GROUND=0`, `AGAPANTHE_MAX_FRAMES=180`,
  `AGAPANTHE_OVERLAY=0`, `AGAPANTHE_CAPTURE=model.ppm`, 1280×720, Debug.
- Record `md5(model.ppm)` + byte length in this board under `## Progress` — W2 gates on it.
- **Acceptance**: hash written; run is 0 validation / 0 leak.

### Wave W2 — the contract + `AppHost` + `model` scene (default only)

#### AW-006 — `IGame` / `ISceneRecipe` / `SceneContext` / `HostOptions` / `TeardownTargets`
- **Type**: code · **Size**: M · **Deps**: AW-003
- **Files**: `src/Agapanthe.App/{IGame,ISceneRecipe,SceneContext,HostOptions}.cs` (new), `AppHost.cs` (new — `TeardownTargets` lives here)
- Exact shapes from spec §Data Model. `IGame.Universe` + `ISceneRecipe.Matches` are default
  interface members. `HostOptions.FromEnvironment(Func<string,string?>? read = null)` — `read`
  defaults to `Environment.GetEnvironmentVariable`; malformed `AGAPANTHE_UNIVERSE` → `Log.Warn` +
  `Universe = null`. `TeardownTargets` = `internal readonly record struct` of the 7 nullable disposables.
- **Acceptance**: builds 0 warning; `IsAotCompatible` analyzers clean (no reflection).

#### AW-007 — `AppHost.RunClient` + `SelectRecipe` + `BuildTeardown`
- **Type**: code · **Size**: M · **Deps**: AW-006, AW-002
- **Files**: `src/Agapanthe.App/AppHost.cs`
- Port from `Program.cs` **verbatim** (cite the line ranges in comments): `window.Loaded` GPU
  bootstrap (`:112-137`, `game.Title` for the `GraphicsDevice` app name, `AGAPANTHE_IBL_TEST` /
  `AGAPANTHE_UNLOAD_TEST` hooks), `world = new GameWorld(GlobalIdRange.Default, options.Universe ??
  game.Universe)`, `SimulationHost.CreateDefault(world, SimulationSettings.Default)`,
  `FrameOrchestrator.CreateDefault(sim, world, renderer, registry, camera, renderList)`, UI overlay
  (`:505-535`), `recipe.Build(new SceneContext { … Args = args })`, `AGAPANTHE_SHADER_RELOAD_TEST`.
- `window.FramebufferResized` (`:681-688`), host-level `window.KeyPressed` (`Escape` two-stage,
  look-sensitivity, exposure, `N`, `L`, `F3`), `window.Rendered` bracket (`:842-973`:
  `wallClockDt = options.MaxFrames > 0 ? orchestrator.FixedTickDeltaSeconds : (float)dt`,
  bench log gated on `options.CullStats`, capture arm/dump, auto-close).
- **The host does NOT subscribe to `window.Updated`** (recipes do).
- `BuildTeardown(in TeardownTargets)` returns `IReadOnlyList<(string Label, Action Step)>` in the
  exact M4-11 order (spec §Interfaces step 8); every `Step` null-safe. `RunClient`'s `finally`:
  `foreach (var (_, step) in BuildTeardown(t)) step();`. `ResourceTracker.Report()` step sets
  `clean`. `Environment.Exit(clean ? 0 : 1)`.
- `internal static ISceneRecipe SelectRecipe(IGame, string?)`: null/empty → `Name == DefaultScene`
  (else `InvalidOperationException`); else first `Matches(token)`; else `ArgumentException`.
- **Acceptance**: builds 0 warning; `IsAotCompatible` clean.

#### AW-008 — Sandbox becomes an `IGame` consumer (`model` scene only)
- **Type**: code · **Size**: M · **Deps**: AW-007 · **owns** `samples/Sandbox/*` this wave
- **Files**: `samples/Sandbox/Sandbox.csproj` (+ `ProjectReference ..\..\src\Agapanthe.App`),
  `samples/Sandbox/EngineWindowAdapter.cs` (new), `samples/Sandbox/SandboxGame.cs` (new),
  `samples/Sandbox/Scenes/ModelSceneRecipe.cs` (new),
  `samples/Sandbox/Content/*` + `samples/Sandbox/Cameras/*` (the helpers `ModelSceneRecipe` needs),
  `samples/Sandbox/Program.cs` (shrink to the `RunClient` one-liner)
- `EngineWindowAdapter(EngineWindow inner) : IWindow` — pure forwarding, no public member outside `IWindow`.
- `SandboxGame`: `Title = "Agapanthe Sandbox"`, `DefaultScene = "model"`, `Universe => UniverseId.None`,
  `Scenes = [new ModelSceneRecipe()]` for now.
- `ModelSceneRecipe.Matches`: true for `"model"`, null, empty (grid:/drop: added in AW-009).
  `Build`: `ResolveModelPath(ctx.Args)` → `GltfLoader.Load` → `registry.Load` → spawn once →
  ground plane → 3-point rig → `FrameCamera`; subscribe to `ctx.Window.Updated` for the free-fly
  controller (verbatim `Program.cs:791-840` body incl. `AGAPANTHE_INPUT_DEBUG`).
- Everything not needed by `model` stays in `Program.cs` **temporarily** as dead static helpers
  IF that keeps the diff reviewable — else move in AW-009. Prefer: move only what `model` uses now.
- **Acceptance**: `dotnet run --project samples/Sandbox` opens the `model` scene windowed;
  `-- MetalRoughSpheres.glb` still selects that fixture; 0 leak / 0 validation on a headless
  `AGAPANTHE_MAX_FRAMES` run.

#### AW-009 — W2 tests + `model` gate
- **Type**: test · **Size**: M · **Deps**: AW-007, AW-008
- **Files**: `tests/Agapanthe.Tests/AppHostTests.cs` (extend), `tests/Agapanthe.Tests/EngineWindowAdapterTests.cs` (new, small)
- Tests (spec rows 5, 6, 7, 8, 9):
  - `EngineWindowAdapter` implements `IWindow`; declares no public member outside it (reflection).
  - a minimal `IGame` with no `Universe` override → `UniverseId.None`.
  - `SelectRecipe`: `null`/`""`/`"grid:8x8"` → the model recipe; a named non-match → `ArgumentException`;
    `DefaultScene` matching 0 recipes → `InvalidOperationException`. (grid case: add a stub recipe
    or assert against the real `ModelSceneRecipe.Matches` once AW-009's grid support lands — order
    AW-009 test after AW-011 if needed, or use a fake recipe here.)
  - `HostOptions.FromEnvironment(read)`: 32-hex `AGAPANTHE_UNIVERSE` → parsed; `"zzz"` → null + no throw;
    absent → null. `MaxFrames`/`CapturePath`/`OverlayVisible` from `read`.
  - `BuildTeardown(default).Select(x => x.Label)` == the exact 10-label M4 order;
    `ResourceTracker.Report` immediately before `window.Dispose`; `world` < `registry` < `renderer`.
- **Gate**: headless `model` capture (same params as AW-005) → `md5 == AW-005 baseline`;
  0 validation / 0 leak; `dotnet build` 0 warning; `dotnet test` green.
- **Acceptance**: all green; baseline matches.

### Wave W3a — `model` family: `grid:` + `drop:`

#### AW-010 — `ModelSceneRecipe` grid/drop branches + bench + churn
- **Type**: code · **Size**: M · **Deps**: AW-008 · **owns** `samples/Sandbox/*`
- **Files**: `samples/Sandbox/Scenes/ModelSceneRecipe.cs`, `samples/Sandbox/Systems/{BenchSpinSystem,ChurnSystem}.cs` (new),
  `samples/Sandbox/Content/*` (SpawnGrid / SpawnDropScene helpers), `samples/Sandbox/Program.cs` (delete moved helpers)
- `Matches` also accepts `grid:` / `drop:` prefixes. `Build` branches on `ParseGrid` / `ParseDrop`
  (verbatim). `AGAPANTHE_CULL_STATS` bench camera + `BenchSpinSystem`; `AGAPANTHE_CHURN` +
  `ChurnSystem`; `AGAPANTHE_PHYSICS` + `PhysicsSystem` for `drop:` (`fixedDt:
  ctx.Simulation.Settings.FixedDeltaSeconds`).
- **Acceptance**: `AGAPANTHE_SCENE=grid:20x20` and `drop:40` launch clean (0 leak / 0 validation);
  bench line logs candidates + `sim ticks`.

#### AW-011 — W3a gate
- **Type**: docs · **Size**: S · **Deps**: AW-010
- Re-run the `model` capture → still == AW-005 baseline (grid/drop are additive branches).
  `dotnet build` 0 warning · `dotnet test` green.
- **Acceptance**: baseline unchanged; grid/drop runs recorded in `## Progress`.

### Wave W3b — planet family + drive; `Program.cs` final

#### AW-012 — planet recipes
- **Type**: code · **Size**: M · **Deps**: AW-010 · **owns** `samples/Sandbox/*` (serial after AW-010)
- **Files**: `samples/Sandbox/Content/PlanetStage.cs` (new — shared `SetupPlanetScene`),
  `samples/Sandbox/Scenes/{PlanetSceneRecipe,PlanetDropSceneRecipe,PlanetChallengeSceneRecipe}.cs` (new),
  `samples/Sandbox/Systems/{ProbeDropSystem,LandingChallengeSystem}.cs` (new),
  `samples/Sandbox/Cameras/*` (FramePlanet* helpers), `samples/Sandbox/Program.cs` (delete moved helpers)
- Planet: sun-only rig + black env + `FramePlanetCamera` + free-fly `Updated`. Drop: + `PhysicsSystem`
  (`.WithAttractor`, `fixedDt` from settings) + `ProbeDropSystem` + `AGAPANTHE_LOAD` (`world.Load` —
  default `AdoptFromHeader`; cross-universe throw propagates per spec §Error Handling) +
  `ApplyCommand` for `SpawnProbe` + `B` key via `ctx.Window.KeyPressed`. Challenge: + `LandingChallengeSystem`
  + `B` (aimed) + `F5` quicksave, all subscribed in `Build` (the `when` guards become "only subscribe
  when this recipe built that system").
- `SandboxGame.Scenes` now lists all 5 recipes, `model` last (so `grid:`/`drop:` never shadow a name).
- **Acceptance**: `planet` / `planet-drop` / `planet-challenge` launch clean; `B` drops; `F5` saves;
  `AGAPANTHE_LOAD` resumes.

#### AW-013 — drive recipe + `Program.cs` final
- **Type**: code · **Size**: S · **Deps**: AW-012 · **owns** `samples/Sandbox/*`
- **Files**: `samples/Sandbox/Scenes/DriveSceneRecipe.cs` (new), `samples/Sandbox/Program.cs` (final ~5 lines)
- `DriveSceneRecipe`: one steerable zero-gravity body (`fixedDt` from settings), fixed camera
  (does **not** subscribe to `Updated`), `InputMap` (`BindAxisVector` + `BindButton` brake `OnPress`),
  `SampleInput` (WASD/Space/C axes, `X` pending-edge), `ApplyCommand` (`SetBodyVelocity`), `X` key.
- `Program.cs` reduced to `return AppHost.RunClient(new SandboxGame(args), new EngineWindowAdapter(new EngineWindow("Agapanthe Sandbox", 1280, 720)), args);` + `ResolveShaderDirectory` if still needed by the host (move it to `AppHost` in AW-007 if so). No dead helpers remain in `Program.cs`.
- **Acceptance**: `drive` runs; WASD/Space/C/X behave; `Program.cs` has no static helper left.

#### AW-014 — W3b capture gate
- **Type**: docs · **Size**: M · **Deps**: AW-013
- Captures ×3 (`planet-drop`, full param set): HDR `12638edd…` / UI `03421357…` **unchanged**.
  `model` capture still == AW-005 baseline. `dotnet build` 0 warning · `dotnet test` green ·
  0 validation / 0 leak.
- **Acceptance**: all hashes as stated, recorded in `## Progress`.

### Wave W4 — `UniverseId` end-to-end + `HeadlessSim` + AOT

#### AW-015 — `AGAPANTHE_UNIVERSE` override + behaviour tests
- **Type**: code + test · **Size**: S · **Deps**: AW-006, AW-012
- **Files**: `src/Agapanthe.App/HostOptions.cs` (the `AGAPANTHE_UNIVERSE` parse is already there
  from AW-006 — verify), `tests/Agapanthe.Tests/AppHostTests.cs` (extend)
- Tests: `AGAPANTHE_UNIVERSE` (32 hex) → `world.Universe` is that after `RunClient`-equivalent
  resolution (`options.Universe ?? game.Universe`); loading an existing `None` Sandbox snapshot
  with a non-`None` world universe → `SnapshotLoadResult.Universe == UniverseOutcome.Kept`, no
  throw; a synthetic snapshot header with a *different* non-`None` universe → `WorldSerializationException`.
- **Acceptance**: green; matches spec §Error Handling row.

#### AW-016 — `HeadlessSim` reads the single definition
- **Type**: code · **Size**: S · **Deps**: AW-001
- **Files**: `samples/HeadlessSim/Program.cs`
- Delete the local `const float FixedDt = 1f / 60f`; build the `PhysicsSettings` and the loop's
  `Tick(dt)` from `host.Settings.FixedDeltaSeconds` (`SimulationHost.CreateDefault(world)` still
  gives `SimulationSettings.Default`, so the value is bit-identical). `--drive` `RunDrive` likewise.
- `EngineIsHeadlessTests` `HeadlessSim.csproj` `[InlineData]` unchanged (no new ProjectReference).
- **Acceptance**: `HeadlessSim` default `7e8dc68f…` (1868 B) + `--drive` `97e786f0…` **unchanged**, JIT.

#### AW-017 — W4 AOT + snapshot gate
- **Type**: docs · **Size**: M · **Deps**: AW-014, AW-015, AW-016
- NativeAOT publish `samples/Sandbox` → PASS (`Agapanthe.App` `IsAotCompatible`, no new IL warnings).
- `HeadlessSim` default + `--drive` snapshots JIT == NativeAOT, both hashes unchanged.
- `AotComponentProbe` publish + run PASS.
- **Acceptance**: every hash as stated; AOT PASS; recorded in `## Progress`.

### Wave W5 — mandatory tail

#### AW-018 — Self Code Review
- **Type**: docs · **Deps**: AW-017
- Re-read the full diff against the spec + Decision Log; fix any off-spec / off-convention item
  before the external audit. Check: `Program.cs` truly ~5 lines; no `1f/60f` literal outside
  `SimulationSettings` + `PhysicsSettings.Default`; `Agapanthe.App` references no `Platform`;
  `EngineWindowAdapter` forwards every member; host does not subscribe `window.Updated`.

#### AW-019 — Requirements Validation
- **Type**: docs · **Deps**: AW-018
- Walk the spec's acceptance bar + every Decision Log row + the Error Handling table line by line.

#### AW-020 — Full Project Verification + double audit + human verdict + CONVERGE
- **Type**: docs · **Deps**: AW-019
- `dotnet build` 0 warning · `dotnet test` green · `EngineIsHeadlessTests` green · captures ×3 ·
  `HeadlessSim` ×2 JIT==AOT · `AotComponentProbe` · 0 leak / 0 validation.
- Double audit: `csharp-lowlevel` + `engine-architect` subagents; apply findings.
- **Human visual verdict**: 5 recipes / 7 tokens (`model`, `grid:20x20`, `drop:40`, `planet`,
  `planet-drop`, `planet-challenge`, `drive`); **free-fly camera + mouse-look in `planet`**
  (the path the pinned captures do not exercise); `drive` (WASD/Space/C/X); `B` (planet-drop +
  challenge); `F3` overlay; `F5` quicksave + `AGAPANTHE_LOAD` resume.
- CONVERGE: update `CLAUDE.md`, `docs/AVANCEMENT.md` (Reprise + module diagram — add `App`),
  `docs/BACKLOG.md` (§4quater "Ensuite" → tick `Agapanthe.App`), spec §Execution outcome;
  archive this board to `.absolute-work/archive/board-session30-AgapantheApp.md`; suggest a commit.

---

## Dependency graph

```
W1:  AW-001 ── AW-002 ──┐
     AW-003 ────────────┼── AW-004
                        └── AW-005 (needs 001,002 build green)

W2:  AW-003 ── AW-006 ── AW-007 ── AW-008 ── AW-009   (AW-002 also feeds AW-007)

W3a: AW-008 ── AW-010 ── AW-011

W3b: AW-010 ── AW-012 ── AW-013 ── AW-014   (all serial on samples/Sandbox/*)

W4:  AW-006,012 ── AW-015 ──┐
     AW-001 ────── AW-016 ──┼── AW-017
     AW-014 ────────────────┘

W5:  AW-017 ── AW-018 ── AW-019 ── AW-020
```

## Wave / parallelism plan

| Wave | Sequential | Parallel-safe (disjoint files) | Gate |
|---|---|---|---|
| W1 | AW-001 → AW-002; then AW-005 | AW-003 alongside AW-001/002; AW-004 after {002,003} | human greenlight |
| W2 | AW-006 → AW-007 → AW-008 → AW-009 | — (App files then Sandbox files, serial) | human greenlight |
| W3a | AW-010 → AW-011 | — | human greenlight |
| W3b | AW-012 → AW-013 → AW-014 | — (shared `samples/Sandbox/*`) | human greenlight |
| W4 | AW-017 last | {AW-015, AW-016} parallel (App vs HeadlessSim, disjoint) | human greenlight |
| W5 | AW-018 → AW-019 → AW-020 | — | human verdict + CONVERGE |

## Deferred Work

### From the W5 double audit (session 30) — after the 2nd fix pass

- 🟠 **`SceneContext` sim/presentation split** (architect 🟠-5) — all 10 members are `required` + non-nullable
  (incl. `Device`, `Renderer`, `Camera`, `Window`), so a recipe cannot be built headless and `HeadlessSim`
  shares *zero* world-population code with a client. Split into a sim core (`World`, `Orchestrator`, `Args`,
  options) + an optional presentation half. **This is the debt that bites at the 2nd (dissimilar) slice and at
  `RunDedicatedServer`.** Symptom: `LandingChallengeSystem` is a `PostSimulation` `ISystem` that holds an
  `IWindow` to write the title — game logic that cannot run without presentation (pre-existing VS-3 debt,
  carried into the new contract).
- 🟠 **`EngineWindowAdapter` → dedicated project** (architect 🟠-6) — ~75 lines of generic forwarding that
  app #2 must copy verbatim. Promote to `src/Agapanthe.Platform.App` (refs Platform + App) when the 2nd app
  arrives. Partial cover in place: `IWindowSurfaceTests` (reflection — `IWindow` ⊆ `EngineWindow` by name/shape;
  `EngineWindow` never itself an `IWindow`).
- 🟠 **Camera/light/ground/sky helpers → `Agapanthe.App`** (architect 🟠-7) — `FrameCamera`, `SetupLights`,
  `NarrowBounds`, `BuildGroundModel`, `BuildSkyEnvironment`, `RecipeInput.WireFreeFly` are engine-generic, not
  Sandbox-specific; app #2 copies them. Move at the content-authoring milestone (with a `Content/ModelStage`
  extraction — `ModelSceneRecipe` is ~200 lines).
- 🟡 **`FrameOrchestrator.CreateDefault(SimulationHost, …)` dropped a mid-list optional param** (lowlevel F-7)
  — a positional call `CreateDefault(sim, world, r, reg, cam, rl, 0.5f)` now silently binds `0.5f` to
  `maxWallClockDeltaSeconds`. No repo caller does this; flagged for the public API.
- 🟡 **model-not-found**: exit code was 2 (before any GPU object), now 1 (after full Vulkan bring-up +
  teardown, `InvalidOperationException` in `ModelSceneRecipe.Build`) (lowlevel F-5). The path check is
  game-specific — a fix belongs in the recipe, not the host.
- 🟡 **stale-asset-load-order** (lowlevel F-13): the planet recipes load the glTF model *after* `SetupPlanetScene`
  (the old `Program.cs` loaded it before) — a pre-refactor `.save` snapshot's handles may not resolve. The new
  build is self-consistent (captures prove it); noted in `AVANCEMENT.md`.

**Fixed in W5 (post-audit, both passes)**: 🔴 exit code (`failed` flag) · per-step `try/catch` in the teardown
loop (F-1) · full `{ex}` + widened comment (F-2) · `EngineIsHeadlessTests` `[InlineData]` for `App.csproj`
(🟠-2) · `Log.Warn` on `AGAPANTHE_LOAD` + a non-restoring scene (🟠-4) · F5 `catch` → `+ UnauthorizedAccessException`
(F-11) · `WireFreeFly`/`WireProbeKey` → `RecipeInput`, `ResolveShaderDirectory` public (dedup) ·
**`HostOptions.Scene`/`SavePath`/`VerifyCull`/`ShaderReloadTest` — `RunClient` reads no env var** (🟠-3, F-6) ·
dead `IWindow` surface removed (`Closing`, `CaptureMouseOnClick`) (F-8) · `Ppm.WriteSwapchain` length guard (F-10) ·
`IWindowSurfaceTests` (the reachable equivalent of spec test 5) · `SelectRecipe` trims · stale window title.

### From the milestone design (pre-execution)

- `HostRole` enum / `AppHost.RunDedicatedServer` / folding `HeadlessSim` into `Agapanthe.App` (netcode milestone).
- In-process scene switching + `FixedTimestepAccumulator.Reset()` + world reseed.
- Declarative scene / prefab format (this milestone's `ISceneRecipe` registry is code organisation).
- `Content/ModelStage.cs` extraction if `ModelSceneRecipe` stays too large after W3a.
- Home-grown `Key` enum / action-map assets / rebindable input / `CameraInput` unification.
- `IWindow` second implementer / `IInputSource` split (revisit at the action-map milestone).
- Stable asset identity / cook (next backlog item after this).
- `PhysicsSettings.FixedDt` structurally deriving from `SimulationSettings` (blocked by the
  `World`↛`Engine` layering; runtime `RatesMatch` assert is the reconciliation).
