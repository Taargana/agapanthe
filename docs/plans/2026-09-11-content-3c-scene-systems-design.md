# Contenu-3c — scene-system/generator factories + `drive`/`planet*` migration

**Status**: brainstormed + plan-mode approved (2026-09-11); scored review 3.70 → **4.30/5 (Approved)** after iteration 2, two non-blocking follow-ups applied (env-var table scope precision, `AGAPANTHE_INPUT_DEBUG` row). Ready for user review.
**Predecessor**: `docs/plans/2026-09-09-content-3b-declarative-scenes-design.md` (closed, `910306c`).

## 1. Summary

Contenu-3b gave the engine a declarative scene format (`.agscene`) and a GPU-free
`SceneLoader`, but only the `model` family (single/grid/cluster) uses it. `drive`,
`planet`, `planet-drop`, `planet-challenge` remain 4 hand-coded `ISceneRecipe`s in
the Sandbox, driven by ~30 `AGAPANTHE_*` env vars, with their own bespoke camera
framers, procedural-geometry builders, and ad-hoc `SimCommand` wiring. Contenu-3c —
the last sub-milestone of the Contenu domain (`docs/BACKLOG.md` §4quater) — closes
that gap: every Sandbox scene becomes the same generic `SceneRecipe(string)`,
gameplay systems (`ProbeDropSystem`, `LandingChallengeSystem`) attach via a small
factory registry on `IGame` instead of being wired by hand per recipe, and
procedural geometry (planet/sun/probe/beacon spheres, the drive ground quad)
becomes cook-time generators producing real `.agmodel` blobs instead of runtime
code.

Two decisions shrink this from the literal backlog wording: **two** registries,
not three (a separate "command-handler" registry was rejected — the 2 existing
systems' input is intrinsically coupled to the one system consuming it, so each
system's factory wires its own input); and **procedural environment** (sky/black
HDR) stays exactly as today, runtime code, deferred to Contenu-2b (which already
owns `AssetKind.Environment`/`.agenv` — doing it here would duplicate that
design).

**Scope**: full migration of all 4 recipes (not a reduced slice). `drive` loses
its CLI arbitrary-model argument (`ResolveModelKey`) — a real, acknowledged
capability loss, traded for consistency with every other scene family since 3b's
D10 (concrete scene files, no parametric CLI overrides). Split into **3 gated
sub-phases**, because the format/registry infrastructure is a hard shared
prerequisite but each migrated scene is otherwise independent:

- **3c-1** — `.agscene` v2 (attractor physics, baked `Fixed` camera, new
  environment modes), the scene-system registry (`ISceneSystemFactory`), the
  procedural-generator registry (cook-time), migrate `planet` + `planet-drop`.
- **3c-2** — `LandingChallenge` system kind + factory, migrate `planet-challenge`.
- **3c-3** — `ground_quad` generator, migrate `drive` (dropping its CLI arg),
  delete now-dead Sandbox code.

Only **3c-1** is decomposed into an executable task board in this session — 3c-2
and 3c-3 gate on 3c-1's completion, matching the 3a→3b precedent.

## 2. Decision log

| # | Decision | Rationale |
|---|---|---|
| D1 | Full migration of all 4 recipes, not a reduced scope. | User confirmed after seeing the full gap analysis. |
| D2 | Procedural GEOMETRY → cook-time generators in `Agapanthe.Assets.Pipeline` → real `.agmodel` blobs (`AssetKind.Model`, indistinguishable from a glTF-sourced model to every downstream reader). Procedural ENVIRONMENT (sky/black) stays runtime code, relocated `Sandbox → Agapanthe.App` so `ClientScenePresenter` can reach it — deferred to Contenu-2b for a real cooked form. | Avoids duplicating Contenu-2b's already-scoped `AssetKind.Environment`/`.agenv` design; keeps the runtime materializer 100% generic (no procedural codegen at runtime). |
| D3 | **Two registries, not three.** A client-side "scene systems" factory registry (`IGame.SceneSystems`, `ISceneSystemFactory`) — each factory constructs its system AND wires its own input. A cook-time-only "procedural generator" registry (`Agapanthe.Assets.Pipeline`, invisible to `IGame`/runtime). No separate command-handler registry. | Only 2 known system kinds exist; their input handling is 1:1 coupled to the owning system. A 3rd registry would be indirection with a single consumer each — the exact kind of speculative machinery this codebase's audits have repeatedly flagged. |
| D4 | `.agscene` bumps to **v2**. `CookerVersion` → `contenu3c-1`. | This codebase's established precedent for growing a cooked format is version bump + drop old + full re-cook, not additive/optional framing (confirmed by `.agmodel` v1→v2, and `.agscene` itself has no TLV/optional-block escape hatch — `RequireExhausted()` rejects any trailing bytes an old reader doesn't expect). |
| D5 | Planet's 3 bespoke camera framers become cook-time trig baked into `SceneCamera.Fixed` (reserved from 3b) + two new fields (`MoveSpeed`, `ShadowDistance`). | All 3 framers compute a fully deterministic pose from scene-authored constants — no live-world dependency. Eliminates the single biggest chunk of would-be new runtime code. |
| D6 | `drive` drops its CLI model arg (`ResolveModelKey`). `drive.toml` fixes on `models/DamagedHelmet.glb`; its bounds-dependent `SetupLights` becomes fixed/authored lights; its `AGAPANTHE_HDRI`/`AGAPANTHE_GROUND` overrides are not reintroduced. | User confirmed, explicitly told this is a real capability loss (arbitrary-model physics smoke-testing), traded for consistency with `model`/`grid`/`drop`/`metalrough` since 3b. |
| D7 | `AGAPANTHE_SAVE` name collision (host-level `HostOptions.SavePath` vs. `planet-challenge`'s F5 quicksave) resolved by elimination: the F5 quicksave path becomes an authored `SceneSystem.QuicksavePath` field, no env var on that side. | Removes the collision by construction rather than renaming into a new collision risk; consistent with "concrete scene, not parametric" applied to every other planet constant this milestone. |
| D8 | Entity name→`GlobalId` lookup (mentioned in the original backlog bullet) is dropped from scope. | `planet-challenge`'s beacon/zone never needed it — confirmed it's plain `Double3`/`float` data, not an entity reference. No consumer exists; building it would be speculative. |
| D9 | `HeadlessSim` hard-errors (exit 1) on a scene whose `Systems` list is non-empty. | Scene systems are a client-only concept (window/camera-coupled). A silent partial run (physics-only, no gameplay) would diverge from what the Sandbox shows — worse than refusing outright. |
| D10 | 3-phase gated execution: 3c-1 (infra + planet/planet-drop) → 3c-2 (planet-challenge) → 3c-3 (drive + cleanup). | A fused board would be ~40 tasks, above the project's ~15-task complexity budget; the phases line up with real dependency boundaries (3c-2/3c-3 need 3c-1's format+registries, not each other's system kinds). |

## 3. Architecture

### 3.1 `.agscene` v2 binary format changes

**`ScenePhysics`** (`src/Agapanthe.Assets/Scene/SceneDefinition.cs`) gains an
optional Newtonian attractor, mirroring `PhysicsSettings.WithAttractor`'s own
"`Mu == 0` ⇒ no attractor, uniform-gravity path stays byte-identical" convention:

```csharp
public sealed record ScenePhysics
{
    public required Vector3 Gravity { get; init; }
    public required float GroundY { get; init; }
    public Double3 AttractorCenter { get; init; }  // meaningful only when Mu > 0
    public double Mu { get; init; }                 // 0 = no attractor
    public double SurfaceRadius { get; init; }
}
```

Binary `[physics]` block (replaces v1's): `present:u8 | [present] gravity f32×3 |
groundY f32 | mu f64 | attractorCenter f64×3 | surfaceRadius f64` — always written
together when present (no nested variant tag; 40 bytes is cheap). `SceneMaterializer.
Materialize` calls `.WithAttractor(...)` when `Mu > 0`, else the existing 3-arg
ctor.

**`SceneCamera`** gains two `Fixed`-only fields: `float MoveSpeed`, `float
ShadowDistance` (0 = `FrameBounds` keeps deriving them dynamically from
`AggregateBounds`, as today). Binary `[fixed]` variant appends `moveSpeed f32 |
shadowDistance f32`. `SceneCameraApplier.Apply`'s `Fixed` branch copies them
across when `> 0` — no new runtime *logic*, just two more copied fields.

**`SceneEnvironment`** gains two closed-enum values:

```csharp
public enum SceneEnvironmentMode : byte { None = 0, HdriPath = 1, ProceduralSky = 2, Black = 3 }
```

`ProceduralSky` (no payload — client derives the sun direction from `def.Lights`'
directional entry at apply time) covers `drive`'s sky (3c-3); `Black` (no payload,
fixed 16×8 all-zero HDR) covers all 3 planet scenes' env. `ClientScenePresenter.
ApplyEnvironment` grows two arms calling a new `Agapanthe.App.Scene.ProceduralSky`
(the relocated body of today's `Sandbox.ModelContent.BuildSkyEnvironment`,
verbatim algorithm) and a trivial `Agapanthe.App.Scene.BlackEnvironment.Build()`.

**`SceneSystemKind`/`SceneSystem`** — new closed tagged union (mirrors
`SceneLightKind`/`SceneCameraMode`/`SceneEnvironmentMode` precedent):

```csharp
public enum SceneSystemKind : byte { ProbeDrop = 0, LandingChallenge = 1 }

public sealed record SceneSystem
{
    public required SceneSystemKind Kind { get; init; }
    public required AssetKey ProbeModel { get; init; }
    public int ProbeLocalMesh { get; init; }
    public int ProbeLocalMat { get; init; }
    public required float ProbeRadius { get; init; }

    // ProbeDrop (3c-1)
    public int Every { get; init; }
    public Double3 Centre { get; init; }

    // LandingChallenge (3c-2)
    public Double3 AttractorCenter { get; init; }
    public double SurfaceRadius { get; init; }
    public double SurfaceBand { get; init; }
    public Double3 ZoneCenter { get; init; }
    public double ZoneRadius { get; init; }
    public double DropHeight { get; init; }
    public int TargetCount { get; init; }
    public int ShotBudget { get; init; }
    public string QuicksavePath { get; init; } = "";
}
```

`SceneDefinition` gains `IReadOnlyList<SceneSystem> Systems { get; init; } = [];`.
Binary layout appended after `[restore]`: `systemCount:u32` then per-system
`kind:u8 | probeModelKeyIdx:u32 | probeLocalMesh:u32 | probeLocalMat:u32 |
probeRadius:f32 | [ProbeDrop] every:i32 | centre:f64×3 | [LandingChallenge, 3c-2]
attractorCenter:f64×3 | surfaceRadius:f64 | surfaceBand:f64 | zoneCenter:f64×3 |
zoneRadius:f64 | dropHeight:f64 | targetCount:i32 | shotBudget:i32 |
quicksavePath (u16 len + UTF-8)`.

The key-table builder (shared with entities) must also collect
`def.Systems[*].ProbeModel` so a probe-only model still gets a key-table slot AND
gets uploaded — `SceneMaterializer.Materialize`'s model-loading loop must also
load every distinct `SceneSystem.ProbeModel` into its `models` dictionary (no
`SceneEntity` references it, but `ClientScenePresenter` uploads everything in
`result.Models`, and a runtime-dropped probe needs an uploaded model to resolve
its `MeshRef` against).

### 3.2 New/changed files by assembly

**`Agapanthe.Assets`**: `Scene/SceneDefinition.cs` (fields above), `Scene/
AgSceneFormat.cs` (`Version = 2`, v1 rejected with a re-cook message — same
pattern as `.agmodel`'s v1 rejection).

**`Agapanthe.Assets.Pipeline`** (cook-side): `Scene/SceneAuthoring.cs`
(`AuthoredPhysics` gains attractor fields — all three present or all three
absent, reject a partial spec; `AuthoredCamera` gains `MoveSpeed`/
`ShadowDistance`; `AuthoredEnvironment.Hdri` becomes nullable + new `bool
ProceduralSky`/`bool Black`, mutually exclusive with `Hdri`; new `AuthoredSystem`
+ `AuthoredScene.Systems`). `Scene/SceneTomlReader.cs` (new `[[system]]` reader
with a per-kind required/rejected key split, same `RejectUnknownKeys` style as
`[[grid]]`/`[[cluster]]`; `[physics]` reads `mu`/`attractor_center`/
`surface_radius`; `[camera]` reads `move_speed`/`shadow_distance`;
`[environment]` accepts `procedural_sky`/`black` booleans). `Scene/
SceneCompiler.cs` (`ToPhysics` attractor case; `ToCamera`'s `fixed` arm carries
the 2 new fields; new `ToSystem` resolving `ProbeModel`'s mesh/material the same
way `Place` resolves a bare entity, validating existence at cook time). New
`Procedural/` folder: `ProceduralGenerators.cs` (the cook-time-only
name→generator dispatch — `internal static Dictionary<string, Func<TomlTable,
string, ModelAsset>>`, unknown name → `AssetException`), `UvSphereGenerator.cs`
(body = `PlanetContent.BuildSphereModel` moved verbatim; TOML reads `radius`
required, `segments`/`rings` defaulted, plus the reduced material-factor subset
every existing call site actually uses: `base_color`/`metallic`/`roughness`/
`emissive`/`emissive_strength`/`name` — no image slots). New
`ProceduralTomlReader.cs` (parses `content/procedural/*.toml`: a top-level
`generator = "..."` key dispatched to the matching generator's own
`RejectUnknownKeys`-guarded reader). `TomlHelpers.cs` (extract
`SceneTomlReader`'s private scalar helpers to `internal static` — pure refactor).
`CookRunner.cs` (new `CookProcedural` phase before `CookScenes`, globs
`content/procedural/*.toml`, keys via `AssetKey.FromContentPath`, incrementality
= `SHA256(toml bytes)` alone, `AssetKind.Model`). `build/Agapanthe.Cook.targets`
(glob grows to include `content/procedural/**/*.toml`).

**`Agapanthe.Scene`** (GPU-free, cannot reference `Agapanthe.Engine`):
`SceneMaterializer.cs` (model-loading loop also loads every `SceneSystem.
ProbeModel`; `def.Systems` otherwise untouched here). No new type needed on
`MaterializeResult` — `result.Definition.Systems` is already the right shape for
the `Agapanthe.App`-side caller to construct concrete `ISystem`s from;
`SceneSystem` only names `AssetKey`/`Double3`, so it's `{Core, World, Assets}`-
clean.

**`Agapanthe.App`**: new `Scene/ISceneSystemFactory.cs`:

```csharp
public interface ISceneSystemFactory
{
    SceneSystemKind Kind { get; }
    Stage Stage { get; }  // Stage.Input for ProbeDrop, Stage.PostSimulation for LandingChallenge
    ISystem Create(SceneSystem spec, SimSceneContext sim, PresentationSceneContext presentation, MaterializeResult result);
}
```

`presentation` is non-nullable by design (D9). `IGame.cs` gains
`IReadOnlyList<ISceneSystemFactory> SceneSystems => [];` (default empty).
`SimSceneContext.cs` gains `required IReadOnlyList<ISceneSystemFactory>
SceneSystemFactories { get; init; }`, populated by `AppHost` from
`game.SceneSystems`. `Scene/SceneRecipe.cs` grows a dispatch loop after
`ClientScenePresenter.Apply`, throwing `InvalidOperationException` for a kind
with no registered factory, and before the loop entirely if `presentation is
null` and `def.Systems.Count > 0`. New `Scene/ProceduralSky.cs` (relocated
`BuildSkyEnvironment` body) and `Scene/BlackEnvironment.cs`. `Scene/
ClientScenePresenter.cs`'s `ApplyEnvironment` grows the two new arms. `Scene/
SceneCameraApplier.cs`'s `Fixed` branch copies `MoveSpeed`/`ShadowDistance`.

**Building a factory's runtime-spawned `ImportedEntitySpec` template** (spec
review finding — the piece that makes `planet-drop` actually spawn probes).
`ProbeDropSystem`/`LandingChallengeSystem` both need a GPU-resolved-handle
`ImportedEntitySpec` *template* (mesh/material handles, `RotationScale`,
`BoundsCenter`/`BoundsRadius`, `MeshRefKey` identity) to stamp a fresh `Position`
onto at each spawn — exactly the shape `SceneMaterializer`'s private `Compose` +
the `new ImportedEntitySpec(...)` call already build for a static entity
(`SceneMaterializer.cs:68-79`), just without a `SceneEntity`'s authored
placement (a probe's *scene* position is meaningless — it spawns wherever the
system computes at runtime). `SceneMaterializer` gains one new **public**
method, reusing the existing bounds/mesh-index validation:

```csharp
/// GPU-free template for a runtime-spawned drawable (a scene system's probe):
/// Mesh/Material left Invalid — the caller (a client-side ISceneSystemFactory)
/// resolves real handles via ResourceRegistry.ResolveMeshRef once, at
/// construction time, then reuses the same template for every spawn.
public static ImportedEntitySpec BuildRuntimeTemplate(
    AssetKey model, int localMesh, int localMat, IReadOnlyDictionary<AssetKey, ModelAsset> models)
```

It looks up `models[model].Meshes[localMesh]` (bounds-checked identically to the
entity path), zeroes the mesh's `WorldTransform` translation into a
`RotationScale`, and returns `ImportedEntitySpec(MeshHandle.Invalid,
MaterialHandle.Invalid, Double3.Zero, rotationScale, mesh.BoundsCenter,
mesh.BoundsRadius, order: 0, new MeshRefKey(model, localMesh, localMat))` —
`Position`/`Order` are placeholders the caller overwrites (a probe's `Order`
doesn't need to be a stable draw-sort key the way a static entity's does,
since `PersistentInstanceBuffer`/`InstanceSlot` are re-assigned on the next
structural rebuild regardless — see `GameWorld.RebuildPersistent`). An
`ISceneSystemFactory.Create` implementation then does exactly what
`DriveSceneRecipe`/`PlanetContent.SetupPlanetDrop` do today by hand:
```csharp
var template = SceneMaterializer.BuildRuntimeTemplate(spec.ProbeModel, spec.ProbeLocalMesh, spec.ProbeLocalMat, result.Models);
var (mesh, material) = presentation.Registry.ResolveMeshRef(spec.ProbeModel, spec.ProbeLocalMesh, spec.ProbeLocalMat);
var probeSpec = new ImportedEntitySpec(mesh, material, template.Position, template.RotationScale,
    template.BoundsCenter, template.BoundsRadius, template.Order, template.Identity);
return new ProbeDropSystem(sim.World, in probeSpec, spec.Centre, spec.ProbeRadius, spec.Every);
```
(`result.Models[spec.ProbeModel]` is guaranteed populated — §3.1's key-table
change ensures `SceneMaterializer.Materialize` loads every `SceneSystem.
ProbeModel`; `ResolveMeshRef` is guaranteed to succeed because
`ClientScenePresenter.Apply` already uploaded every entry of `result.Models`,
including probe-only ones, before the system-dispatch loop runs.)

**Single-slot `SimulationHost.ApplyCommand`/`InputMap`/`SampleInput`** (spec
review finding). These three are plain settable properties on `SimulationHost`
(`src/Agapanthe.Engine/SimulationHost.cs`), not multicast — D3's "each factory
wires its own input" implicitly assumes at most one factory per scene ever sets
them, which holds for every scene in 3c-1/3c-2/3c-3 (each declares exactly one
`[[system]]`), but nothing enforces it. Each factory that wires input (`Probe
DropSystemFactory` now, `LandingChallengeSystemFactory` in 3c-2) MUST guard with
`if (sim.Simulation.ApplyCommand is not null) throw new InvalidOperationException(
"a scene may declare at most one input-wiring system today")` before assigning —
a cheap, explicit fail-loud rather than a silent second-factory clobber of the
first's delegate. This is a real, acknowledged limitation of the two-registry
design (D3), not fixed here — flagged so a future 3-system scene fails fast
instead of silently losing input for one of its systems.

**`samples/HeadlessSim/Program.cs`**: `RunScene` gains, right after
`SceneLoader.LoadHeadless` returns, a hard check on `result.Definition.Systems.
Count > 0` — named-system error + `return 1`.

**`samples/Sandbox/`** (3c-1 slice): new `Systems/ProbeDropSystemFactory.cs`
implementing `ISceneSystemFactory`; `SandboxGame.cs` gains `new
SceneRecipe("planet"), new SceneRecipe("planet-drop")` in `Scenes` and
`SceneSystems => [new ProbeDropSystemFactory()]`; `content/procedural/
{planet-surface,sun-surface,probe}.toml` + `content/scenes/{planet,
planet-drop}.toml` authored. Deleted in 3c-1: `Scenes/PlanetSceneRecipe.cs`,
`Scenes/PlanetDropSceneRecipe.cs`. Not yet deleted (still needed by
`planet-challenge`/`drive` until 3c-2/3c-3): `Content/PlanetContent.cs`
(shrinks), `Content/PlanetStage.cs`, `Cameras/SandboxCameras.cs` (loses
`FramePlanetCamera`/`FramePlanetDropCamera`), `Content/ModelContent.cs`'s
`ResolveModelKey`.

### 3.3 Worked TOML example (`planet-drop`)

`content/procedural/planet-surface.toml`:
```toml
generator = "uv_sphere"
radius = 3185500.0        # 6_371_000 / 2 (matches today's default, half-scale)
segments = 128
rings = 64
base_color = [0.16, 0.42, 0.62, 1.0]
metallic = 0.0
roughness = 0.9
name = "PlanetSurface"
```

`content/scenes/planet-drop.toml` (numbers illustrative — actual values baked at
execution time by running the existing framer/setup formulas once, not
hand-derived):
```toml
name = "planet-drop"

[[entity]]
model = "procedural/planet-surface"
casts_shadow = false

[[entity]]
model = "procedural/sun-surface"
position = [<baked sun position>]
casts_shadow = false

[light]
kind = "point"
position = [<same as sun>]
color = [1.0, 0.97, 0.92]
intensity = <baked targetIrradiance*dist^2>

ambient = [0.0, 0.0, 0.0]

[environment]
black = true

[camera]
mode = "fixed"
position = [<baked eye>]
yaw = <baked>
pitch = <baked>
fov_y = 70.0
near = 1.0
far = <baked>
move_speed = 20.0
shadow_distance = 1.0
free_fly = true

[physics]
gravity = [0.0, 0.0, 0.0]
ground_y = 0.0
mu = 2.030e14
attractor_center = [0.0, 0.0, 0.0]
surface_radius = 3185500.0

[[system]]
kind = "probe_drop"
probe_model = "procedural/probe"
probe_radius = 3.0
centre = [0.0, 3185620.0, 0.0]
every = 30
```

Every "baked" number must come from actually running the *existing*
`SandboxCameras.FramePlanetDropCamera`/`PlanetContent.SetupPlanetDrop` formulas
once (a throwaway script or a one-off unit test dumping the numbers) before the
old code is deleted, so the migrated scene's capture stays traceable.

### 3.4 `renderer.ClearColor` — confirmed irrelevant under `Black`

`PlanetStage.Build` sets `renderer.ClearColor = (0,0,0,1)` (`PlanetStage.cs:55`,
overriding the default `(0.02,0.02,0.05,1)`, `Renderer.cs:493`) alongside
`BuildBlackEnvironment()`. Checked against `Renderer.DrawFrame`
(`Renderer.cs:1132-1228`): the color attachment is cleared to `ClearColor`, mesh
geometry draws, then the **skybox pass draws last inside the same render pass**,
filling every background pixel (anywhere depth is still at the far plane) with
the environment cubemap sampled from whatever `SetEnvironment` last provided.
With `SceneEnvironmentMode.Black`'s all-zero HDR environment, the skybox paints
the entire background black regardless of `ClearColor` — the two produce the
identical visible result. `SceneEnvironment`/`BlackEnvironment.Build()` need no
`ClearColor` field; `ClientScenePresenter` does not touch `renderer.ClearColor`
at all for any environment mode (it keeps whatever default/prior value it had —
invisible once `SetEnvironment` runs, for every mode, not just `Black`).

### 3.5 Env vars dropped by the `planet`/`planet-drop` migration

Per D6's "concrete scene, no parametric CLI/env overrides" (already applied to
every scene since 3b), the following become fixed authored numbers in
`content/scenes/{planet,planet-drop}.toml` / `content/procedural/*.toml` and are
**not** read anywhere after 3c-1 lands (no fallback env-var override is kept —
symmetric with `model`/`grid`/`drop`/`metalrough` having none since 3b):

| Env var | Was read in | Becomes |
|---|---|---|
| `AGAPANTHE_PLANET_RADIUS` | `PlanetContent.SetupPlanetScene` | `procedural/planet-surface.toml`'s `radius` |
| `AGAPANTHE_SUN_RADIUS` | `PlanetContent.SetupPlanetScene` | `procedural/sun-surface.toml`'s `radius` |
| `AGAPANTHE_SUN_DISTANCE` | `PlanetContent.SetupPlanetScene` | baked into the sun entity's authored `position` |
| `AGAPANTHE_SUN_DIR` | `PlanetContent.SetupPlanetScene` | baked into the sun entity's authored `position` + the light's `direction` |
| `AGAPANTHE_PLANET_PHASE` | `SandboxCameras.FramePlanetCamera` | baked into `planet.toml`'s `[camera]` fixed `position`/`yaw`/`pitch` |
| `AGAPANTHE_PLANET_ALT` | `SandboxCameras.FramePlanetCamera` | baked into `planet.toml`'s `[camera]` fixed `position` |
| `AGAPANTHE_DROP_EVERY` | `PlanetDropSceneRecipe` | `[[system]] every` |
| `AGAPANTHE_DROP_SUN_OFF` | `SandboxCameras.FramePlanetDropCamera` | baked into `planet-drop.toml`'s `[camera]` fixed pose |
| `AGAPANTHE_DROP_CAM_BACK` | `SandboxCameras.FramePlanetDropCamera` | baked into `planet-drop.toml`'s `[camera]` fixed pose |
| `AGAPANTHE_DROP_CAM_HEIGHT` | `SandboxCameras.FramePlanetDropCamera` | baked into `planet-drop.toml`'s `[camera]` fixed pose |

**Shared with `planet-challenge` — stay live until 3c-2** (spec review
iteration 2 caught the original table overclaiming these as fully dropped:
`SetupPlanetChallenge`/`FramePlanetChallengeCamera`, not deleted in 3c-1, still
read them for the not-yet-migrated `planet-challenge` scene):

| Env var | Was read in | 3c-1 fate | 3c-2 fate |
|---|---|---|---|
| `AGAPANTHE_PLANET_FOV` | all 3 planet camera framers | unused by `planet`/`planet-drop` (baked) | remains live for `planet-challenge` |
| `AGAPANTHE_PLANET_MU` | `PlanetContent.SetupPlanetDrop`/`SetupPlanetChallenge` | unused by `planet-drop` (baked) | remains live for `planet-challenge` |
| `AGAPANTHE_PROBE_RADIUS` | `PlanetContent.SetupPlanetDrop`/`SetupPlanetChallenge` | unused by `planet-drop` (baked) | remains live for `planet-challenge` |
| `AGAPANTHE_DROP_HEIGHT` | `PlanetContent.SetupPlanetDrop`/`SetupPlanetChallenge` | unused by `planet-drop` (baked) | remains live for `planet-challenge` |

**`AGAPANTHE_INPUT_DEBUG`** (`RecipeInput.WireProbeKey`/`WireFreeFly`'s verbose
free-fly logging branch, `RecipeInput.cs:28`) — the migrated `planet`/
`planet-drop` route free-fly through `SceneInput.EnableFreeFly`
(`src/Agapanthe.App/Scene/SceneInput.cs`), which has no debug-logging branch.
This var silently stops applying to `planet`/`planet-drop` in 3c-1 (still
applies to not-yet-migrated `drive`/`planet-challenge` via the old
`RecipeInput.WireFreeFly`). Accepted as a minor dev-tooling regression, not
ported — porting it belongs with `SceneInput` growing a debug mode generally,
out of scope here.

`AGAPANTHE_WORLD_ORIGIN` is unaffected (it's a host/recipe-level override
already orthogonal to scene content, kept as-is — same as every other scene
family). `AGAPANTHE_LOAD`'s special-case in `PlanetSceneRecipe.Matches` (also
claims a bare/no-token run when a load path is set) is dropped along with the
recipe itself — `SceneRecipe`'s default name-equality `Matches` is sufficient
once `planet`/`planet-drop` are ordinary named scenes; a restore request for
either still flows through the existing `SceneSystem`/`SceneRestore`→
`sim.RequestRestore` path (unaffected by this migration).

## 4. Error handling

- Malformed `.agscene` v2 payloads → `AgSceneException`, same bounds-checked
  `Reader` pattern as v1 (per the Contenu-3b audit hardening: every count bounded
  by a `minRecordBytes` divisor before it drives a `new T[]` allocation).
- A v1 `.agscene` blob → `AgSceneException` naming the version mismatch and
  telling the caller to re-cook (same message shape as `.agmodel`'s v1
  rejection).
- Cook-time: an authored `[physics]` with only some of `mu`/`attractor_center`/
  `surface_radius` present → `AssetException` naming the file (reject a partial
  attractor spec, don't guess defaults for the missing pieces).
- Cook-time: `[[system]]` with keys belonging to the other kind (e.g. `zone_
  radius` on a `probe_drop` system) → `AssetException` (per-kind `Reject
  UnknownKeys`).
- Cook-time: a `[[system]]`'s `probe_model` unresolvable, or its
  `probe_local_mesh`/`probe_local_mat` out of range for that model → `Asset
  Exception` at cook time (a genuine correctness improvement over today's
  runtime-only discovery).
- Runtime: `SceneRecipe.Build` throws `InvalidOperationException` if a scene
  declares a `SceneSystemKind` with no registered `ISceneSystemFactory`, or if
  `def.Systems.Count > 0` and `presentation is null`.
- `HeadlessSim.RunScene`: exit 1 with a named-system message (D9) rather than a
  silent partial run.

## 5. Testing strategy

### 5.1 Deterministic gates
- `dotnet build Agapanthe.slnx` 0 warning.
- `dotnet test` green — existing suite + new tests below.
- Sandbox + HeadlessSim + `AotComponentProbe` JIT == NativeAOT, 0 leak, 0
  validation message.
- `AGAPANTHE_SCENE={planet,planet-drop} AGAPANTHE_MAX_FRAMES=2` captures — new
  MD5s pinned + human visual verdict.
- `dotnet run --project samples/HeadlessSim -- --scene planet-drop` → exit 1
  with the named-system error (confirms D9 in code, not just in the design).
- `model`/`grid`/`drop`/`metalrough`/`headless-default` captures/snapshots —
  byte-identical (this milestone touches no code path they exercise beyond the
  `.agscene` version bump, which forces their re-cook but not their content).

### 5.2 New tests (3c-1)
- `AgSceneFormatTests`: v2 round-trip (attractor present/absent, `Fixed` camera
  with `MoveSpeed`/`ShadowDistance`, `ProceduralSky`/`Black` environment modes,
  a `ProbeDrop` system entry), v1 rejection, forged-count/truncation negatives
  extended to the new blocks (mirroring 3b's post-audit coverage).
- `SceneTomlReaderTests`: `[[system]] kind="probe_drop"` happy path, unknown
  key per-kind rejection, partial-attractor rejection, mutually-exclusive
  `hdri`/`procedural_sky`/`black` rejection.
- `SceneCompilerTests`: `ToPhysics` attractor translation, `ToSystem`
  mesh/material validation (happy + out-of-range).
- `SceneMaterializerTests`: a `SceneSystem.ProbeModel` gets loaded into
  `MaterializeResult.Models` even with no matching `SceneEntity`.
- A cook-side generator test: `UvSphereGenerator.Build` produces a `ModelAsset`
  matching `PlanetContent.BuildSphereModel`'s output for the same params (the
  "copied verbatim" claim, verified rather than assumed — Contenu-3b's own
  audit flagged an unverified verbatim-copy claim as a finding; this test
  closes that class of gap proactively).
- `ProbeDropSystemFactoryTests` (or an `AppHostContractTests`-style test): the
  registry dispatch throws for an unregistered kind, constructs correctly for a
  registered one.

### 5.3 Double audit + human visual verdict
Same pattern as every prior Contenu sub-milestone: `csharp-lowlevel` +
`engine-architect` dispatched after 3c-1's implementation, before the phase
gate; findings applied or explicitly deferred with rationale. Human visual
verdict on `planet` + `planet-drop` captures required before closing 3c-1.

## 6. Migration path

1. **3c-1** (this session's task board): format v2 + both registries + `planet`/
   `planet-drop` migration, as detailed above.
2. **3c-2** (future board, gated on 3c-1's close): `SceneSystemKind.
   LandingChallenge` + `LandingChallengeSystemFactory` + `SceneSystem.
   QuicksavePath` wiring + `planet-challenge` migration.
3. **3c-3** (future board, gated on 3c-2's close): `ground_quad`/grass-image
   generator + `drive` migration (dropping its CLI arg per D6) + deletion of
   now-fully-dead Sandbox code (`SandboxCameras`, `PlanetContent`,
   `PlanetStage`, `RecipeInput`, `ModelContent.ResolveModelKey`, and
   `BenchSpinSystem`/`ChurnSystem` if confirmed to have zero remaining callers
   at that point).

Each phase closes with the standard gate: build/test green, captures re-pinned
+ human visual verdict, double audit, docs converged, board archived — exactly
the 3a/3b pattern.

## 7. Out of scope (this milestone and deferred)

Procedural environment → real `.agenv`/`AssetKind.Environment` (Contenu-2b) ·
entity name→`GlobalId` lookup (no consumer, dropped per D8) · runtime prefab
instantiation (still no consumer, 3b's D8 holds) · `Agapanthe.App`→`App`/
`App.Client` split and the `MaterializeResult` wiring dedup (3b-deferred,
unaffected here) · `ResourceRegistry.Unload`'s zero-caller gap (still no reload
path; re-deferred, not silently dropped) · `AssetRef[]` GC-cost measurement
(unaffected) · a generic "command-handler" registry (rejected, D3).

## 8. Outcome (3c-1, closed 2026-09-12)

Delivered exactly as scoped in §1-7 above. Board: `.absolute-work/board.md`
(archived to `.absolute-work/archive/board-contenu3c1.md`). 8 waves, 29 tasks.

**Gates**: 780 tests (778 + 2 audit-driven regression tests), 0 warning, 0 leak,
0 validation. `planet`/`planet-drop` captures pinned + human visual verdict
PASS (`99e2f4a345a75a51d0c498c17ed08908` / `81ddf07438af72f7b828bc0acbc5c2e2`).
`model`/`grid`/`drop`/`metalrough`/`headless-default`/`planet-challenge`/
`drive` all re-verified unaffected. `HeadlessSim --scene planet-drop` → exit 1
confirmed (D9), including on the AOT binary. Sandbox + HeadlessSim +
`AotComponentProbe` JIT == NativeAOT on every capture/snapshot, re-confirmed
after the audit fixes below.

**Double audit** (`csharp-lowlevel` + `engine-architect`, dispatched in
parallel against the full diff): PASS-with-concerns both. **1 🔴 found
independently by both and fixed**: `SceneMaterializer.Materialize` parsed,
compiled, and round-tripped the attractor (`Mu`/`AttractorCenter`/
`SurfaceRadius`) through the entire pipeline but never called `PhysicsSettings.
WithAttractor(...)` — always took the 3-arg uniform-gravity path, silently
giving `planet-drop` **zero gravity**. Invisible in the pinned capture (no
probe is ever visible there — it only spawns on a `B` keypress), so the human
visual verdict could not have caught it; found only by the audit. Fixed +
2 regression tests added to `SceneMaterializerTests`
(`Materialize_AttractorPhysics_AppliesWithAttractor`,
`Materialize_MuZero_TakesTheUniformGravityPath_ByteIdenticalToBefore`) —
csharp-lowlevel had explicitly named this exact missing test tier
(`ScenePhysics → PhysicsSettings`).

4 additional 🟠 findings applied: missing `localMat` bounds check in
`BuildRuntimeTemplate` (mirrors the pre-existing entity-path check) ·
`CookProcedural`/`CookScenes` blindly sliced a `.toml` suffix — a Win32
wildcard near-miss extension could silently mis-key a file — fixed with a
`StripTomlExtension` helper that verifies the suffix first · `SimSceneContext`
would have transitively named a GPU-coupled type via `ISceneSystemFactory.
Create`'s own signature — `SceneSystemFactories` moved from `SimSceneContext`
to `PresentationSceneContext` · the single-slot `SimulationHost.ApplyCommand`
guard was duplicated per-factory (easy to forget in a future factory) —
hoisted into `SceneRecipe`'s dispatch loop. A 5th finding (`AGAPANTHE_LOAD`
silently ignored for any cooked `SceneRecipe` scene with no `[restore]` block)
turned out to be true since Contenu-3b, not a 3c-1 regression — this doc's
§3.5 "unaffected" claim was simply inaccurate, not something 3c-1 broke; fixed
with a `Log.Warn` anyway, matching `DriveSceneRecipe`'s existing precedent.

Several 🟡 nits from both audits (`ProbeRadius`/`Every` validation,
`MoveSpeed`/`ShadowDistance` sentinel-semantics documentation, duplicate-`Kind`
factory silently picking the first match, `ApplyEnvironment` missing an
explicit `default` arm, committing the `bake_planet.cs` derivation script for
traceability) were deferred to 3c-2/3c-3's Deferred Work rather than expanding
this board.

**Deviations from this spec, all confirmed correct in review**: `SceneSystem`
declares only `SceneSystemKind.ProbeDrop` — the `LandingChallenge`-only fields
sketched in §3.1's binary layout were *not* pre-declared (YAGNI; the codebase's
own precedent is version-bump-when-a-real-consumer-exists, not
speculative reservation). Baked camera/light numeric literals were derived via
a throwaway script reproducing the *exact* original formulas rather than
attempting bit-identical reproduction of the old code's float-rounding quirks
— correct, since no capture hash existed for `planet`/`planet-drop` before
this migration to be bit-faithful to; the actual verification gate was always
"new MD5 + human visual verdict," exactly as this spec's §5 promised.

3c-2 (`LandingChallenge` + `planet-challenge`) and 3c-3 (`ground_quad` +
`drive` + cleanup) remain to be decomposed into their own task boards per the
phase gate — not started.

## 9. 3c-2 design (concrete, decomposed 2026-09-12)

3c-1 deliberately did not pre-declare `LandingChallenge`'s fields on `SceneSystem`
(YAGNI, confirmed correct by both audits). 3c-2 is that real consumer arriving —
this section is the concrete design, superseding this doc's earlier speculative
sketch of the same fields.

### `.agscene` v3

`SceneSystemKind` gains `LandingChallenge = 1`. `SceneSystem` gains, additive to
the existing `ProbeModel`/`ProbeLocalMesh`/`ProbeLocalMat`/`ProbeRadius` (reused
verbatim — `LandingChallenge` spawns the same kind of probe as `ProbeDrop`, just
command-driven and aimed instead of periodic and fixed-centre):

```csharp
public sealed record SceneSystem
{
    // existing: Kind, ProbeModel, ProbeLocalMesh, ProbeLocalMat, ProbeRadius
    // ProbeDrop
    public int Every { get; init; }
    public Double3 Centre { get; init; }
    // LandingChallenge
    public Double3 ZoneCenter { get; init; }
    public double ZoneRadius { get; init; }
    public double SurfaceBand { get; init; }
    public double DropHeight { get; init; }
    public int TargetCount { get; init; }
    public int ShotBudget { get; init; }
    public string QuicksavePath { get; init; } = "";
}
```

**Deliberately NOT duplicated**: `AttractorCenter`/`SurfaceRadius` — `Landing
ChallengeSystemFactory` reads them from `MaterializeResult.Physics.Value`
(already carried by the scene's `[physics]` block, added in 3c-1); a
`LandingChallenge` system without an attractor is a scene-authoring error,
enforced by the factory throwing if `result.Physics is null`. The beacon is a
plain drawable `[[entity]]` (an emissive sphere at a cook-time-computed
position) — no new field, `SceneMaterializer` already spawns it like any other
entity.

Binary layout appended to `[[system]]`'s existing record (`kind:u8=1`) after the
shared probe fields: `zoneCenter:f64×3 | zoneRadius:f64 | surfaceBand:f64 |
dropHeight:f64 | targetCount:i32 | shotBudget:i32 | quicksavePathLen:u16 +
UTF-8`. `AgSceneFormat.Version = 3`, v1/v2 rejected with a re-cook message
(same precedent as v1→v2).

### `LandingChallengeSystemFactory` (`samples/Sandbox/Systems/`)

Mirrors `ProbeDropSystemFactory`'s shape: `Kind => LandingChallenge`, `Stage =>
Stage.PostSimulation`. `Create` resolves the probe template the same way
(`BuildRuntimeTemplate` → `ResolveMeshRef`), reads `attractorCenter`/
`surfaceRadius` off `result.Physics!.Value` (throws if null), constructs the
existing `LandingChallengeSystem` unchanged (it already takes exactly these
parameters — zero changes needed to that class), wires `ApplyCommand` →
`challenge.TryShoot(cmd.Vector)` on `RecipeInput.SpawnProbeCommandKind` (shared
constant, shared with `ProbeDropSystemFactory` — never a conflict, a scene
never declares both kinds), calls `RecipeInput.WireProbeKey` (unchanged, still
used by both factories), and wires the `F5` quicksave handler using
`spec.QuicksavePath` (falls back to `"challenge.save"` if empty) — this is
where `SceneSystem.QuicksavePath` closes D7 (the `AGAPANTHE_SAVE` host-level
vs. F5-quicksave name collision), moving the quicksave path from an env-var
read inline in the old recipe to authored scene data.

### Camera + content

`planet-challenge`'s camera is exactly as deterministic as `planet`/`planet-
drop`'s were in 3c-1 (`FramePlanetChallengeCamera` computes eye/yaw/pitch/fov/
near/far/moveSpeed/shadowDistance from scene constants + the beacon's baked
position) — collapses into `SceneCamera.Fixed` the same way, zero new runtime
camera logic. `content/scenes/planet-challenge.toml` = `planet.toml`'s shape
(same procedural planet/sun spheres, same physics attractor, same black env)
plus a beacon entity, a probe procedural asset (shared `content/procedural/
probe.toml` from 3c-1), and the `[[system]]` block. Numbers baked via the same
throwaway-script approach as 3c-1 (§3c-1 Wave 7), reproducing `PlanetContent.
SetupPlanetChallenge`/`FramePlanetChallengeCamera`'s exact formulas at their
env-var defaults.

### Cleanup (end of 3c-2, not deferred to 3c-3)

Once `planet-challenge` migrates, `PlanetStage`, `PlanetContent.
SetupPlanetScene`/`SetupPlanetChallenge`/`BuildSphereModel`/
`BuildBlackEnvironment`/`BuildProbeSphere`, and `SandboxCameras.
FramePlanetChallengeCamera` all reach **zero remaining callers** (verified by
grep before deletion) — deleted in this phase, not 3c-3, since "delete once
dead" doesn't need to wait for the unrelated `drive` migration. `drive`'s own
dead code (`ModelContent.ResolveModelKey`, its CLI arg, `SandboxCameras.
FrameCamera`/`SetupLights`/`NarrowBounds`) stays out of scope here — those
still have `drive` as a live caller until 3c-3.

## 10. Outcome (3c-2, closed 2026-09-12)

Delivered as scoped in §9, plus one unplanned cross-cutting fix (see below). Board:
`.absolute-work/board.md` (archived to `.absolute-work/archive/board-contenu3c2.md`). 5
waves, 16 tasks (BW-001 through BW-016).

**Gates**: 797 tests (788 close-of-3c-1 baseline + 9 this phase), 0 warning, 0 leak, 0
validation. `planet-challenge` capture pinned + human visual verdict PASS
(`ea6ba9101b972940bf4ed00fb6d5e25c`) — planet+sun+beacon visible, camera aimed
correctly. All 6 other `SceneRecipe` scenes (`model`/`grid`/`drop`/`metalrough`/
`planet`/`planet-drop`) re-verified byte-identical after 2 forced full re-cooks
(the `.agscene` v3 bump, then again after the resume fix). `drive` unaffected.
`HeadlessSim --scene planet-challenge` → exit 1 confirmed (D9, message correctly
names `LandingChallenge`), on both JIT and a NativeAOT publish. Sandbox + HeadlessSim
JIT == NativeAOT on every capture/snapshot.

**Double audit** (`csharp-lowlevel` + `engine-architect`, parallel, full diff):
PASS-with-concerns both, **1 🔴 found independently by both, fixed** — see below. 6
additional 🟠 findings applied (weak attractor guard mirroring the wrong invariant,
missing `AttractorSurfaceRadius > 0` cook-time check, no numeric validation on the new
`landing_challenge` fields — negative counts reached a raw `OverflowException`, NaN
made the challenge silently unwinnable —, an unvalidated quicksave path taken
verbatim from a cooked blob, an un-bumped `CookerVersion` that disarmed the
incremental-recook safety net, and a stale comment asserting a now-false invariant).
2 🟡 findings noted and deferred to 3c-3 (the shared `ProbeModel`/`ProbeRadius` head
of `SceneSystem` being `required` will force a future non-spawning kind to author a
dummy probe; `world_origin` applies to entities but not to
`AttractorCenter`/`Centre`/`ZoneCenter`, inert today since every scene uses a zero
origin).

**The 🔴, and why it became a design change rather than a local patch**: `AGAPANTHE_
LOAD`/F5-quicksave resume for `planet-challenge` was silently broken by this
migration. The deleted hand-coded `PlanetStage.Build` passed `spawnEntities: !
loadMode` to keep the world empty in load mode (`GameWorld.Load` hard-throws on a
non-empty world); `SceneRecipe`/`SceneMaterializer` had no equivalent — they always
spawn every `[[entity]]` unconditionally. This was not a `planet-challenge`-local
bug: the identical mechanism failure has affected `planet-drop` since 3c-1, unnoticed
because `planet-drop` never had a human-verified resume feature the way
`planet-challenge`'s F5 did. Both audits flagged this as blocking; given the fix
necessarily touches `SceneMaterializer`/`SceneLoader`/`SceneRecipe` — used by every
`SceneRecipe`-driven scene, not just this one — it was presented to the user as a
genuine design decision (options: fix now, accept as scoped debt like D6, or partially
revert) rather than resolved unilaterally. **The user chose to fix it now** (an
additional sub-wave, BW-015b): `SceneMaterializer.Materialize` gained `bool
spawnEntities = true`; `SceneRecipe.Build` computes it from `sim.Options.LoadPath`
and, when a load is requested, restores from that runtime path directly (taking
priority over any scene-authored `[restore]` block — no scene uses one, and a
fixed cook-time path could never match a runtime `AGAPANTHE_LOAD` override anyway).
Live-verified end-to-end on both `planet-challenge` and `planet-drop`, on JIT and
NativeAOT: save 3/2 entities, relaunch with `AGAPANTHE_LOAD`, world stays empty
through cook-time spawn, then restores correctly from the snapshot. All 7 pinned
captures re-confirmed unaffected (the fix only activates when `AGAPANTHE_LOAD` is
set, which no pinned capture does).

**Deviations from this spec's §9, all confirmed correct in review**: none beyond the
unplanned §10 fix above — the LandingChallenge field layout, factory shape, and
content authoring all shipped exactly as designed in §9.

3c-3 (`ground_quad` generator + `drive` migration + final Sandbox cleanup) remains to
be decomposed into its own task board per the phase gate — not started.

## 11. 3c-3 design (concrete, decomposed 2026-09-12)

Final gated sub-phase: `ground_quad` generator + `drive` migration + Sandbox cleanup. Unlike
3c-2 (which reused 3c-1's `SceneSystem` shape almost verbatim), `drive`'s input model —
continuous axis-vector movement steering an already-spawned body, not a periodic or
command-triggered spawn — does not fit the "probe" shape at all. This is exactly the moment
3c-2's audit-deferred debt (F2: the shared `ProbeModel`/`ProbeRadius` head being `required`
will force a future non-spawning kind to author a dummy model; F4: the `ApplyCommand`-only
guard doesn't cover `InputMap`/`SampleInput`) becomes live, so both are closed here rather
than deferred again.

### `ground_quad` generator (cook-time, no format bump)

`GroundQuadGenerator.Build` — `ModelContent.BuildGroundModel`/`BuildGrassImage` moved
verbatim into `Agapanthe.Assets.Pipeline/Procedural/GroundQuadGenerator.cs`, registered in
`ProceduralGenerators.ByName["ground_quad"]`. TOML: `generator = "ground_quad"`, `size`
(required float) — the old runtime code sized the ground dynamically from the loaded
model's `AggregateBounds`; since D6 fixes `drive` on `models/DamagedHelmet.glb`, the size
becomes a baked constant (derived via the same throwaway-script approach, reproducing
`groundSize = max((span * 2.5) + (extent.Y * 8.0), 40.0)` at `DamagedHelmet.glb`'s actual
bounds). `drive`'s zero-gravity physics, `ProceduralSky` environment, and deterministic
`Fixed` camera all fit the **existing** v3 format — no bump needed for those.

### `SceneSystemKind.DriveControl` — format bump to v4, 2 structural changes

**Change 1 (closes 3c-2 audit finding F2)**: `SceneSystem.ProbeModel`/`ProbeLocalMesh`/
`ProbeLocalMat`/`ProbeRadius` go from `required` to defaulted (`AssetKey.None`/`0`/`0`/
`0f`) — `DriveControl` spawns nothing at runtime, so authoring a dummy probe model to
satisfy `required` would be exactly the anti-pattern F2 warned about.
`SceneMaterializer.Materialize`'s system-model-loading loop skips a `None` `ProbeModel`
(`if (!system.ProbeModel.IsNone && !models.ContainsKey(...))`).

**Change 2**: `SceneSystem` gains `int ControlledEntityIndex` (default 0) and `float
MoveSpeed` (was the hardcoded `DriveMoveSpeed = 6f` constant in `DriveSceneRecipe`, now
authored). `ControlledEntityIndex` names which of `def.Entities` (the flat, cook-time-
ordered list) this system steers — `drive.toml` has exactly one `[[entity]]` with a
`[body]` block, at index 0.

**`MaterializeResult` gains `IReadOnlyList<EntityRef?> SpawnedEntities`** (parallel to
`def.Entities`; `null` at an index where spawn was skipped for a pending restore —
reuses 3c-2's `spawnEntities` mechanism, so `DriveControl` also correctly no-ops during
a resume until the restore populates the world). `SceneMaterializer.Materialize` captures
`GameWorld.SpawnBody`'s return value (an `EntityRef`, previously discarded) into this
list instead of just calling it for effect.

### `DriveControlSystemFactory` (client)

`Kind => DriveControl`, `Stage => Stage.Input` (mirrors `ProbeDropSystemFactory` — this
also polls a `SampleInput` callback every tick). `Create` resolves `result.
SpawnedEntities[spec.ControlledEntityIndex]` (throws if `null`/out of range — a restore
not yet applied when this runs would be a materializer ordering bug, not an authoring
mistake, same posture as `LandingChallengeSystemFactory`'s attractor guard), constructs
the exact `InputMap`/`SampleInput`/`ApplyCommand` wiring `DriveSceneRecipe` has today
(axis-vector `BindAxisVector`, brake `BindButton(OnPress)`, `X`-key edge tracked in a
small mutable field), scaled by `spec.MoveSpeed`.

### Guard widening (closes 3c-2 audit finding F4)

`SceneRecipe.Build`'s dispatch loop currently snapshots only `sim.Simulation.ApplyCommand`
before/after each factory's `Create` call. `DriveControlSystemFactory` is the first factory
to also assign `InputMap`/`SampleInput` — both are single-slot on `SimulationHost`, same as
`ApplyCommand`. The guard is widened to snapshot and compare all three, throwing the same
"more than one input-wiring system" `InvalidOperationException` if any of them changed
after a `priorX is not null`. No scene declares two input-wiring systems today (drive never
coexists with ProbeDrop/LandingChallenge), so this is defence-in-depth exercised for real
by the format bump, not by a live conflict.

### Content

`content/procedural/ground.toml` (`ground_quad`, baked `size`). `content/scenes/drive.toml`
— `models/DamagedHelmet.glb` fixed (D6: the CLI arbitrary-model arg is dropped, a confirmed,
already-accepted capability loss), baked `Fixed` camera + 2 point lights (from
`SandboxCameras.FrameCamera`/`SetupLights`'s deterministic formulas at the Helmet's actual
bounds — same throwaway-script derivation as every prior migration), `[environment]
procedural_sky = true`, `[physics] gravity = [0,0,0] ground_y = -100000`, `[[system]] kind
= "drive_control" move_speed = 6.0 controlled_entity_index = 0`.

### Cleanup (grep-verified 0-caller before deletion)

`DriveSceneRecipe.cs` deleted. `ModelContent.cs` becomes **fully dead** and is deleted
whole (`ResolveModelKey`/`LogModelStats`/`BuildGroundModel`/`BuildSkyEnvironment`/
`BuildGrassImage` lose their only caller; `ParseGrid`/`ParseDrop`/`ModelDiagonal` were
**already** 0-caller since Contenu-3b's `ModelSceneRecipe` deletion — pre-existing debt
swept here, not new this phase). `SandboxCameras.cs` becomes fully dead and is deleted
whole (`FrameCamera`/`SetupLights`/`NarrowBounds` lose their only caller). `RecipeInput.
WireFreeFly` was already 0-caller (superseded by `Agapanthe.App.Scene.SceneInput.
EnableFreeFly` back in Contenu-3b) — deleted here as the same kind of pre-existing sweep.
`BenchSpinSystem.cs`/`ChurnSystem.cs` confirmed 0-caller (never wired into any recipe,
pre-existing) — deleted per the original 3c-3 plan's "if confirmed dead" clause.

### Deferred (unchanged from 3c-1/3c-2)

Procedural environment as a real cooked `.agenv` (Contenu-2b) · entity name→`GlobalId`
lookup (D8, no consumer) · `world_origin` not applied to `AttractorCenter`/`Centre`/
`ZoneCenter` (3c-1/3c-2 debt, still inert while every scene uses a zero origin — `drive`
also uses a zero origin, does not widen this) · `ProbeRadius`/`Every` validation (3c-1
debt, `DriveControl` doesn't touch these fields at all).

## 12. Outcome (3c-3, closed 2026-09-12) — CLOSES Contenu-3c (3/3), the domain "declarative scenes" effort finishes here

Delivered as designed in §11, plus fixes from the double audit (below). Board:
`.absolute-work/board.md` (archived to `.absolute-work/archive/board-contenu3c3.md`). 6
waves, 20 tasks (CW-001 through CW-020).

**Every Sandbox scene now runs on cooked `SceneRecipe` data — zero hand-coded recipes
remain.** `model`/`grid`/`drop`/`metalrough` (Contenu-3b), `planet`/`planet-drop`
(3c-1), `planet-challenge` (3c-2), `drive` (3c-3): 8 scenes, one generic
`SceneRecipe(string)` each, backed by `content/scenes/*.toml`.

**Gates**: 831 tests (821 close-of-3c-2 baseline + ~10 net this phase after audit-driven
additions), 0 warning, 0 leak, 0 validation. `drive` capture pinned + human visual
verdict PASS (`9030f6a64e9587b05d5abb99b487b1b9`). All 7 other scenes re-verified
byte-identical across two forced full re-cooks (the `.agscene` v4 bump, then again
after the audit fixes). `HeadlessSim --scene drive` → exit 1 confirmed (D9, message
names `DriveControl`), on both JIT and a NativeAOT publish. Sandbox + HeadlessSim
JIT == NativeAOT on every capture/snapshot.

**Double audit** (`csharp-lowlevel` + `engine-architect`, parallel, full diff — plus,
since this is the closing phase, a review of all 3 phases' accumulated 🟡 deferred
debt for anything that should block domain close): PASS-with-concerns both, **2 🟠
found independently by both and fixed**:
1. `drive_control` + a pending restore (`AGAPANTHE_LOAD` or `[restore]`) was a hard
   startup crash, contradicting this doc's own §11 claim that it "correctly no-ops
   during a resume." Unlike `LandingChallengeSystem`'s lazy count-based seed,
   `DriveControl` genuinely cannot resolve "the body at cook-time index N" after a
   restore — fixed by rejecting the combination explicitly (cook time via
   `SceneCompiler`, runtime via `SceneRecipe` for the `AGAPANTHE_LOAD` case cook time
   can't see), not by attempting a broken no-op.
2. `[[entity]] body = true` on a multi-mesh model or multi-member prefab silently
   spawned N co-located, mutually-penetrating rigid bodies (`Place` stamps one
   `SceneBody` onto every member×mesh `SceneEntity` it emits) — fixed by rejecting
   at cook time unless exactly one entity results.

5 additional 🟡 findings applied: `body`/`velocity` TOML keys leaked onto
`[[grid]]`/`[[cluster]]` and were silently discarded there (violating
`RejectUnknownKeys`' own no-silent-ignore guarantee) — now kind-gated; a forged/corrupt
blob's `None` probe model on a non-`DriveControl` kind deferred to a much later,
less legible failure — now rejected immediately, naming the kind; the mutable brake
latch + `KeyPressed` subscription lived on the registry-singleton factory instead of
the per-`Create` system instance — moved; `SceneRecipe`'s factory dispatch used
`FirstOrDefault`, silently picking the first of several same-`Kind` factories — now
throws on ambiguity; and — endorsed by both audits as the one 🟡 accumulated across
all 3 phases worth closing before the domain closes, rather than left as backlog —
`world_origin` was applied to entity positions but consumed raw by `ScenePhysics.
AttractorCenter`/`SceneSystem.Centre`/`ZoneCenter`, a silent-wrong-answer landmine for
the first scene wanting both non-zero — now rejected at cook time.
`CookRunner.CookerVersion` bumped to `"contenu3c-3"` for the v3→v4 change (a 3c-2
audit finding, applied consistently here too).

**Deviations from this spec's §11, all confirmed correct or fixed in review**:
`[[entity]]` needed an entirely new `body`/`velocity` authoring capability that §11
had not anticipated (only `[[cluster]]` could build a `SceneBody` before this phase) —
added as in-scope cook-side authoring work, then hardened by the audit fixes above.

Contenu-3c is now **CLOS (3/3)**. The domain "declarative scenes" effort — spanning
Contenu-1/2/3a/3b/3c-1/3c-2/3c-3 — is complete: every scene, prefab, and gameplay
system is authored TOML compiled to cooked binary blobs, materialized by GPU-free
shared code, with client and (eventually) a dedicated server able to run the exact
same population path. What remains as explicitly scoped-out backlog (not blocking):
procedural environment as a real cooked `.agenv`/`AssetKind.Environment` (Contenu-2b);
entity name→`GlobalId` lookup (D8, no consumer); `ProbeRadius`/`Every` validation;
`MoveSpeed`/`ShadowDistance` sentinel semantics formal documentation; the derivation
scripts (`bake_planet.cs`, `bake_challenge.cs`, this phase's `drive` bake) not
committed for traceability.
