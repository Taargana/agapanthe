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
