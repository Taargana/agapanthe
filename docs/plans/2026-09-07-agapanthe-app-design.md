# Agapanthe.App — first production host — Design Spec

> Engine-cap milestone (backlog §4quater, "Ensuite, dans l'ordre" → first item). Follows MP-0
> (closed 4/4). Interview: session 30, 2026-09-07. Spec rev 4 (reviewer rounds 1–3 applied; adapter decision).

## Summary

Extract `samples/Sandbox/Program.cs` (2360 lines of GPU bootstrap, strict-order teardown,
frame loop, capture/env harness, 5 scene families, camera framing, light rigs, input and
gameplay) into a reusable **`src/Agapanthe.App`** library: an `AppHost` that owns the client
frame loop and a two-level game contract (`IGame` + `ISceneRecipe`). This is where the first
real `UniverseId` is threaded into `GameWorld` (MP-0b debt), where the composition-root seam
born in MP-0a is formalised, and where the fixed timestep becomes a single definition
(`SimulationSettings`, netcode prerequisite). An `IWindow` abstraction is introduced so the host
no longer names the concrete `EngineWindow`.

## Context

### What exists today

- **`samples/Sandbox/Program.cs`** — top-level statements. `window.Loaded` builds
  `GraphicsDevice` / `Swapchain` / `Renderer` / `ResourceRegistry` / `GameWorld` /
  `FrameRenderer` / `FrameOrchestrator` and the UI overlay, then a large `if/else` chain
  (`:235-311`) selects a scene from `AGAPANTHE_SCENE`. `window.Updated` (`:791-840`) runs the
  free-fly camera; `window.KeyPressed` (`:697-789`) handles keys; `window.Rendered`
  (`:842-973`) is the frame bracket. The `finally` block (`:980-1009`) tears down in a
  **load-bearing order** (M4-11): `frameRenderer.WaitIdle → frameRenderer.Dispose →
  world.Dispose → registry.Dispose → renderer.Dispose → device.DeletionQueue.FlushAll →
  swapchain.Dispose → device.Dispose → ResourceTracker.Report() → window.Dispose`. The report
  is emitted **before** `window.Dispose()` because the native GLFW teardown can hit an
  uncatchable 0xC0000005 that would otherwise mask the 0-leak result. ~1340 lines of static
  helpers follow: asset builders (`BuildGrassImage`, `BuildSkyEnvironment`, `BuildSphereModel`,
  `BuildGroundModel`, `BuildBlackEnvironment`), camera framing (`FramePlanetCamera` & co), PPM
  writers, scene setup (`SetupPlanetScene`, `SetupPlanetDrop`, `SetupPlanetChallenge`,
  `SpawnGrid`, `SpawnDropScene`), parsers (`ParseGrid` `:1203`, `ParseDrop` `:1226`), and system
  classes (`BenchSpinSystem`, `ChurnSystem`, `ProbeDropSystem`, `LandingChallengeSystem`).
- **The scene chain is 5 families, not 7 scenes.** `model` (the default: one glTF), `grid:NxN`
  and `drop:N` are **one shared code path** — glTF load → `registry.Load` → replicate
  (`SpawnGrid` / `SpawnDropScene` / once) → ground plane → 3-point light rig → `FrameCamera` —
  with `ParseGrid` / `ParseDrop` choosing the replication branch. `planet`, `planet-drop`,
  `planet-challenge` are the other family (shared `SetupPlanetScene` + sun-only rig + black env,
  then drop/challenge add physics + spawner). `drive` is its own small path (one steerable
  zero-gravity body). `planet-drop` + `AGAPANTHE_LOAD` also restores a VS-1 snapshot.
- **`samples/HeadlessSim/Program.cs`** — 250 lines, no GPU, no window, closure `{Core, Engine,
  World}` only (guarded by `EngineIsHeadlessTests.ProjectFile_ReferencesExactlyTheAllowedProjects`,
  which pins its `ProjectReference` set exactly). The dedicated-server seed. Local
  `const float FixedDt = 1f / 60f`.
- **`src/Agapanthe.Engine.Render/FrameOrchestrator.cs`** — composes a `SimulationHost`, owns
  nothing. Real signatures:
  `CreateDefault(GameWorld world, Renderer renderer, ResourceRegistry registry, Camera camera,
  RenderList render, float fixedTickDeltaSeconds = 1f/60f, float maxWallClockDeltaSeconds = 0.25f)`
  and
  `CreateDefault(SimulationHost simulation, GameWorld world, Renderer renderer,
  ResourceRegistry registry, Camera camera, RenderList render, float fixedTickDeltaSeconds = 1f/60f,
  float maxWallClockDeltaSeconds = 0.25f)` — the second is the **attach-presentation-to-an-existing-host**
  overload (its doc: "a composition root that decides between 'server only', 'client + server'
  and 'client only' must be able to build and configure the host itself, then hand it over").
  `Tick(wallClockDt)` → `_accumulator.AdvanceFrame(_simulation, dt)`. `EndFrame()`.
  `RenderDelegate`. `FixedTickDeltaSeconds => _accumulator.FixedDeltaSeconds`.
- **`src/Agapanthe.Engine/SimulationHost.cs`** — world + tick schedule + frame self-measurement
  + MP-0d input phase (`SampleInput` / `InputMap` / `ApplyCommand` / `Commands` /
  `DiscardedCommandCount`). `CreateDefault(GameWorld)` registers `PropagateSystem`. Private
  ctor `SimulationHost(GameWorld world)`.
- **`src/Agapanthe.World/GameWorld.cs`** — `GameWorld()` → `GlobalIdRange.Default`,
  `UniverseId.None`. `GameWorld(GlobalIdRange range, UniverseId universe = default)` (`:191`).
  `Universe` getter (`:1085`). No production host calls the two-arg ctor — the MP-0b debt this
  milestone pays.
- **`src/Agapanthe.World/WorldSerialization.cs:175`** —
  `Load(Stream, SnapshotAllocatorPolicy)`; `Load(Stream)` defaults to
  `SnapshotAllocatorPolicy.AdoptFromHeader`. Universe reconciliation (`:218-243`): both `None` →
  stays unidentified; snapshot `None`, world set → **`Kept`, no throw**; snapshot set, world
  `None` → `Adopted`; both set equal → `Confirmed`; **both set & different → throws
  `WorldSerializationException`**.
- **`src/Agapanthe.World/PhysicsSettings.cs`** — `readonly struct`, `FixedDt` field, default
  `1f/60f` (`PhysicsSettings.Default(groundY)` at `:60`). Lives in `World`, which does **not**
  reference `Engine`.
- **`src/Agapanthe.Engine/PhysicsSystem.cs:38`** —
  `internal static bool RatesMatch(float tickDeltaSeconds, float fixedDt) => tickDeltaSeconds == fixedDt;`
  + `Debug.Assert` at `:43` — the only thing reconciling the ~4 independent `1f/60f` literals.
- **`src/Agapanthe.Platform/EngineWindow.cs`** — `sealed class`, the one public type in
  `Platform` (which references `Agapanthe.Core` only, plus `Silk.NET.Windowing/Input/Glfw`
  packages). Internally has `using Silk.NET.Windowing;` and a field
  `private readonly IWindow _window;` (the Silk `IWindow`). Public surface: events `Loaded` /
  `Updated(double)` / `Rendered(double)` / `FramebufferResized(int,int)` / `Closing` /
  `KeyPressed(Key)`; props `Title { get; set; }`, `FramebufferSize`, `VkSurface` (`IVkSurface?`
  from `Silk.NET.Core.Contexts`), `Input` / `Keyboard` / `Mouse`, `MouseDelta` (**zeroed
  immediately after `Updated` fires**, `:57-64`), `MouseCaptured`, `CaptureMouseOnClick`;
  methods `IsKeyDown(Key)`, `SetMouseCaptured(bool)`, `GetRequiredVulkanExtensions()`, `Run()`,
  `Close()`, `Dispose()`.
- **`Agapanthe.slnx`** — the solution file (`.slnx`, not `.sln`). A new project must be added.
- Locked rule: **no `Vk*` outside `Agapanthe.Graphics`**; `Silk.NET.Vulkan` only in `Graphics`.
  `Silk.NET.Input` / `Silk.NET.Windowing` / GLFW are already `Platform` package references.

### Why now

1. Measured (S25): `Engine` = 10 public types / 6 files vs `Program.cs` = 2360 lines. "The
   engine layer is in the Sandbox." The test that cuts: *a second, different game without
   editing the engine* → not possible.
2. First real `UniverseId` stamp — MP-0b closed the mechanism; "usage awaits the first real
   host". This is that host.
3. `FrameOrchestrator.CreateDefault(SimulationHost, …)` already anticipates a composer that
   picks topology — nothing exercises it.
4. Debt that "bites at the first `Agapanthe.App`": fixed-step single definition (backlog
   §Physique, netcode prerequisite).

### Explicitly out of scope

Declarative scene/prefab format (the `ISceneRecipe` registry here is **code organisation** — the
VS-1 snapshot stays a *save*, not an authoring artifact), stable asset identity / cook, second
slice, job system, netcode transport, **in-process scene switching**,
`FixedTimestepAccumulator.Reset()`, `HostRole` enum, dedicated-server path through `AppHost`,
`CameraInput` unification, rebindable input / action-map assets, a home-grown `Key` enum.

## Design

### Architecture

```
                         ┌──────────────────────── src/Agapanthe.App (new, IsAotCompatible) ─────────────────────────┐
                         │  IWindow (exposes Silk.NET.Input.Key, IVkSurface — both already non-Vulkan)               │
                         │  IGame / ISceneRecipe / SceneContext / HostOptions                                        │
                         │  AppHost.RunClient(IGame game, IWindow window, string[] args, HostOptions? options = null)               │
                         └───────┬──────────────────────────────┬───────────────────────────────────────────────────┘
                references (types in SceneContext)     PackageReference Silk.NET.Input (for Key in IWindow)
                                 │
      ┌──────────────────────────┼───────────────────────────────────────────────┐
      ▼                          ▼                          ▼                     ▼
  Engine.Render            Rendering / Graphics        Engine                 Assets / Ui / Core / World
  (FrameOrchestrator)      (Renderer, Camera, …)       (SimulationHost,
                                                        SimulationSettings ◄── the fixed-step single definition)

  ┌── ACYCLIC: App does NOT reference Platform; Platform stays a Vulkan-free leaf (unchanged) ───────────────────────┐
  │  src/Agapanthe.Platform  ──references──►  Core   (UNCHANGED — no ref to App, EngineWindow untouched)             │
  │  samples/Sandbox         ──references──►  src/Agapanthe.App  AND  src/Agapanthe.Platform                         │
  │      Sandbox defines  EngineWindowAdapter(EngineWindow) : Agapanthe.App.IWindow   (~40 lines forwarding)         │
  │      Program.cs = return AppHost.RunClient(new SandboxGame(args),                                                │
  │                        new EngineWindowAdapter(new EngineWindow(title, w, h)), args);                            │
  └────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘

  samples/HeadlessSim  ── UNCHANGED reference set {Core, Engine, World} ──  (reads host.Settings.FixedDeltaSeconds,
                                                                            deletes its local FixedDt const)
```

**Why `AppHost` does not construct the window, and why `EngineWindow` is untouched.** `IWindow`'s
location is locked to `Agapanthe.App` (interview). Making `EngineWindow : Agapanthe.App.IWindow`
would force `Platform → App` and drag `App`'s whole closure (Engine.Render → Rendering → Graphics
→ Silk.NET.Vulkan) transitively into `Agapanthe.Platform`, which is a Vulkan-free leaf today
(only the opaque `IVkSurface` crosses its boundary). So instead: `EngineWindow` stays exactly as
it is, and **`Sandbox`** — which already references both `App` and `Platform` — defines
`sealed class EngineWindowAdapter(EngineWindow inner) : IWindow`, ~40 lines of pure forwarding.
`AppHost` takes `IWindow`; the caller builds `new EngineWindowAdapter(new EngineWindow(...))` and
hands it in. `Agapanthe.App` never references `Platform`; `Agapanthe.Platform` gains nothing.
The `args` flow `Program.cs → RunClient → SceneContext.Args`, so
`ModelSceneRecipe` can do `ResolveModelPath(ctx.Args)` (`dotnet run … -- MetalRoughSpheres.glb`,
a documented CLAUDE.md workflow, keeps working); `game.Title` feeds `GraphicsDevice`'s app name
(its only consumer — the window title is the caller's, set on the `EngineWindow` it constructs).

`Agapanthe.App` sits **above** `Engine.Render` — the composition-root layer. It is **not** in
the `Agapanthe.Engine` headless closure, so `EngineIsHeadlessTests` is unaffected: the existing
`[InlineData]` for `HeadlessSim.csproj` already pins its reference set *exactly*
(`{Core, Engine, World}`), so it fails automatically if `Agapanthe.App` is ever added there — no
new row needed (test 11 just re-asserts it stays green).

### Components

| Component | Responsibility | File path | Notes |
|---|---|---|---|
| `IWindow` | The windowing + input surface the host needs, backend-agnostic. 1:1 with `EngineWindow`'s current public surface. | `src/Agapanthe.App/IWindow.cs` (new) | `using` `Silk.NET.Input`, `Silk.NET.Core.Contexts`. No `IInputSource` — input folds into `IWindow`. |
| `EngineWindow` | **Unchanged** — `Agapanthe.Platform` is not touched, stays a Vulkan-free leaf. | `src/Agapanthe.Platform/EngineWindow.cs` | — |
| `EngineWindowAdapter : IWindow` | `sealed class EngineWindowAdapter(EngineWindow inner) : IWindow` — ~40 lines forwarding every member (events re-raised, props/methods delegated). Lives in Sandbox because it needs both `App` (the interface) and `Platform` (the concrete type). | `samples/Sandbox/EngineWindowAdapter.cs` (new) | |
| `IGame` | App-level definition: `Title`, `Scenes`, `DefaultScene`, `Universe` (DIM → `UniverseId.None`). | `src/Agapanthe.App/IGame.cs` (new) | references `Agapanthe.World` for `UniverseId`. |
| `ISceneRecipe` | One scene family: `Name`, `Matches(token)` (DIM: ordinal-ignore-case equality; `ModelSceneRecipe` overrides it), `Build(SceneContext)`. | `src/Agapanthe.App/ISceneRecipe.cs` (new) | |
| `SceneContext` | The toolbox handed to `Build` (§Interfaces). First-party recipes get the full GPU + sim stack the host built. | `src/Agapanthe.App/SceneContext.cs` (new) | |
| `HostOptions` | Host-level knobs, defaulting from the environment. | `src/Agapanthe.App/HostOptions.cs` (new) | |
| `AppHost` | `static int RunClient(IGame, IWindow, string[] args, HostOptions? = null)`. Owns bootstrap, the `Tick`/`DrawFrame`/`EndFrame` bracket, the capture harness, the strict-order teardown, the host-debug hooks. Exposes `internal static ISceneRecipe SelectRecipe(IGame, string?)` and `internal static IReadOnlyList<(string Label, Action Step)> BuildTeardown(in TeardownTargets t)` for tests — **the `finally` iterates this same list**, so test 9 cannot pass while the real order drifts. | `src/Agapanthe.App/AppHost.cs` (new) | `TeardownTargets` is a `readonly record struct` of the nullable disposables (`FrameRenderer?`, `GameWorld?`, `ResourceRegistry?`, `Renderer?`, `GraphicsDevice?`, `Swapchain?`, `IWindow?`); each `Step` is null-safe, so `BuildTeardown(default)` yields the label list with harmless no-op steps. |
| `SimulationSettings` | `sealed class { float FixedDeltaSeconds { get; init; } = 1f/60f; static SimulationSettings Default { get; } }` — the fixed-step single definition. | `src/Agapanthe.Engine/SimulationSettings.cs` (new) | no deps; `Agapanthe.Engine.csproj` still carries no package reference. |
| `SimulationHost` | New `CreateDefault(GameWorld, SimulationSettings)` overload + `Settings` getter; the private ctor takes `SimulationSettings`; `CreateDefault(GameWorld)` delegates with `SimulationSettings.Default`. | `src/Agapanthe.Engine/SimulationHost.cs` (modify) | |
| `FrameOrchestrator` | `CreateDefault(SimulationHost, …)` **drops** its `fixedTickDeltaSeconds` param; the private ctor builds `_accumulator` from `internal static float ResolveAccumulatorStep(SimulationHost s) => s.Settings.FixedDeltaSeconds`. `CreateDefault(GameWorld, …)` keeps a `fixedTickDeltaSeconds` param (default `0f` = "use the single definition"; `< 0` → `ArgumentOutOfRangeException`). `Agapanthe.Engine.Render.csproj` gains `<InternalsVisibleTo Include="Agapanthe.Tests" />` so test 3 asserts `ResolveAccumulatorStep` directly. | `src/Agapanthe.Engine.Render/FrameOrchestrator.cs` (modify) | |
| `Agapanthe.App.csproj` | `<TargetFramework>net10.0</TargetFramework>`, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<IsAotCompatible>true</IsAotCompatible>`, `<ImplicitUsings>` matching the rest of `src/`. `PackageReference Silk.NET.Input` (`PrivateAssets="compile"` where the repo does that) for `Key`. `<InternalsVisibleTo Include="Agapanthe.Tests" />` (matches `Agapanthe.Engine.csproj:30` / `Agapanthe.Rendering.csproj:23` — tests 7 & 9 call `SelectRecipe` / `BuildTeardown` / `TeardownTargets`). `ProjectReference`: Engine.Render, Engine, Rendering, Graphics, Ui, Assets, World, Core. Added to `Agapanthe.slnx`. | `src/Agapanthe.App/Agapanthe.App.csproj` (new) | |
| `Sandbox.csproj` | + `ProjectReference ..\..\src\Agapanthe.App\Agapanthe.App.csproj`. Still publishes NativeAOT; `IsAotCompatible` on `Agapanthe.App` keeps the IL2xxx/IL3xxx gate live for the moved code. | `samples/Sandbox/Sandbox.csproj` (modify) | |
| `SandboxGame` | `IGame`: `Title = "Agapanthe Sandbox"`, `Scenes` = the 5 recipes, `DefaultScene = "model"`, `Universe => UniverseId.None`. | `samples/Sandbox/SandboxGame.cs` (new) | |
| `ModelSceneRecipe` | `Name = "model"`; `Matches` returns true for `"model"`, `null`/empty, `grid:*`, `drop:*`. Reproduces the shared glTF path incl. `ParseGrid`/`ParseDrop` branches, ground plane, 3-point rig, `FrameCamera`, bench mode (`AGAPANTHE_CULL_STATS` spinner), churn. | `samples/Sandbox/Scenes/ModelSceneRecipe.cs` (new) | the biggest recipe — it IS today's shared path. |
| `PlanetSceneRecipe` / `PlanetDropSceneRecipe` / `PlanetChallengeSceneRecipe` | The sun-only family. `PlanetDrop`/`PlanetChallenge` extend `Planet`'s setup (shared helper in `Content/`), add physics + spawner + `ApplyCommand`, and `PlanetDrop` handles `AGAPANTHE_LOAD` (`world.Load`). | `samples/Sandbox/Scenes/Planet*Recipe.cs` (new) | |
| `DriveSceneRecipe` | One steerable zero-gravity body, fixed camera, `InputMap` + `SampleInput` + `ApplyCommand`, `X` brake edge. | `samples/Sandbox/Scenes/DriveSceneRecipe.cs` (new) | |
| Sandbox `Content/`, `Systems/`, `Cameras/` | Static helpers + system classes lifted from `Program.cs` verbatim, re-homed. `Content/PlanetStage.cs` is the shared planet setup the three planet recipes call. | `samples/Sandbox/{Content,Systems,Cameras}/*.cs` (new) | |
| `Program.cs` | `return AppHost.RunClient(new SandboxGame(args), new EngineWindowAdapter(new EngineWindow("Agapanthe Sandbox", 1280, 720)), args);` | `samples/Sandbox/Program.cs` (shrink) | ~5 lines. |

### Data Model

```csharp
// src/Agapanthe.Engine/SimulationSettings.cs
namespace Agapanthe.Engine;

/// <summary>The simulation's protocol-level constants (MP-0-cap). Today only the fixed step; the
/// netcode milestone adds the tick rate as a wire constant here. The SINGLE place 1/60 is
/// DEFINED — FrameOrchestrator, the scene recipes and HeadlessSim read it rather than
/// re-literalling. (PhysicsSettings, in Agapanthe.World, cannot reference this type — see the
/// layering note in §Interfaces; its literal is reconciled at runtime by PhysicsSystem.RatesMatch.)</summary>
public sealed class SimulationSettings
{
    /// <summary>The fixed simulation step, seconds. A PERIOD, not a rate. Default 1/60.</summary>
    public float FixedDeltaSeconds { get; init; } = 1f / 60f;

    public static SimulationSettings Default { get; } = new();
}
```

```csharp
// src/Agapanthe.App/IWindow.cs
namespace Agapanthe.App;

using System.Numerics;
using Silk.NET.Core.Contexts;
using Silk.NET.Input;

/// <summary>Windowing + input surface the host needs. Matches Agapanthe.Platform.EngineWindow 1:1.
/// Exposes Silk.NET.Input.Key and the opaque IVkSurface — neither is a Vulkan type, and both
/// already cross Platform's public boundary today.</summary>
public interface IWindow : IDisposable
{
    event Action? Loaded;
    event Action<double>? Updated;
    event Action<double>? Rendered;
    event Action<int, int>? FramebufferResized;
    event Action? Closing;
    event Action<Key>? KeyPressed;

    string Title { get; set; }
    (int Width, int Height) FramebufferSize { get; }
    IVkSurface? VkSurface { get; }
    Vector2 MouseDelta { get; }        // NB: EngineWindow zeroes this right after Updated fires
    bool MouseCaptured { get; }
    bool CaptureMouseOnClick { get; set; }

    bool IsKeyDown(Key key);
    void SetMouseCaptured(bool captured);
    string[] GetRequiredVulkanExtensions();
    void Run();
    void Close();
}
```

```csharp
// src/Agapanthe.App/IGame.cs
namespace Agapanthe.App;
using Agapanthe.World;

public interface IGame
{
    string Title { get; }
    IReadOnlyList<ISceneRecipe> Scenes { get; }
    string DefaultScene { get; }
    UniverseId Universe => UniverseId.None;   // default interface member
}
```

```csharp
// src/Agapanthe.App/ISceneRecipe.cs
namespace Agapanthe.App;

public interface ISceneRecipe
{
    /// <summary>The canonical AGAPANTHE_SCENE token for this recipe (used for DefaultScene lookup).</summary>
    string Name { get; }

    /// <summary>True if <paramref name="sceneToken"/> (already trimmed; may be null/empty) selects
    /// this recipe. Default: ordinal-ignore-case equality with <see cref="Name"/>.
    /// <see cref="ISceneRecipe"/> for ModelSceneRecipe overrides it to also accept null/empty and
    /// the "grid:" / "drop:" prefixes.</summary>
    bool Matches(string? sceneToken)
        => sceneToken is not null
           && string.Equals(sceneToken, Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Build the scene: spawn entities, register systems on ctx.Orchestrator /
    /// ctx.Simulation, frame the camera (and, for a free-fly scene, subscribe to
    /// ctx.Window.Updated to drive ctx.Controller — see the camera-control note in §Interfaces),
    /// wire InputMap / SampleInput / ApplyCommand, subscribe to ctx.Window.KeyPressed for this
    /// scene's own keys. Called ONCE, after the host built the GPU stack + orchestrator, before
    /// the first Tick.</summary>
    void Build(SceneContext ctx);
}
```

```csharp
// src/Agapanthe.App/SceneContext.cs
namespace Agapanthe.App;
using Agapanthe.Engine;
using Agapanthe.Engine.Render;
using Agapanthe.Graphics;
using Agapanthe.Rendering;
using Agapanthe.World;

/// <summary>The toolbox a recipe gets — the full GPU + sim stack the host built, exactly what
/// Program.cs touched inline. Recipes are trusted first-party code.</summary>
public sealed class SceneContext
{
    public required GraphicsDevice Device { get; init; }
    public required ResourceRegistry Registry { get; init; }
    public required GameWorld World { get; init; }
    public required FrameOrchestrator Orchestrator { get; init; }
    public required Renderer Renderer { get; init; }
    public required Camera Camera { get; init; }
    public required FreeCameraController Controller { get; init; }
    public required IWindow Window { get; init; }
    public required RenderList RenderList { get; init; }
    public required string[] Args { get; init; }

    public SimulationHost Simulation => Orchestrator.Simulation;
}
```

```csharp
// src/Agapanthe.App/HostOptions.cs
namespace Agapanthe.App;
using Agapanthe.World;

public sealed class HostOptions
{
    public UniverseId? Universe { get; init; }         // null → fall back to IGame.Universe
    public int MaxFrames { get; init; } = -1;          // AGAPANTHE_MAX_FRAMES; > 0 → synthetic dt + auto-close
    public string? CapturePath { get; init; }          // AGAPANTHE_CAPTURE
    public string? CaptureUiPath { get; init; }        // AGAPANTHE_CAPTURE_UI
    public bool OverlayVisible { get; init; } = true;  // AGAPANTHE_OVERLAY != "0"
    public bool CullStats { get; init; }               // AGAPANTHE_CULL_STATS

    /// <summary>Builds options from the process environment (the default when RunClient's
    /// <c>options</c> is null, so existing AGAPANTHE_* runs behave identically). <paramref name="read"/>
    /// is the env accessor — defaults to Environment.GetEnvironmentVariable; a test passes a dict
    /// lookup so it never mutates process state. A malformed AGAPANTHE_UNIVERSE logs a warning and
    /// leaves <see cref="Universe"/> null (→ IGame.Universe wins).</summary>
    public static HostOptions FromEnvironment(Func<string, string?>? read = null);
}
```

```csharp
// src/Agapanthe.App/AppHost.cs
namespace Agapanthe.App;

public static class AppHost
{
    /// <summary>Runs a windowed client to completion. Returns 0 on a clean shutdown (no GPU
    /// leak), 1 otherwise. The caller owns <paramref name="window"/>'s type; the host owns its
    /// lifecycle from here (it is disposed LAST, in the strict teardown). <paramref name="args"/>
    /// is the raw process command line — the host does not parse it, it flows to
    /// <see cref="SceneContext.Args"/> so a recipe can (e.g. ModelSceneRecipe picks the glTF).</summary>
    public static int RunClient(IGame game, IWindow window, string[] args, HostOptions? options = null);

    internal static ISceneRecipe SelectRecipe(IGame game, string? sceneToken);              // test 7
    internal static IReadOnlyList<(string Label, Action Step)> BuildTeardown(in TeardownTargets t); // test 9
}

/// <summary>The disposables the strict teardown acts on. All nullable — RunClient fills what it built
/// (init may have thrown early). BuildTeardown(default) yields the label list with null-safe no-op steps.</summary>
internal readonly record struct TeardownTargets(
    FrameRenderer? FrameRenderer, GameWorld? World, ResourceRegistry? Registry, Renderer? Renderer,
    GraphicsDevice? Device, Swapchain? Swapchain, IWindow? Window);
```

`RunClient`'s `finally` does `foreach (var (_, step) in BuildTeardown(targets)) step();` — the
executed sequence and the asserted labels are the **same list**, they cannot drift.

### Interfaces — call flow

**`AppHost.RunClient(game, window, args, options)`**:

1. `options ??= HostOptions.FromEnvironment()`.
2. `window.Loaded +=`:
   a. `device = new GraphicsDevice(game.Title, window.GetRequiredVulkanExtensions(), window.VkSurface!);
      swapchain = new Swapchain(...); renderer = new Renderer(...)` — verbatim from
      `Program.cs:112-137` (with `game.Title` for the app name), including the `AGAPANTHE_IBL_TEST`
      early return.
   b. `registry = new ResourceRegistry();` + the `AGAPANTHE_UNLOAD_TEST` loop, verbatim.
   c. `var universe = options.Universe ?? game.Universe;`
      `world = new GameWorld(GlobalIdRange.Default, universe);`
   d. `var sim = SimulationHost.CreateDefault(world, SimulationSettings.Default);`
   e. `frameRenderer = new FrameRenderer(device, swapchain, () => window.FramebufferSize);`
   f. `orchestrator = FrameOrchestrator.CreateDefault(sim, world, renderer, registry, camera, renderList);`
      — the **attach-to-existing-host** overload (composition-root seam, exercised for real).
   g. UI overlay wiring verbatim (`Program.cs:505-535`), `debugOverlay.Visible = options.OverlayVisible`.
   h. `var recipe = SelectRecipe(game, Env("AGAPANTHE_SCENE")?.Trim());`
      `recipe.Build(new SceneContext { Device = device, … , Args = args });`
   i. `AGAPANTHE_SHADER_RELOAD_TEST` hook verbatim.
3. `window.FramebufferResized +=` verbatim (`camera.AspectRatio`, `resizePending`).
4. **The host does NOT subscribe to `window.Updated`.** Per-frame camera control is per-scene
   (a `drive` scene wants the camera *fixed*, `planet`/`model` want free-fly), so a free-fly
   recipe subscribes to `ctx.Window.Updated` itself and calls `ctx.Controller.Update(ctx.Camera,
   (float)dt, in input)` there — the same event, the same variable dt, the same place `MouseDelta`
   is valid (it is zeroed right after `Updated`; a read from any later callback is always zero).
   This is a **verbatim move of the `Updated` handler body into the recipe**, not a relocation to
   another callback. `AGAPANTHE_INPUT_DEBUG` moves with it.
5. `window.KeyPressed +=`: **host-level keys only** — `Escape` (two-stage), PageUp/Down &
   Home/End (look sensitivity), `Equal`/`Minus` (exposure), `N` (debug view), `L` (light swing),
   `F3` (overlay toggle, when `debugOverlay is not null`). **Scene-level keys move into the
   recipe that owns their state**, which subscribes to `ctx.Window.KeyPressed` in `Build`:
   `B` → `PlanetDropSceneRecipe` / `PlanetChallengeSceneRecipe` (each already holds its
   `probeDropper` / `landingChallenge`, so the `when` guard becomes an unconditional handler the
   recipe only registers when it built that system); `X` → `DriveSceneRecipe`; `F5` →
   `PlanetChallengeSceneRecipe` (holds `world`). A recipe that did not build the relevant system
   simply never subscribes — the guard is replaced by *not registering the handler*.
6. `window.Rendered +=`: verbatim `Program.cs:842-973` —
   `var wallClockDt = options.MaxFrames > 0 ? orchestrator.FixedTickDeltaSeconds : (float)dt;`
   `orchestrator.Tick(wallClockDt); frameRenderer.DrawFrame(orchestrator.RenderDelegate);
   orchestrator.EndFrame();` then bench logging (`options.CullStats`), capture arming
   (`options.CaptureUiPath`), auto-close (`options.MaxFrames`), HDR + swapchain dump.
7. `try { window.Run(); }`
8. `finally { foreach (var (_, step) in BuildTeardown(targets)) step(); }` — the strict-order
   teardown, **verbatim** `Program.cs:980-1009`. `BuildTeardown` returns, in order:
   ```
   "frameRenderer.WaitIdle", "frameRenderer.Dispose", "world.Dispose", "registry.Dispose",
   "renderer.Dispose", "device.DeletionQueue.FlushAll", "swapchain.Dispose", "device.Dispose",
   "ResourceTracker.Report" (sets `clean`), "window.Dispose"
   ```
   `ResourceTracker.Report()` at position 9 runs **before** `window.Dispose()` at 10 — the 0-leak
   result is never masked by a native GLFW fault (`Program.cs:1000-1008`). The `finally` iterates
   this exact list, and test 9 asserts its `Label` projection — no drift possible.
9. `Environment.Exit(clean ? 0 : 1)` — kept in the public entry, matching `Program.cs:1011`.
   *(A full GPU-free `RunClient` is not achievable — `window.Loaded` unconditionally builds
   `GraphicsDevice` — so end-to-end coverage is: `SelectRecipe` (test 7), `BuildTeardown` order
   (test 9), `HostOptions` (test 8), plus the windowed capture gate. See Testing Strategy.)*

**`SelectRecipe(game, token)`**: `token` null/empty → the recipe whose `Name == game.DefaultScene`
(exactly one; else `InvalidOperationException` — a game-definition bug); else the first recipe
whose `Matches(token)` is true; else `throw new ArgumentException($"AGAPANTHE_SCENE '{token}'
matches no scene")`.

**`SimulationHost`** additions:

```csharp
private SimulationHost(GameWorld world, SimulationSettings settings)
{
    _scheduler = new SystemScheduler(world.FlushStructuralChanges);
    _discard = Discard;
    Settings = settings;
}

public static SimulationHost CreateDefault(GameWorld world)
    => CreateDefault(world, SimulationSettings.Default);

public static SimulationHost CreateDefault(GameWorld world, SimulationSettings settings)
{
    ArgumentNullException.ThrowIfNull(world);
    ArgumentNullException.ThrowIfNull(settings);
    var host = new SimulationHost(world, settings);
    host._scheduler.Add(Stage.PostSimulation, new PropagateSystem(world));
    return host;
}

/// <summary>The simulation's protocol constants — today the fixed step. The single definition;
/// FrameOrchestrator's accumulator and the application's PhysicsSettings both derive from it.</summary>
public SimulationSettings Settings { get; }
```

**`FrameOrchestrator`** change — the two `CreateDefault` overloads:

```csharp
// Agapanthe.Engine.Render.csproj gains <InternalsVisibleTo Include="Agapanthe.Tests" /> (test 3)

// the ONE place the accumulator's step is decided (the private ctor calls it)
internal static float ResolveAccumulatorStep(SimulationHost simulation)
    => simulation.Settings.FixedDeltaSeconds;

// attach-to-existing-host: fixedTickDeltaSeconds PARAM REMOVED, read from the host
public static FrameOrchestrator CreateDefault(
    SimulationHost simulation, GameWorld world, Renderer renderer, ResourceRegistry registry,
    Camera camera, RenderList render, float maxWallClockDeltaSeconds = 0.25f)
{
    // ... private ctor: _accumulator = new FixedTimestepAccumulator(
    //         ResolveAccumulatorStep(simulation), maxWallClockDeltaSeconds);
}

// build-a-host convenience: param kept; 0f = "use the single definition", < 0 rejected LOUDLY
public static FrameOrchestrator CreateDefault(
    GameWorld world, Renderer renderer, ResourceRegistry registry, Camera camera, RenderList render,
    float fixedTickDeltaSeconds = 0f,
    float maxWallClockDeltaSeconds = 0.25f)
{
    ArgumentOutOfRangeException.ThrowIfNegative(fixedTickDeltaSeconds);
    var step = fixedTickDeltaSeconds == 0f ? SimulationSettings.Default.FixedDeltaSeconds : fixedTickDeltaSeconds;
    var sim = SimulationHost.CreateDefault(world, new SimulationSettings { FixedDeltaSeconds = step });
    return CreateDefault(sim, world, renderer, registry, camera, render, maxWallClockDeltaSeconds);
}
```
*(A `0f` sentinel rather than `= 1f/60f` keeps the literal in exactly one place;
`ThrowIfNegative` preserves `FixedTimestepAccumulator`'s existing loud rejection of a bad step
rather than silently coercing it to 1/60. Callers passing an explicit positive step — the
`AccumulatorEquivalenceTests` — still work; none in the repo pass `0`.)*

**Fixed-step "single definition" — layering note.** `SimulationSettings` is in
`Agapanthe.Engine`; `PhysicsSettings` is in `Agapanthe.World`, which cannot reference `Engine`.
So `PhysicsSettings.FixedDt` is **not** made to derive from it structurally. Instead:
`SimulationSettings.FixedDeltaSeconds` is the one place the value is **defined**; every consumer
**reads** it:
- `FrameOrchestrator` → `simulation.Settings.FixedDeltaSeconds` (above).
- scene recipes → `new PhysicsSettings(..., fixedDt: ctx.Simulation.Settings.FixedDeltaSeconds)`.
- `HeadlessSim` → `host.Settings.FixedDeltaSeconds`; its local `const float FixedDt` is deleted.
- `PhysicsSettings.Default(groundY)` keeps its `1f/60f` literal (a `World` type with no access
  to `Engine`) + an XML-doc note naming `SimulationSettings` as canonical; `PhysicsSystem.RatesMatch`
  + `Debug.Assert` is the runtime reconciliation and now fires if a recipe forgets to thread the
  value through.

### Data flow — a capture run (the determinism gate)

1. `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420 AGAPANTHE_OVERLAY=0 AGAPANTHE_DROP_EVERY=12
   AGAPANTHE_CAPTURE=hdr.ppm AGAPANTHE_CAPTURE_UI=ui.ppm dotnet run --project samples/Sandbox`.
2. `Program.cs` → `AppHost.RunClient(new SandboxGame(args), new EngineWindowAdapter(new EngineWindow("Agapanthe Sandbox", 1280, 720)), args)`.
3. `HostOptions.FromEnvironment()` → `MaxFrames=420`, capture paths set, `OverlayVisible=false`.
4. `window.Loaded` builds the GPU stack; `universe = None`;
   `sim = CreateDefault(world, SimulationSettings.Default)`;
   `orchestrator = CreateDefault(sim, world, …)`.
5. `SelectRecipe` → `PlanetDropSceneRecipe`. `Build` reproduces `SetupPlanetScene` (via
   `Content/PlanetStage`) + `SetupPlanetDrop` + `FramePlanetDropCamera` + `PhysicsSystem` +
   `ProbeDropSystem` + `ApplyCommand` for `SpawnProbe`, reading `AGAPANTHE_DROP_EVERY` etc.
   itself. `planet-drop` frames the camera fixed-ish; `Build` also subscribes to
   `ctx.Window.Updated` for the free-fly controller (same body as `Program.cs:791-840`).
6. Each `window.Rendered`: `wallClockDt = orchestrator.FixedTickDeltaSeconds` (MaxFrames > 0) →
   1 tick/frame. Capture armed at frame 418, dumped at 420, `window.Close()`.
7. `finally` teardown (label order above) → `ResourceTracker.Report()` → `Environment.Exit(0)`.
8. **Expected**: `md5(hdr.ppm) == 12638eddd7f3f67ab161b298ffbcd15e`,
   `md5(ui.ppm) == 034213575932dabcff41c2e0c72addfa`, byte-identical over 3 runs, 0 validation,
   0 leak — every step is the same code, only re-homed.

## Error Handling

| Failure | Handling |
|---|---|
| `AGAPANTHE_SCENE` names no recipe | `SelectRecipe` throws `ArgumentException` inside `window.Loaded`; it propagates out, the `finally` still runs (`ResourceTracker.Report()` still prints), `Environment.Exit(1)`. |
| `AGAPANTHE_UNIVERSE` malformed (not 32 hex) | `HostOptions.FromEnvironment` catches `FormatException` from `UniverseId.Parse`, `Log.Warn`, leaves `Universe = null` → `IGame.Universe` (`None`) wins. Matches the tolerant env-parsing everywhere else in the Sandbox. |
| **`AGAPANTHE_UNIVERSE` set AND `AGAPANTHE_LOAD` of a snapshot** | The recipe (`PlanetDropSceneRecipe`) calls `world.Load(stream)` (default policy `AdoptFromHeader`). Reconciliation (`WorldSerialization.cs:218-243`): existing Sandbox snapshots carry `UniverseId.None` → outcome **`Kept`**, no throw, the run resumes. A snapshot saved under a *different* non-`None` universe → **`WorldSerializationException`** ("the entity ids in this file mean something else"). The recipe lets it propagate out of `Build` → `finally` teardown → `Environment.Exit(1)` with that message. This is a new user-reachable failure the milestone creates; it is the intended behaviour (loading another universe's ids is exactly the mistake `UniverseId` exists to catch) and is documented in `AVANCEMENT.md` on close. |
| GPU init throws inside `window.Loaded` | Unchanged: the `finally` runs, leak report is produced, exit 1. |
| A recipe throws in `Build` | Propagates out of `window.Loaded`; `finally` teardown runs. No partial-scene recovery. |
| Recipe forgets to thread `FixedDeltaSeconds` into its `PhysicsSettings` | `PhysicsSystem`'s `Debug.Assert(RatesMatch(...))` fires in Debug on the first tick — the existing guard, now covering the recipe seam. |
| `window` native teardown faults (GLFW 0xC0000005) | Unchanged: leak report emitted **before** `window.Dispose()` (label 9 before label 10). |
| Two recipes' `Matches` both return true | `SelectRecipe` returns the first (`Scenes` list order); documented on `ISceneRecipe.Matches`. `SandboxGame` orders `model` last so `grid:`/`drop:` never shadow a named scene. |

## Testing Strategy

New file `tests/Agapanthe.Tests/AppHostTests.cs` + additions to existing files. All GPU-free.
The windowed capture path stays a manual/CI gate, as today.

| # | Test | Level | Cases (inputs → expected) |
|---|---|---|---|
| 1 | `SimulationSettings_Default_IsOneSixtieth` | unit | `SimulationSettings.Default.FixedDeltaSeconds == 1f/60f`; `new SimulationSettings { FixedDeltaSeconds = 1f/30f }.FixedDeltaSeconds == 1f/30f`. |
| 2 | `SimulationHost_CreateDefault_ExposesTheSettings` | unit | `CreateDefault(world).Settings` is `ReferenceEquals` `SimulationSettings.Default`; `CreateDefault(world, custom).Settings` is `ReferenceEquals` `custom`. |
| 3 | `FrameOrchestrator_ResolveAccumulatorStep_ReadsHostSettings` | unit | `FrameOrchestrator.ResolveAccumulatorStep(SimulationHost.CreateDefault(world, new SimulationSettings { FixedDeltaSeconds = 1f/30f })) == 1f/30f`; and `== SimulationSettings.Default.FixedDeltaSeconds` for `CreateDefault(world)` (via `InternalsVisibleTo`). Real executable cover of the one behavioural line the "single definition" decision is — the private ctor calls the same method. |
| 3b | `FrameOrchestrator_CreateDefault_RejectsNegativeStep` | unit | `CreateDefault(world, r, reg, cam, rl, fixedTickDeltaSeconds: -1f)` throws `ArgumentOutOfRangeException` **before** touching `r` (pass `null!` — the `ThrowIfNegative` is the first statement). `fixedTickDeltaSeconds: 0f` path is covered by test 3's `== Default`. |
| 4 | `PhysicsSystem_RatesMatch_TrueOnlyOnExactEquality` | unit | `RatesMatch(1f/60f, 1f/60f)` true; `RatesMatch(3f/60f, 1f/60f)` false (MP-0c ULP lesson — re-assert after refactor). |
| 5 | `EngineWindowAdapter_MatchesIWindowSurface` | unit | `typeof(IWindow).IsAssignableFrom(typeof(EngineWindowAdapter))`; the adapter declares **no public member outside `IWindow`** (reflection — catches an accidental extra surface / a missed forward). The forwarding itself is exercised by the windowed capture gate + human verdict (constructing `EngineWindow` opens a real GLFW window — not unit-testable). |
| 6 | `IGame_Universe_DefaultsToNone` | unit | a minimal test `IGame` with no `Universe` override → `game.Universe == UniverseId.None`. |
| 7 | `AppHost_SelectRecipe_DefaultThenMatchThenThrow` | unit | fake `IGame`, `DefaultScene="model"`, recipes `[Planet, Model]` (Model overrides `Matches` for null/empty + `grid:`/`drop:`): `null` → Model; `""` → Model; `"grid:8x8"` → Model; `"planet"` → Planet; `"drive"` → `ArgumentException`; a game whose `DefaultScene` matches 0 recipes → `InvalidOperationException`. |
| 8 | `HostOptions_FromEnvironment_UniverseResolution` | unit | `FromEnvironment(read)` with `read("AGAPANTHE_UNIVERSE") = "000...0001" (32 hex)` → `Universe == UniverseId.Parse(that)`; `read = "zzz"` → `Universe == null` + no throw; `read` returns null → `Universe == null`. `MaxFrames`/`CapturePath`/`OverlayVisible` likewise from `read`. No process-env mutation (injected accessor). |
| 9 | `AppHost_BuildTeardown_LabelsAreInStrictM4Order` | unit | `BuildTeardown(default).Select(x => x.Label)` → exactly `["frameRenderer.WaitIdle","frameRenderer.Dispose","world.Dispose","registry.Dispose","renderer.Dispose","device.DeletionQueue.FlushAll","swapchain.Dispose","device.Dispose","ResourceTracker.Report","window.Dispose"]`. Asserts `ResourceTracker.Report` is immediately before `window.Dispose`, and `world.Dispose` < `registry.Dispose` < `renderer.Dispose`. `RunClient`'s `finally` iterates the same list (§Interfaces step 8), so this is real cover for the order as it moves assemblies — not a parallel description that can drift. `BuildTeardown(default)` is safe: every `Step` is null-guarded. |
| 10 | `HeadlessSimSnapshotFormatTests` (existing) | integration | Re-run after `HeadlessSim` switches its `FixedDt` const to `host.Settings.FixedDeltaSeconds`: default `7e8dc68f…` (1868 B) and `--drive` `97e786f0…` **unchanged**. |
| 11 | `EngineIsHeadlessTests` (existing) | unit | Still green, **no change**. Its `HeadlessSim.csproj` `[InlineData]` already pins the reference set to `{Core, Engine, World}` *exactly*, so it fails automatically if `Agapanthe.App` is ever added there. No new row. |
| 12 | `AccumulatorEquivalenceTests` (existing) | unit | Rebuild every profile from `SimulationSettings.Default.FixedDeltaSeconds` instead of the local `Fixed` const; assertions unchanged. (It drives `FixedTimestepAccumulator` directly, not `FrameOrchestrator` — the orchestrator wiring is test 3's job.) |

**Manual / CI gates (unchanged bar):**

- **W1 records a new baseline**: on the pre-refactor tree, capture the `model`-scene HDR md5
  (`AGAPANTHE_SCENE` unset, `AGAPANTHE_GROUND=0`, `AGAPANTHE_MAX_FRAMES=180`, studio HDRI) and
  write it into the board — W2 (whole bootstrap + loop + teardown moved) gates on it.
- Captures ×3: HDR `12638eddd7f3f67ab161b298ffbcd15e` / UI `034213575932dabcff41c2e0c72addfa`
  (`planet-drop`, `MAX_FRAMES=420`, `DROP_EVERY=12`, `OVERLAY=0`, 1280×720, Debug).
- `HeadlessSim` default `7e8dc68f5a25914c84677a7a53ad3a58` (1868 B) · `--drive`
  `97e786f0455a53d856b9ba4affca1003`, JIT and NativeAOT.
- `dotnet build` 0 warning · `dotnet test` green (589 + new) · 0 validation · 0 leak ·
  0 alloc/frame steady state · NativeAOT publish of `samples/Sandbox` PASS.
- Double audit (`csharp-lowlevel` + `engine-architect`).
- **Human visual verdict** (Sandbox): the 5 recipes / 7 `AGAPANTHE_SCENE` tokens
  (`model`, `grid:20x20`, `drop:40` → `ModelSceneRecipe`; `planet`, `planet-drop`,
  `planet-challenge`, `drive`);
  **free-fly camera + mouse-look in `planet`** (the one path the pinned captures — mouse
  uncaptured — do not exercise); `drive` (WASD / Space / C / X); `B` (planet-drop + challenge);
  `F3` overlay; `F5` quicksave + `AGAPANTHE_LOAD` resume.

## Migration Path

Five waves, each ends green with every capture/snapshot hash unchanged.

| Wave | Content | Gate |
|---|---|---|
| **W1** | `SimulationSettings` (Engine) + `SimulationHost.CreateDefault(world, settings)` + `Settings` + `FrameOrchestrator.ResolveAccumulatorStep` (+ `InternalsVisibleTo`) + the `0f` sentinel / `ThrowIfNegative` on the convenience overload. New `src/Agapanthe.App` project (csproj as specified, added to `Agapanthe.slnx`) with `IWindow`. `Agapanthe.Platform` **untouched**. Tests 1–4, 3b, 12. **Record the `model`-scene HDR md5** on this tree. No Sandbox change. | build 0 warn · test green · captures + `HeadlessSim` snapshots **unchanged** (the sentinel resolves to the same `1f/60f`). |
| **W2** | `IGame` / `ISceneRecipe` / `SceneContext` / `HostOptions` / `TeardownTargets` / `AppHost.RunClient` + `SelectRecipe` + `BuildTeardown`. Port bootstrap + `Rendered` bracket + teardown + capture harness + host keyboard **verbatim**. Sandbox: `EngineWindowAdapter`, `SandboxGame` + **`ModelSceneRecipe` only** (default `model`, incl. its `Updated` free-fly body), `Sandbox.csproj` += `Agapanthe.App`, `Program.cs` → `AppHost.RunClient(new SandboxGame(args), new EngineWindowAdapter(new EngineWindow(...)), args)`. Tests 5, 6, 7, 8, 9. | Sandbox runs windowed on `model` (incl. `-- <model>.glb` arg); **`model` HDR md5 == the W1 baseline**; 0 leak / 0 validation; `dotnet test` green. |
| **W3a** | `ModelSceneRecipe` gains the `grid:` / `drop:` branches (`ParseGrid`/`ParseDrop`, `SpawnGrid`/`SpawnDropScene`, `BenchSpinSystem`, `ChurnSystem`, `AGAPANTHE_PHYSICS`). `Content/` + `Systems/` for these. | `grid:20x20` + `drop:40` launch clean; bench line logs; `model` md5 still == baseline. |
| **W3b** | The 3 planet recipes + `Content/PlanetStage.cs` + `DriveSceneRecipe`. `Cameras/`. Scene keyboard split (`B`/`X`/`F5` → recipes). `Program.cs` final (~5 lines). | captures ×3 (`planet-drop`) HDR `12638edd…` / UI `03421357…` **unchanged**; `planet` / `planet-challenge` / `drive` launch clean; `B` + `X` + `F5` + `AGAPANTHE_LOAD` behave. |
| **W4** | `UniverseId` end-to-end: `AGAPANTHE_UNIVERSE` in `HostOptions.FromEnvironment` + Error-Handling row behaviour (verify `Kept` on an existing snapshot; verify the throw message on a synthetic cross-universe snapshot). `HeadlessSim` `FixedDt` → `host.Settings.FixedDeltaSeconds`; re-verify test 10 JIT + AOT. NativeAOT publish of Sandbox. | test 8, 10 green; `HeadlessSim` hashes unchanged JIT == AOT; Sandbox AOT PASS; the `UNIVERSE`×`LOAD` cases behave as the table says. |
| **W5** | Mandatory tail: Self Code Review → Requirements Validation → Full Project Verification + double audit + human verdict. CONVERGE: `CLAUDE.md`, `docs/AVANCEMENT.md` (Reprise + module diagram), `docs/BACKLOG.md` (§4quater "Ensuite" → tick `Agapanthe.App`), archive board, suggest commit. | all gates; audits applied; board `completed`. |

**Rollback**: each wave is a coherent commit-able unit; W1's tree-clean hash is the milestone
rollback point.

## Open Questions

None blocking.

- **Test 3 form.** Constructing a real `FrameOrchestrator` needs a `Renderer` (GPU), so the
  behavioural line is covered by an `internal static ResolveAccumulatorStep(SimulationHost)` that
  the private ctor calls and test 3 asserts directly (via `InternalsVisibleTo`). If a future
  headless `Renderer` fake appears, promote test 3 to a full-orchestrator assertion.
- **`ModelSceneRecipe` size.** It absorbs today's entire shared model path (~400 lines of
  `Program.cs`). W3a/W3b split keeps each step's pixel movement attributable; if the recipe
  still feels too large, a follow-up `Content/ModelStage.cs` extraction is a Deferred-Work item,
  not a blocker.

## Decision Log

| Decision | Options considered | Chosen | Rationale |
|---|---|---|---|
| Milestone scope | host shell only; + scene breakup; minimal (loop only) | **host shell + `IGame` contract + scene breakup into `ISceneRecipe` files** | The registry is code organisation, not an authoring format (deferred). Scenes behind a contract *and* in files now is the honest "second game without editing the engine" test. |
| Game contract shape | callbacks `OnLoad/OnUpdate/OnRender`; **configuration + `ISceneRecipe`**; config + per-frame escape hatch | **configuration + `ISceneRecipe`** | Matches the existing scheduler ("registration order is execution order, frozen at first tick") and the MP-0a/c/d intent that the loop lives in the engine. One way to do a thing. |
| Project location & window coupling | `src/Agapanthe.App` ref Platform concretely; **`src/Agapanthe.App` + `IWindow` now**; keep in `samples/` | **`src/Agapanthe.App` + `IWindow` now** | Shipped engine layer, not a sample. `IWindow` decouples the host from the GLFW frame loop at the one seam that matters. |
| Acyclic project graph | `App→Platform`; `Platform→App` + `EngineWindow : IWindow`; **adapter in Sandbox, `EngineWindow` & `Platform` untouched**; a 3rd leaf project for `IWindow` | **`EngineWindowAdapter : IWindow` in Sandbox** | `IWindow` in `App` (locked). `Platform→App` would pull `App`'s whole closure (→ Rendering → Graphics → Silk.NET.Vulkan) transitively into `Platform`, a Vulkan-free leaf. `App→Platform` closes a cycle. A dedicated leaf project is +1 project for one interface. The adapter (~40 lines, in Sandbox which already has both refs) leaves `Platform` and `EngineWindow` exactly as they are. |
| CLI args route | `IGame.Args`; `HostOptions.Args`; **`string[] args` param on `RunClient`** | **`RunClient(IGame, IWindow, string[] args, HostOptions?)`** | `dotnet run … -- <model>.glb` is a documented workflow (`Program.cs:1045` `ResolveModelPath(args[0])`). Args are neither a game *definition* nor a host *option* — they are the invocation. Threaded to `SceneContext.Args`; the host itself never parses them. |
| `game.Title` consumer | window title (caller sets it); **`GraphicsDevice` app name** | **`GraphicsDevice` app name** | Since the caller now builds `EngineWindow` (with whatever title it wants), `game.Title`'s remaining use is the Vulkan app/instance name in `new GraphicsDevice(game.Title, …)` — one consumer, inside `RunClient`. |
| `IWindow` key type | **expose `Silk.NET.Input.Key`**; home-grown `Key` enum + table | **expose `Silk.NET.Input.Key`** | `Platform` already references `Silk.NET.Input`; a home-grown enum + mapping is the action-map milestone, deferred by MP-0d. `Agapanthe.App` gains a `Silk.NET.Input` package reference for this one type. |
| `IInputSource` | separate input interface; **fold into `IWindow`** | **fold into `IWindow`** | `EngineWindow` is the single input source today; a second interface with the same implementer is ceremony. Revisit at the action-map milestone. |
| `UniverseId` source | **`IGame.Universe` (default `None`) + `AGAPANTHE_UNIVERSE` override**; `SandboxGame` stamps a fixed non-`None` id; random per run | **`IGame.Universe` default `None` + env override** | `SandboxGame` staying `None` keeps every existing gate byte-identical (captures are pixels; no Sandbox snapshot hash is pinned; `HeadlessSim` is untouched). Mechanism wired end to end + a test exercises the override — the MP-0b debt is paid. A random default breaks future save+hash determinism (MP-0b's locked "non-random `None`" rationale). |
| Scene families | 7 independent recipes; **5 recipes, `model` covers `grid:`/`drop:`** | **5 recipes** | `model`/`grid:`/`drop:` are one shared code path in `Program.cs` with `ParseGrid`/`ParseDrop` branches — three "verbatim" recipes would be a fiction. `ModelSceneRecipe.Matches` accepts the family tokens; the planet family and `drive` are genuinely separate. |
| Camera control ownership | host keeps `window.Updated`; **recipes own it (verbatim move of the `Updated` body)** | **recipes own it, still on `window.Updated`** | `drive` needs the camera *fixed*, `planet`/`model` free-fly — per-scene. The handler body moves unchanged into the recipe's `Build` (subscribing to `ctx.Window.Updated`), so dt, call count and the `MouseDelta`-valid-only-in-`Updated` constraint are all preserved. Not a move to another callback. |
| Host vs scene keyboard | one big switch in the host; **host keys in the host, scene keys in the recipe** | **split by state ownership** | `B`/`X`/`F5` act on state a recipe now owns; their `when` guards become "the recipe only subscribes if it built that system". Host keeps camera/render/overlay keys. |
| Topology | **concrete seam, no enum**; + `HostRole` enum; real `DedicatedServer` | **concrete seam, no enum** | `AppHost.RunClient` builds the `SimulationHost` and hands it to `FrameOrchestrator.CreateDefault(sim, …)` — the composition-root seam, exercised for real. An enum with dead branches is vaporware; `RunDedicatedServer` is the netcode milestone. |
| `HeadlessSim` | **untouched**; folded into `Agapanthe.App` | **untouched** | MP-0a deliberately made it standalone; `Agapanthe.App` pulls in Graphics/Platform which a server must not have. Scene selection stays relaunch-only → `FixedTimestepAccumulator.Reset()` still does not bite, stays deferred. Only change: `FixedDt` const → `host.Settings.FixedDeltaSeconds`. |
| Fixed step | **`SimulationSettings` on `SimulationHost`, everyone reads it**; leave the ~4 literals | **`SimulationSettings` (single definition + readers)** | The host is now the composition point. Netcode prerequisite. Layering forbids `PhysicsSettings` (in `World`) from referencing it, so "single source" = one definition + readers + the existing `PhysicsSystem.RatesMatch` runtime reconciliation. `FrameOrchestrator`'s convenience-overload literal becomes a `0f` sentinel resolving to `SimulationSettings.Default`. |
| Teardown-order coverage | `internal` return-code seam + fake GPU stack; **`internal BuildTeardown` = the list the `finally` iterates, asserted by label order**; no automated cover | **`BuildTeardown` (test 9)** | A full GPU-free `RunClient` is impossible (`window.Loaded` builds `GraphicsDevice` unconditionally). `RunClient`'s `finally` iterates the very `IReadOnlyList<(Label, Step)>` that `BuildTeardown` returns, so test 9 asserting the `Label` projection cannot pass while the executed order drifts. Cheap, real seam for a cross-assembly move. |
| Naming | **`AppHost` / `IGame` / `ISceneRecipe` / `SceneContext`**; `Application`/`Game`/`IScene`/`SceneSetup` | **`AppHost` / `IGame` / `ISceneRecipe` / `SceneContext`** | `SceneBuilder` is taken (`Rendering`). `IGame` with DIMs fits "config, not callbacks". `AppHost` matches the project name. |

## Execution outcome (session 30)

**Delivered as designed** (spec rev 4). 5 waves + a post-audit fix pass, `dotnet test` **617 passed** (589 + 28),
`dotnet build` (slnx) 0 warning. Every pinned hash **unchanged**, JIT == NativeAOT: HDR `12638eddd7f3f67ab161b298ffbcd15e` /
UI `034213575932dabcff41c2e0c72addfa` (×3), `model`-scene baseline `df55d444b74c7aa94fd0ab18d795cc9c` (recorded
on the pre-refactor tree in W1), `HeadlessSim` default `7e8dc68f5a25914c84677a7a53ad3a58` (1868 B) + `--drive`
`97e786f0455a53d856b9ba4affca1003`. `Program.cs` 2360 → 22 lines. Sandbox AOT publish PASS, `AotComponentProbe` PASS.

**Adapter decision (post-approval, human).** `EngineWindow : Agapanthe.App.IWindow` in `Platform` would drag
`App`'s closure (→ Rendering → Graphics → Silk.NET.Vulkan) transitively into `Platform`, a Vulkan-free leaf. So
`Platform` and `EngineWindow` are **untouched**; `samples/Sandbox` defines `EngineWindowAdapter(EngineWindow) :
IWindow` (~75 lines forwarding). `Agapanthe.App` references no `Platform`. `EngineIsHeadlessTests` gained an
`[InlineData]` pinning `Agapanthe.App.csproj`'s reference set (static allowlist, MP-0a pattern).

**Double audit.**
- `engine-architect` — PASS-with-concerns **4.2/5**, **1 🔴**: `RunClient` returned 0 on an init failure —
  `clean` (the exit-code flag) is overwritten to the leak-report result by the teardown's `Report` step, and my
  `catch` around `window.Run()` did not record a failure. **Fixed**: `var failed` set in the `catch`,
  `return clean && !failed ? 0 : 1` (a bad `AGAPANTHE_SCENE` now exits 1, JIT + AOT verified).
- `csharp-lowlevel` — PASS-with-concerns, **no 🔴**. Teardown order verified EXACT character-for-character against
  the old `Program.cs:990-1008`; hot path 0-alloc confirmed (single shared display class for the 4 window
  handlers, `CameraInput` a `readonly struct in`, no LINQ/boxing per frame); no reflection; extractions faithful.

**Audit 🟠/🟡 applied**: per-step `try/catch` in the teardown loop (F-1, uses the `label`); `Log.Error($"...{ex}")`
full stack + comment covers the frame loop not just init (F-2); the `App ↛ Platform` `[InlineData]` gate (🟠-2);
`Log.Warn` when `AGAPANTHE_LOAD` is set on a non-restoring scene (🟠-4); F5 quicksave `catch` widened to
`UnauthorizedAccessException` (F-11); `AppHost.ResolveShaderDirectory` made public, `IblTestTool` dedup (F-9);
`WireFreeFly`/`WireProbeKey` extracted to `RecipeInput` (was duplicated `ModelSceneRecipe` ↔ `PlanetRecipeShared`);
stale window title. Self-review before the audit caught `SetupPlanetDrop`/`SetupPlanetChallenge` re-literalling
`1f/60f` — now threaded from `ctx.Simulation.Settings.FixedDeltaSeconds`.

**2nd fix pass ("fix everything fixable")**: **`HostOptions.Scene` + `SavePath` + `VerifyCull` + `ShaderReloadTest`**
— `RunClient` now reads **zero** environment variables (🟠-3, F-6); dead `IWindow` surface (`Closing`,
`CaptureMouseOnClick`) removed from the interface + adapter (F-8); `Ppm.WriteSwapchain` gains a buffer-length guard
(F-10); **spec test 5** delivered as `IWindowSurfaceTests` (reflection: every `IWindow` member exists on
`EngineWindow` by name/shape, and `EngineWindow` is never itself an `IWindow` — the acyclic-graph invariant);
`SelectRecipe` trims. Tests project gains a `ProjectReference` on `Agapanthe.Platform` (a leaf).

**Deferred** (board §Deferred Work, S30): 🟠 **`SceneContext` sim/presentation split** — all `required` → a recipe
cannot be built headless, `HeadlessSim` shares zero world-population code; *the debt that bites at the 2nd slice
and `RunDedicatedServer`* · 🟠 `EngineWindowAdapter` (~75 l) → `src/Agapanthe.Platform.App` at app #2 · 🟠
camera/light/ground/sky helpers → `App` at the content milestone · 🟡 `FrameOrchestrator` mid-list optional param
removed (public API) · 🟡 model-not-found exit 2→1 + full GPU bring-up (the path check is game-specific) · 🟡
planet recipes load the glTF after `SetupPlanetScene` (was before) — a pre-refactor `.save` may not resolve its handles.

**Human visual verdict: DUE.** Run `dotnet run --project samples/Sandbox` with `AGAPANTHE_SCENE` ∈
{`model`, `grid:20x20`, `drop:40`, `planet`, `planet-drop`, `planet-challenge`, `drive`}; check free-fly + mouse-look
in `planet` (the one path the pinned captures do not exercise); `drive` (WASD / Space / C / X); `B` (planet-drop +
challenge); `F3` overlay; `F5` quicksave + relaunch with `AGAPANTHE_LOAD` to resume.
