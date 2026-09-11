# Contenu-3b — `.agscene` / `.agprefab` + `SceneLoader` — Design Spec

**Status**: draft for scored review
**Date**: 2026-09-09 (session 34)
**Domain**: Contenu (sub-milestone 3b of 3)
**Predecessors**: Contenu-1 (`AssetKey`, snapshot v3, S31) · Contenu-2 (offline cook + manifest +
`AssetCatalog`, S32) · Contenu-3a (`AssetRef` sim-side identity, snapshot v4, `SimSceneContext` split,
S33 — `25d1126`)

---

## 1. Summary

Give the engine a **declarative scene format**: hand-authored **TOML** under `content/scenes/` and
`content/prefabs/`, compiled offline by `tools/AssetCooker` into deterministic binary `.agscene`
blobs listed in `content.agmanifest`, read at runtime by a new **GPU-free `SceneLoader`** that
populates a `GameWorld`. The payoff: **client and server share world-population code** —
`HeadlessSim --scene <key>` loads the exact same cooked file the Sandbox does.

The `model` scene family (single model / grid / drop) moves to data. Prefabs are a **cook-time
include** — inlined into the scene blob, never a runtime asset. Procedural asset generation
(ground / grass / sky / sphere), game-system factory registries, and migrating `drive` / `planet*`
are **3c**.

### Out of scope (3c / Contenu-2b / later)

Procedural asset generators + a `.agenv` environment format + real `AssetKind.Environment`;
game-system / command-handler factory registries; migrating `drive` and the three `planet*`
recipes; runtime prefab instantiation (spawn-at-gameplay); multi-file `.gltf` with sibling
dependencies (Contenu-2b); moving `StbImageSharp` out of the runtime (Contenu-2b); cross-machine
byte-reproducibility of the blob (depends on `DeflateStream` output stability — same caveat as
`.agmodel`).

---

## 2. Locked decisions (S34 design interview)

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | Procedural assets deferred to 3c. 3b's `model` scene = one glTF model + a real HDR (`models/studio_small_1k.hdr`, already shipped) + declarative lights + camera. No procedural ground/sky. The `model` capture is **re-pinned** (a new scene composition — no `git stash` A/B is possible; the gate is a new MD5 + the human visual verdict). | "Generator" is a 3c concept in the domain decomposition; cooking generators in 3b means either a mini generator-DSL in the cooker or moving 3 generators + adding `.agenv` — scope creep. A studio-HDR model scene is a fine proof. |
| D2 | Scope = the full `model` family (single / grid / drop) + `.agprefab` + a `[physics]` block wired to the **engine** `PhysicsSystem` (the one built-in — not a registry). | Makes 3b a complete "the model scene is data" story; gives `.agprefab` a real consumer (the grid); `[physics]→PhysicsSystem` is defensible (it is not game-specific). Balances 3b vs 3c. |
| D3 | Authoring is TOML via **Tomlyn**, cook-side only (`Agapanthe.Assets.Pipeline`). The runtime reads the **binary blob**, never TOML. | TOML's nested tables + array-of-tables + native comments suit a hand-edited scene file; the cook tool is non-AOT and never a `ProjectReference`, so a new dep there is free; Tomlyn stays out of every shipped binary's closure. |
| D4 | `HeadlessSim --scene <key>` loads a cooked `.agscene` (full convergence). The hand-rolled `BuildScene` stays as the default / fallback. | 3b's whole point vs 3a is that the format now exists — the shared-population promise is exactly what 3b should deliver, not defer. |
| D5 | `.agmodel` **v2**: `MeshAsset` gains `BoundsCenter` / `BoundsRadius`, computed by the cooker via a shared `Agapanthe.Assets.MeshBounds.Compute` helper (body = today's `SceneBuilder.ComputeMeshLocalSphere`). The materializer (headless) reads them; `SceneBuilder` (client) reads them **when set** and **falls back to computing** for the un-migrated in-code procedural generators (whose meshes never reach the cooker). `CookerVersion` bumps → full re-cook. | A GPU-free materializer needs per-mesh local bounds; precomputing at cook time is one source of truth for cooked assets; the fallback keeps procedural drawables correct until 3c cooks them. |
| D6 | New project `Agapanthe.Scene` = `{Core, World, Assets}`, GPU-free. Holds the materializer + `SceneLoader` + shared logic. The reader (`AgSceneFormat` + `SceneDefinition` DTO) lives in `Agapanthe.Assets` next to `AgModelFormat`. | `SceneLoader` needs `GameWorld` (World) + `AssetCatalog` (Assets); it must be referenceable by `HeadlessSim`, so it cannot live in `Agapanthe.App` (Vulkan is transitive there). `Agapanthe.Engine`'s pinned closure `{Core, World}` stays intact — Engine does not reference Scene. |
| D7 | Cook-time expansion: TOML carries directives (`[[entity]]`, `[[grid]]`, `[[cluster]]`, prefab instances); the cooker **unrolls** them into a **flat entity list** in the blob (it replays `Hash(i)`, the grid stride). The runtime materializer is a trivial loop — no layout logic, no `Hash`. | Keeps the runtime path (the shared client/server code) minimal; a 100×100 grid = ~10k blob entries ≈ 400 KB → deflate ≈ 20 KB (highly repetitive). |
| D8 | `.agprefab` = cook-time include, inlined. No `AssetKind.Prefab`, no runtime prefab loader, no shipped prefab blob. Runtime never sees a prefab. | Runtime prefab instantiation (projectiles, gameplay spawns) has no consumer in 3b — YAGNI. 3c/later, with a gameplay need, adds it. |
| D9 | `GameWorld.ResolveMeshRefs(MeshRefResolver)` (new, ~10 lines): the client materializes GPU-free → `registry.Load` per model key (upload + register) → `ResolveMeshRefs` fills `MeshRef` from `AssetRef`. One spawn path for client + server (reuses the Contenu-3a resolver). | Avoids two divergent spawn code paths. `ResolveMeshRefs` is also the natural "promote a headless-loaded world to a client" operation. |
| D10 | Concrete scene files: `content/scenes/{model,grid,drop,metalrough}.toml` with fixed params. `AGAPANTHE_SCENE=<name>` → loads `scenes/<name>`. The dynamic `grid:NxN` / `drop:N` tokens **and** the CLI model-key arg are **removed**. | A data-driven scene carries its params. A dev who wants 50×50 adds a file. Contenu-2 already removed arbitrary-path model load; this is the same direction. |

---

## 3. Architecture

### 3.1 Assemblies

```
Core ◄── Assets ◄── Scene ◄── App          Scene = GPU-free {Core, World, Assets}
         ▲            ▲
       World ─────────┘        HeadlessSim ──► Scene
```

- **`src/Agapanthe.Scene/Agapanthe.Scene.csproj`** — new. ProjectReferences `Agapanthe.Core`,
  `Agapanthe.World`, `Agapanthe.Assets`. No `PackageReference`. `IsAotCompatible`.
- `EngineIsHeadlessTests`: a new static `[InlineData("src/Agapanthe.Scene/Agapanthe.Scene.csproj",
  "Agapanthe.Core", "Agapanthe.World", "Agapanthe.Assets")]`; `Agapanthe.Scene` added to the
  `HeadlessSim` and `App` allowlists; the assembly-closure walk for `HeadlessSim` now legitimately
  includes `Agapanthe.Assets` + `StbImageSharp` (a pure-managed image decoder — AOT-clean, already
  in the runtime per Contenu-2; Contenu-2b removes the HDR path). No forbidden assembly (Graphics /
  Rendering / Platform / Engine.Render / Silk.NET) enters.
- **`src/Agapanthe.Assets.Pipeline/*.csproj`** gains `PackageReference Tomlyn` — cook-side only.
  `AssetsPipelineIsolationTests` already forbids any shipped project referencing `Assets.Pipeline`;
  a new assertion pins that no shipped assembly's closure contains `Tomlyn`.

### 3.2 The `.agscene` binary format (`src/Agapanthe.Assets/AgSceneFormat.cs`)

Pattern of `AgModelFormat`: `magic "AGSC"u8 (4) | version u32 LE | uncompressedPayloadLen u32 LE`,
then a raw `DeflateStream` (`CompressionLevel.Optimal`) of the payload to EOF. Reader
`public static SceneDefinition Read(ReadOnlySpan<byte>)`; writer
`internal static void WriteContainer(Stream, SceneDefinition)` — IVT to `Agapanthe.Assets.Pipeline`
(the `.csproj` `<InternalsVisibleTo>` already exists). `AgSceneException` (parallel to
`AgModelException`). Every count is bounds-checked against remaining bytes before it drives an
allocation (the `AgModelFormat.Reader.ReadCount()` pattern); a hard payload ceiling (1 GiB); no
trailing bytes.

**Payload layout** (all primitives little-endian; the flat, already-expanded entity list):

```
sceneName          : u16 len + UTF-8
worldOrigin        : f64 ×3
keyTable           : u32 count, then per key { u16 byteLen + UTF-8 } — ordinal-ascending, entry 0 = "" sentinel
entityCount        : u32
  per entity:
    keyIdx         : u32   (into keyTable — the entity's model; entry 0 "" = no model, the sentinel,
                            unused in 3b because every 3b entity is a drawable)
    localMesh      : u32
    localMat       : u32
    position       : f64 ×3
    rotation       : f32 ×4   (quaternion, xyzw)
    scale          : f32
    flags          : u8       (bit0 castsShadow, bit1 physicsBody)
    [physicsBody]   inverseMass f32 | restitution f32 | radius f32 | velocity f32 ×3
lightCount         : u32
  per light:
    kind           : u8       (0 directional, 1 point)
    color          : f32 ×3
    intensity      : f32
    [directional]  direction f32 ×3
    [point]        position f64 ×3 | range f32
ambient            : f32 ×3
camera:
    mode           : u8       (0 frame-bounds, 1 fixed)
    fovY           : f32
    freeFly        : u8
    [frame-bounds] viewDir f32 ×3 | distanceMul f32
    [fixed]        position f64 ×3 | yaw f32 | pitch f32 | near f32 | far f32
environment:
    mode           : u8       (0 none, 1 hdri-path)
    [hdri]         u16 len + UTF-8 (a runtime path, relative to the content dir or absolute)
physics:
    present        : u8
    [present]      gravity f32 ×3 | groundY f32
restore:
    present        : u8
    [present]      u16 len + UTF-8 snapshot path
```

`version` mismatch → `AgSceneException` naming the version. The key table's strictly-ascending
ordinal check + strict UTF-8 decode match the snapshot v3/v4 key table.

**`camera.mode = fixed` and the `[restore]` block are parsed, written and round-tripped, but no 3b
scene uses them** (finding #8) — `fixed` is `drive`'s camera, `[restore]` is `planet*`'s snapshot
resume, both migrated in 3c. Implementing them now (they are cheap) keeps the format stable across
3c; a round-trip test covers them; the migration in §6 does not depend on them.

### 3.3 `SceneDefinition` (DTO, `src/Agapanthe.Assets/SceneDefinition.cs`)

Immutable, GPU-free, names only `Core` types (`Double3`, `Vector3`, `Quaternion`, `AssetKey`). A
`sealed record` with `IReadOnlyList<SceneEntity>`, `IReadOnlyList<SceneLight>`, `SceneCamera`,
`SceneEnvironment`, `ScenePhysics?`, `SceneRestore?`, `Double3 WorldOrigin`, `string Name`.
`SceneEntity { AssetKey Model; int LocalMesh; int LocalMat; Double3 Position; Quaternion Rotation;
float Scale; bool CastsShadow; SceneBody? Body }`. `SceneBody { Vector3 Velocity; float InverseMass;
float Restitution; float Radius }`.

**No hierarchy in 3b.** `SceneEntity` has no `Parent` — the flat expanded list is drawables/bodies
only. Hierarchical transform nodes (`GameWorld.Spawn` / `Parent` / `PropagateTransforms`) stay
covered by `HeadlessSim`'s hand-rolled `BuildScene` default and `AotRootingSmoke`; a scene that needs
a hierarchy adds a `[[node]]` block in a later milestone (finding #2). `.agprefab` children are
therefore also flat in 3b.

### 3.4 `ContentManifest` + `AssetCatalog`

- `AssetKind` gains `Scene = 3` (after `Font = 2`). `Environment = 1` stays reserved.
- `ContentManifest.Read` already validates `blobPath` and ordinal-ascending keys — no change beyond
  the enum.
- `AssetCatalog.LoadScene(AssetKey key) → SceneDefinition`: dict lookup → `AssetException` if
  absent; `entry.Kind != AssetKind.Scene` → `AssetException` ("is a {Kind}, not a scene");
  `Path.Combine(_contentRoot, entry.BlobPath)`; `File.ReadAllBytes` wrapped; `AgSceneFormat.Read`.
  No cache (matches `LoadModel`).

### 3.5 `.agmodel` v2 — per-mesh bounds

- `MeshAsset` gains `Vector3 BoundsCenter` (default `Zero`), `float BoundsRadius` (default `0`).
- `AgModelFormat.Version` 1 → **2**. The mesh section writes `boundsCenter f32×3 | boundsRadius f32`
  after `worldTransform`. `Read` for v1 is **dropped** (there are no shipped v1 blobs — the cooker
  regenerates everything on the `CookerVersion` bump; a v1 blob → `AgModelException` "re-cook").
- The bounds algorithm: a `MeshBounds.Compute(ReadOnlySpan<Vector3> positions)` helper in a shared
  GPU-free spot (**`Agapanthe.Core`** or `Agapanthe.Assets`), **whose body is the current
  `SceneBuilder.ComputeMeshLocalSphere`** — one implementation, no drift. The glTF loader (Pipeline)
  calls it to fill `MeshAsset.BoundsCenter/Radius` at cook time.
- **`SceneBuilder` keeps computing bounds — as a fallback.** The in-code procedural generators
  (`BuildGroundModel`, `PlanetContent.BuildSphereModel`, grass — **not migrated in 3b**, D1) build
  `MeshAsset` directly and never reach the cooker, so their `BoundsRadius` stays `0`. `SceneBuilder`
  reads `mesh.BoundsCenter/Radius` **when `BoundsRadius > 0`** (the cooked path), else calls the same
  `MeshBounds.Compute` (the procedural path). Every drawable — cooked or procedural — gets a correct
  local sphere. (Finding #1 — the "delete the computation" wording was wrong.)
- Verify: the cooked `model` capture — if the shared helper == the old `ComputeMeshLocalSphere`
  (it is, same body), byte-identical bounds. A `planet-drop` capture assertion in W1 proves the
  procedural fallback path still produces the same spheres (that scene is the one `model` can't see).

### 3.6 The cook side (`src/Agapanthe.Assets.Pipeline/`)

- **`SceneTomlReader.cs`** — Tomlyn parse → `AuthoredScene` (an in-memory model with the directives
  **unexpanded**: `[[entity]]`, `[[grid]]`, `[[cluster]]`, prefab refs, plus the light/camera/
  environment/physics/restore blocks). Rejects unknown keys, missing required fields, malformed
  arrays — each with a `AssetException` naming the file + the TOML path.
- **`PrefabTomlReader.cs`** — `content/prefabs/*.toml` → `AuthoredPrefab` (a list of `[[entity]]`,
  optionally nested children — 3b: a flat list is enough).
- **`SceneCompiler.cs`** — takes `AuthoredScene` + the set of already-cooked `.agmodel` blobs (read
  back for bounds + diagonal) + the authored prefabs → a flat `SceneDefinition`:
  - `[[entity]]` / prefab instance → one or more `SceneEntity` (a prefab instance = its entities,
    each transformed by the instance's transform).
  - `[[grid]] { prefab|model, rows, cols, spacing_mul }` → `rows*cols` `SceneEntity`s at
    `((c - halfC)*spacing, 0, (r - halfR)*spacing)` where `spacing = modelDiagonal * spacing_mul` —
    the loop **copied from `ModelContent.SpawnGrid`**.
  - `[[cluster]] { prefab|model, count, spacing_mul? }` → a cube cluster — the `Hash(i)` jitter +
    stride **copied from `ModelContent.SpawnDropScene`**; each entity gets `flags.physicsBody` with
    the cluster's `inverseMass`/`restitution`/`radius`.
  - The scene's `[physics]` → `ScenePhysics`; `[restore]` → `SceneRestore`; lights/camera/env
    translated field-for-field.
- **`AgSceneWriter.cs`** — public façade → `AgSceneFormat.WriteContainer` + tmp-then-move
  `WriteFile` (the `AgModelWriter` pattern).
- **`CookRunner.Cook`** — after `*.glb → .agmodel`, globes `content/scenes/*.toml` (ordinal), for
  each: `SceneTomlReader` → `SceneCompiler` (needs the just-written `.agmodel` blobs + prefabs) →
  `AgSceneWriter.WriteFile` → `ManifestEntry(sceneKey, AssetKind.Scene, blobRel, SHA256(blob))`.
  **Incrementality**: a scene's `.cookstate` hash = `SHA256(scene.toml)` combined with the content
  hashes of every `.agmodel` it references and every prefab it includes — a model change re-cooks
  the scenes that use it. `CookerVersion` bumps (`contenu2-1` → `contenu3b-1`).
- **`build/Agapanthe.Cook.targets`** (new) — the `CookAssets` / `IncludeCookedAssets` MSBuild
  targets **extracted verbatim** from `Sandbox.csproj` (paying the Contenu-2 debt), imported by
  both `samples/Sandbox/Sandbox.csproj` and `samples/HeadlessSim/HeadlessSim.csproj`.

### 3.7 `Agapanthe.Scene` — the runtime materializer

**`SceneMaterializer.Materialize(SceneDefinition def, AssetCatalog catalog, GameWorld world) →
MaterializeResult`** (GPU-free):

1. For each distinct `def` model key: `catalog.LoadModel(key)` → `ModelAsset` (v2). Local
   `Dictionary<AssetKey, ModelAsset>` cache.
2. For each `SceneEntity`: for the referenced model's `LocalMesh`, build an `ImportedEntitySpec` —
   `Mesh/Material = MeshHandle.Invalid` / `MaterialHandle.Invalid`;
   `Identity = new MeshRefKey(entity.Model, entity.LocalMesh, entity.LocalMat)`;
   `Position = entity.Position + def.WorldOrigin`;
   `RotationScale` = `mesh.WorldTransform` (translation row zeroed) composed with the entity's
   `Rotation`/`Scale`; `BoundsCenter/BoundsRadius` from `mesh` (v2); `Order = entity.LocalMesh`.
   `entity.Body` present → `world.SpawnBody(spec, body.Velocity, body.InverseMass, body.Restitution,
   body.Radius)`; else `world.SpawnImported(spec, castsShadow: entity.CastsShadow)`.
3. `world.FlushStructuralChanges()`.
4. `MaterializeResult { IReadOnlyDictionary<AssetKey, ModelAsset> LoadedModels;
   PhysicsSettings? Physics; string? RestorePath; SceneDefinition Def }` — `Physics` built from
   `def.Physics` + `sim`-supplied `FixedDeltaSeconds` (passed in), `RestorePath` from `def.Restore`.

**`GameWorld.ResolveMeshRefs(MeshRefResolver resolve)`** (new, `src/Agapanthe.World/GameWorld.cs`):

```csharp
public void ResolveMeshRefs(MeshRefResolver resolve)
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    AssertOwnerThread();
    ArgumentNullException.ThrowIfNull(resolve);
    foreach (ref var chunk in _world.Query(new QueryDescription().WithAll<AssetRef, MeshRef>()))
    {
        var assets = chunk.GetSpan<AssetRef>();
        var meshes = chunk.GetSpan<MeshRef>();
        for (var i = 0; i < chunk.Count; i++)
        {
            var k = assets[i].Value;
            if (!k.IsNone && !meshes[i].Mesh.IsValid)
            {
                var (m, mat) = resolve(k.Key, k.LocalMesh, k.LocalMat);
                meshes[i] = new MeshRef { Mesh = m, Material = mat };
            }
        }
    }
    _structuralDirty = true; // the persistent slot buffer must rebuild
}
```

The resolver's throw-on-unresolvable contract (Contenu-3a) propagates. Test accessor
`MeshRefForTest` already exists.

### 3.8 `SceneLoader` — per-side orchestration

**`src/Agapanthe.Scene/SceneLoader.cs`**

- `public static MaterializeResult LoadHeadless(SceneDefinition def, AssetCatalog catalog,
  SimSceneContext sim)` — wait: `SimSceneContext` is in `Agapanthe.App`, which `Agapanthe.Scene`
  must not reference (App pulls Vulkan). **So `LoadHeadless` takes the primitives it needs**:
  `LoadHeadless(SceneDefinition def, AssetCatalog catalog, GameWorld world, float fixedDeltaSeconds)
  → MaterializeResult`. The caller (`AppHost` for the client, `HeadlessSim` directly) wires
  `PhysicsSystem` / `RequestRestore` from the result. `Agapanthe.Scene` references `Agapanthe.Engine`?
  No — `PhysicsSettings` lives in `Agapanthe.World`, `PhysicsSystem` in `Agapanthe.Engine`. The
  materializer returns `PhysicsSettings?`; the caller (which has `Engine`) constructs `PhysicsSystem`.
  So `Agapanthe.Scene` = `{Core, World, Assets}` exactly.
- **Client** (`src/Agapanthe.App/`): a new `SceneRecipe(string sceneName) : ISceneRecipe` — **one
  class, one instance per scene**. `SandboxGame.Scenes` lists `new SceneRecipe("model"), new
  SceneRecipe("grid"), new SceneRecipe("drop"), new SceneRecipe("metalrough")`. `Name => sceneName`;
  default `Matches` (name equality) is enough — no token family, no dynamic parsing. Replaces
  `ModelSceneRecipe`.
  - `Build(sim, presentation)`: `var def = sim.Catalog.LoadScene(new AssetKey($"scenes/{Name}"))`.
  - `var result = SceneLoader.LoadHeadless(def, sim.Catalog, sim.World,
    sim.Simulation.Settings.FixedDeltaSeconds)`.
  - `if (result.Physics is { } ps) sim.AddSystem(Stage.Simulation, new PhysicsSystem(sim.World, in ps))`.
  - `if (result.RestorePath is { } rp) sim.RequestRestore(rp, SnapshotAllocatorPolicy.AdoptFromHeader)`.
  - **presentation tail** (`var p = presentation!;`): for each `(key, model)` in `result.LoadedModels`
    → `p.Registry.Load(p.Device, model, p.Renderer.MaterialSetLayout, key, def.WorldOrigin)` (the
    returned specs are ignored — only the upload + `_keyIndex.Add` side effects matter);
    `sim.World.ResolveMeshRefs(p.Registry.ResolveMeshRef)`; apply `def.Lights` → `p.Renderer.Lights`,
    `def.Ambient`, `def.Environment` (`p.Renderer.SetEnvironment(HdrImageLoader.Load(path))` for
    `hdri`), `def.Camera`, `def.Camera.FreeFly`.
  - **S30-debt payment, scoped** (finding #7/§6): only what the client tail needs moves into
    `Agapanthe.App` — `SandboxCameras.FrameCamera` + `NarrowBounds` → `App/SceneCameraApplier`
    (`frame-bounds` reads `world.AggregateBounds()` then the same distance/pitch/near/far math;
    `fixed` sets the fields directly); `RecipeInput.WireFreeFly` → `App/SceneInput.EnableFreeFly`.
    `SandboxCameras.SetupLights` / the planet camera helpers / `RecipeInput.WireProbeKey` **stay in
    the Sandbox** (used by `drive`/`planet*`, 3c). `SceneLight` application (color/intensity/dir/
    position/range → `p.Renderer.Lights`) is a new `App/SceneLights.Apply` — the hardcoded rig in
    `SetupLights` is now authored data, not code.
- **`HeadlessSim`** (`samples/HeadlessSim/Program.cs`): `--scene <key>` →
  `AssetCatalog.Open(<contentRoot>)` → `catalog.LoadScene(key)` → `new GameWorld()` →
  `SimulationHost.CreateDefault(world)` → `SceneLoader.LoadHeadless(def, catalog, world,
  host.Settings.FixedDeltaSeconds)` → `if (result.Physics is { } ps) host.Add(Stage.Simulation,
  new PhysicsSystem(world, in ps))`. `--load` snapshot path unchanged; default (`--bodies` /
  hand-rolled `BuildScene`) unchanged. `HeadlessSim.csproj` gains `ProjectReference
  Agapanthe.Scene` + imports `build/Agapanthe.Cook.targets` (cooks `content/scenes/headless-default.toml`).

### 3.9 The Sandbox / `content/` after 3b

- `content/scenes/*.toml` — **pinned parameters** (finding #7):
  - `model.toml`: `models/DamagedHelmet.glb` at origin; directional light `dir (0.4,-0.7,-0.6)`,
    color `(1,0.96,0.9)`, intensity 12; ambient `(0.08,0.08,0.09)`; camera `frame-bounds`,
    `view_dir (0,0.35,1)`, `distance_mul 1.5`, `fov_y 60`, `free_fly`; `environment.hdri =
    "models/studio_small_1k.hdr"` (a runtime path — the HDR pipeline is Contenu-2b).
  - `grid.toml`: `[[grid]] prefab="helmet" rows=10 cols=10 spacing_mul=1.5`; same lights/camera/env
    as `model`. Camera framing over the 10×10 spread — **the culling-sensitive capture gate**
    (finding #4).
  - `drop.toml`: `[[cluster]] prefab="helmet" count=20 inverse_mass=1 restitution=0.3 radius=&lt;model
    radius&gt;`; `[physics] gravity=[0,-9.81,0] ground_y=&lt;cluster ground&gt;` — **always on now**
    (the `AGAPANTHE_PHYSICS=1` opt-in gate is gone; a `drop` scene is a physics scene). Same
    lights/camera/env.
  - `metalrough.toml`: a bare `[[entity]] model="models/MetalRoughSpheres.glb"` at scene scope (the
    model's mesh is already the metallic×roughness grid); `frame-bounds` camera; studio HDR.
    (A top-level `[[entity]]` is the direct-placement directive — same one `model.toml` uses.)
  - `headless-default.toml`: `[[cluster]] model="models/DamagedHelmet.glb" count=8 ...` +
    `[physics] gravity=[0,-9.81,0] ground_y=0` — **8 physics bodies, no hierarchy** (finding #2/#3;
    it references a real cooked model, so its snapshot differs from `BuildScene` — a new MD5, D4).
  - `content/prefabs/helmet.toml`: `[[entity]] model = "models/DamagedHelmet.glb"`.
- `samples/Sandbox/Scenes/ModelSceneRecipe.cs` **deleted**; `SceneRecipe.cs` added.
  `SandboxGame.Scenes` swaps them. `drive` + `planet*` recipes **untouched** (3c).
- `ModelContent`: `ResolveModelKey` deleted (no CLI arg); `SpawnGrid` / `SpawnDropScene` **logic
  copied into `SceneCompiler`** — the originals **kept** (as `internal static`, no live caller) so
  `SceneCompiler_Grid_Unrolls` / `_Cluster_MatchesSpawnDropScene` compare against a real reference
  impl (drift detection); deleted in 3c when the recipes go. `BuildGroundModel` / `BuildGrassImage`
  / `BuildSkyEnvironment` stay (3c cooks them). `PlanetContent` untouched.
- `Sandbox.csproj` drops its inline `CookAssets` block, imports `build/Agapanthe.Cook.targets`.
- CLI: the Sandbox no longer takes a model-key arg. `IblTestTool` (`AGAPANTHE_IBL_TEST`) unchanged.

---

## 4. Error handling

| Condition | Behaviour |
|---|---|
| `.agscene` blob: bad magic / unsupported version / truncated / count-beyond-remaining / trailing bytes | `AgSceneException` (naming the cause) |
| `AssetCatalog.LoadScene`: key absent / wrong `AssetKind` / blob IO error | `AssetException` / `AgSceneException` |
| `SceneMaterializer`: `def` references a model key not in the catalog | `AssetException` from `catalog.LoadModel` propagates |
| `SceneMaterializer`: `entity.LocalMesh` / `LocalMat` out of range for the model | `AgSceneException` ("scene references model '{key}' mesh {i} but it has {n}") |
| Client `ResolveMeshRefs`: resolver throws (key not uploaded) | propagates — a bug in the load order (upload must precede resolve) |
| Cooker: `scene.toml` unknown key / missing field / malformed array | `AssetException` naming the file + TOML path; exit 1 |
| Cooker: `[[grid]]`/`[[cluster]]` references a prefab/model not found | `AssetException`; exit 1 |
| Cooker: two scene sources map to one key | `AssetException` ("two sources map to key '{key}'") — the existing model-side guard, extended |
| `HeadlessSim --scene`: no `content.agmanifest` | `AssetException` "run a build" — same as the client's tolerant path, but headless has no `AssetCatalog.Empty` fallback → exit non-zero with the actionable message |
| v1 `.agmodel` blob encountered | `AgModelException` "format v1 predates precomputed bounds (Contenu-3b) — re-cook" |

---

## 5. Testing strategy

### 5.1 Deterministic gates

- `AGAPANTHE_SCENE=model` HDR capture — **re-pinned** to a new MD5 (studio-HDR composition; a new
  scene, so no `git stash` A/B). Weak on culling — see the `grid` gate below.
- `AGAPANTHE_SCENE=grid` HDR capture — **new deterministic gate** (`AGAPANTHE_MAX_FRAMES` set): a
  10×10 spread of instances is culling- and instancing-sensitive in a way a single centred helmet is
  not (finding #4). Pinned MD5 + a headless `AGAPANTHE_CULL_STATS` assertion (visible count matches
  the CPU frustum test) on the cooked grid.
- `planet-drop` HDR / UI captures — **unchanged** (planet recipes untouched by 3b). W1 additionally
  asserts the procedural-bounds fallback (`BuildSphereModel` / `BuildGroundModel` → `SceneBuilder`
  computes) produces spheres bit-identical to today (finding #1).
- `HeadlessSim` **default** snapshot MD5 (`--bodies 8` / `BuildScene`) — **unchanged** (no
  `.agmodel` involved; `AssetRef` already v4).
- `HeadlessSim --scene headless-default --save` — **new** MD5, pinned in
  `HeadlessSimSnapshotFormatTests`, verified JIT == NativeAOT.
- `.agmodel` cooked-blob hash (`AgModel_CookedDamagedHelmet_HashIsPinned`) — **re-pinned** (v2, +bounds).
- `AotComponentProbe` — PASS; `Agapanthe.Scene` in the HeadlessSim AOT closure, no Vulkan.
- `dotnet build` 0 warning · `dotnet test` green · 0 leak · 0 validation.

### 5.2 New tests

| Test | Asserts |
|---|---|
| `AgSceneFormat_RoundTrips` | `SceneDefinition` → `WriteContainer` → `Read` byte-identical + value-equal; two writes identical |
| `AgSceneFormat_Rejects` | bad magic / v-mismatch / truncation / forged count / trailing bytes → `AgSceneException` |
| `SceneCompiler_Grid_Unrolls` | `[[grid]] rows=3 cols=2` → 6 entities at the right positions (matches `SpawnGrid`) |
| `SceneCompiler_Cluster_MatchesSpawnDropScene` | `[[cluster]] count=20` → 20 bodies at positions bit-identical to `ModelContent.SpawnDropScene` (the copied `Hash(i)` + stride) |
| `SceneCompiler_Prefab_Inlines` | a scene instancing `helmet` twice → 2 entities with the prefab's model + the instance transforms |
| `SceneCompiler_UnknownKey_Throws` | scene/toml referencing a missing model/prefab → `AssetException` |
| `SceneTomlReader_Rejects` | unknown key / missing field / bad array → `AssetException` naming the path |
| `SceneMaterializer_PopulatesWorld_Headless` | `Materialize(def, catalog, world)` → N entities, each with the right `AssetRef`, `MeshRef` Invalid, correct `Bounds` (from v2), physics bodies where `flags.physicsBody` |
| `SceneMaterializer_UnknownModelKey_Throws` | `def` naming an absent key → `AssetException` |
| `GameWorld_ResolveMeshRefs_FillsFromAssetRef` | spawn drawables with Invalid `MeshRef` + set `AssetRef`, call `ResolveMeshRefs(fakeResolver)` → `MeshRef` filled; a `None` `AssetRef` left Invalid |
| `SceneLoader_LoadHeadless_AttachesPhysics` | a scene with `[physics]` → result carries `PhysicsSettings`; a scene with `[restore]` → carries the path |
| `AgModelFormat_V2_RoundTripsBounds` | `MeshAsset.BoundsCenter/Radius` survive write→read; a v1 blob → `AgModelException` |
| `MeshBounds_MatchesSceneBuilder` | the copied bounds algo == `SceneBuilder`'s previous computation on `DamagedHelmet` |
| `EngineIsHeadlessTests` (updated) | `Agapanthe.Scene.csproj` allowlist = `{Core, World, Assets}`; `HeadlessSim` closure has no forbidden assembly with `Scene` added |
| `AssetsPipelineIsolationTests` (updated) | no shipped assembly's closure contains `Tomlyn`; `Assets.Pipeline` + `AssetCooker` still the only referrers |
| `AppHostContractTests` (updated) | the `SelectRecipe(game, "grid:8x8")` / `"drop:…"` token-family cases **removed**; `SceneRecipe.Matches` covers `model`/`grid`/`drop`/`metalrough` by name (finding #5) |
| `CookedSceneModel_RendersLikeDirectLoad` | a model loaded via a cooked `.agscene` produces the same `RenderList` as the same model loaded directly through `registry.Load` (the Contenu-2 test-9 analogue for the scene path — permanent, unlike the one-shot `MeshBounds_MatchesSceneBuilder` W1 check) (finding #9) |

### 5.3 Double audit + human visual verdict

`csharp-lowlevel` (the `.agscene` reader's bounds-checking, `ResolveMeshRefs` correctness / zero
alloc, the v2 `.agmodel` change, HeadlessSim's grown closure) + `engine-architect` (the
`Agapanthe.Scene` project boundary, the cook-time-expansion principle, the client/headless split,
`.agprefab` as a cook-time include, the removal of the CLI arg + dynamic tokens). Human visual
verdict: `model` / `grid` / `drop` / `metalrough` render; `HeadlessSim --scene headless-default`
runs and saves.

---

## 6. Migration path

- `CookerVersion` bump → every `.agmodel` regenerates (v2). No shipped v1 blobs exist.
- `AGAPANTHE_SCENE=grid:8x8` / `drop:20` and the `-- <model>.glb` CLI arg **stop working** —
  replaced by `AGAPANTHE_SCENE=grid` / `drop` / `metalrough`. Launch profiles + `CLAUDE.md` +
  `docs/` updated. `IblTestTool` (`AGAPANTHE_IBL_TEST`) is unaffected.
- Env vars the `model` recipe read (`AGAPANTHE_GROUND`, `AGAPANTHE_HDRI`, `AGAPANTHE_CHURN`,
  `AGAPANTHE_PHYSICS`, `AGAPANTHE_CULL_STATS`, `AGAPANTHE_WORLD_ORIGIN`, `AGAPANTHE_UNLOAD_TEST`,
  `AGAPANTHE_VIEW`, `AGAPANTHE_SUN`) — the bench/churn/unload ones become scene fields or are
  dropped (bench/churn/unload are 3c system-factory territory); `WORLD_ORIGIN` / `VIEW` / `SUN`
  become scene fields. `AGAPANTHE_SCENE` / `AGAPANTHE_LOAD` / `AGAPANTHE_SAVE` / `AGAPANTHE_CONTENT`
  / `AGAPANTHE_MAX_FRAMES` / `AGAPANTHE_CAPTURE*` stay host-level.
- A `planet-challenge` `.save` (snapshot v4) still loads — 3b does not touch the snapshot format.
- `SceneBuilder` reads `mesh.BoundsCenter/Radius` **when set**, else computes (§3.5 — fallback for
  the un-migrated procedural generators). One-shot W1 check `MeshBounds_MatchesSceneBuilder` +
  permanent `CookedSceneModel_RendersLikeDirectLoad` + the `planet-drop` procedural-bounds assertion.
- **`HeadlessSim` artifact grows** (finding #10): today ~1.68 MB minimal. It gains `Agapanthe.Scene`
  + `Agapanthe.Assets` + `StbImageSharp` (all AOT-clean) + the shipped `content/` tree. Still no
  GPU / Vulkan; "NativeAOT sans GPU" holds. The size is noted, not gated — a dedicated-server binary
  legitimately carries its content. Contenu-2b's HDR-decoder-out-of-runtime work will trim
  `StbImageSharp`.

---

## 7. Execution outline (waves — not part of this spec's gate)

1. **W1** — `.agmodel` v2: `MeshAsset` bounds, `AgModelFormat` v2, `Agapanthe.Assets.MeshBounds`
   helper (body from `SceneBuilder`), `SceneBuilder` reads the DTO **with a compute-fallback for
   `BoundsRadius == 0`**. `CookerVersion` bump, blob hash re-pin. Gates: `model` capture re-verify,
   **`planet-drop` capture unchanged** (proves the procedural fallback), `MeshBounds_MatchesSceneBuilder`.
   Build green.
2. **W2** — `AgSceneFormat` + `SceneDefinition` + `AssetKind.Scene` + `AssetCatalog.LoadScene`;
   `AgSceneFormatTests`.
3. **W3** — cook side: `Tomlyn` ref, `SceneTomlReader`, `PrefabTomlReader`, `SceneCompiler`
   (grid/cluster/prefab copied from `ModelContent`), `AgSceneWriter`, `CookRunner` globes scenes;
   `build/Agapanthe.Cook.targets` extracted. `content/scenes|prefabs/*.toml` authored.
   `SceneCompilerTests`.
4. **W4** — `Agapanthe.Scene` project: `SceneMaterializer`, `SceneLoader.LoadHeadless`,
   `GameWorld.ResolveMeshRefs`. `SceneMaterializerTests`, `EngineIsHeadlessTests` +InlineData.
5. **W5** — client: `SceneRecipe`, `ClientScenePresenter` / camera + free-fly helpers lifted to
   `Agapanthe.App`, delete `ModelSceneRecipe`, `SandboxGame` swap, `Sandbox.csproj` imports the
   targets. Captures re-pinned.
6. **W6** — `HeadlessSim --scene`, `.csproj` ref + targets, `headless-default.toml`, new MD5
   pinned JIT == AOT.
7. **W7** — full verify, AOT publish, self-review, requirements validation, double audit, converge.

---

## 8. Decision log

| Decision | Chosen | Alternatives rejected |
|---|---|---|
| Procedural assets in 3b | deferred to 3c | cook ground/grass/sky in 3b (needs a generator-DSL or 3 generator moves + `.agenv` — scope creep) |
| Migration scope | full `model` family + `.agprefab` + `[physics]` | single-model only (makes 3c huge, `.agprefab` unconsumed) |
| Authoring encoding | TOML via Tomlyn (cook-side) | JSON via STJ (verbose for a hand-edited scene; no comments); TOML at runtime (a parser in the shipped closure) |
| `HeadlessSim` convergence | full `--scene` | plumbing + test (3b's whole point is the format exists — deliver the shared path) |
| Per-mesh bounds | `.agmodel` v2 carries them | a shared GPU-free bounds computation called by both (two callers to keep aligned, nothing precomputed) |
| `SceneLoader` home | new `Agapanthe.Scene` `{Core, World, Assets}` | reader in Assets + materializer in Engine (Engine's pinned `{Core, World}` closure would grow) |
| Layout / repetition | cook-time expansion → flat blob | runtime directives (layout logic + `Hash(i)` in the shared runtime path) |
| `.agprefab` | cook-time include, inlined | a cooked runtime asset (`AssetKind.Prefab` + loader — no consumer in 3b) |
| Client handle resolution | `ResolveMeshRefs` after upload | client spawns from `registry.Load` specs (two divergent spawn paths) |
| Tokens / CLI | concrete scene files, `grid:NxN`/`drop:N` + model arg removed | keep dynamic tokens (contradicts flat-blob expansion; parametric logic at runtime) |
| Contenu-3 shape | 3 sub-milestones (3a done, 3b this, 3c generators+factories+migrate) | fold generators into 3b |

## 9. Outcome (session 34, closed 2026-09-11)

**Delivered as designed**, decisions D1–D10 all held except a partial deviation on D10 (see below).
740 tests (up from 696 at the start of the milestone), 0 warning, 0 leak, 0 validation message, Sandbox +
HeadlessSim + `AotComponentProbe` JIT == NativeAOT. Captures re-pinned + human visual verdict **PASS** on
all 4 `model`-family scenes (`model` `9a010fc3…`, `grid` `c314e6a1…`, `drop` `2fbf87fa…`, `metalrough`
`c4cf4605…`); `planet-drop` byte-identical (`bc8440ab…`, untouched). `HeadlessSim --scene headless-default`
`8a5c0463…` JIT == NativeAOT; the hand-built default scene's MD5 (`6a13dd54…`) unchanged.

**D10 deviation**: `ModelContent.ResolveModelKey` and the Sandbox's CLI model argument were kept —
`DriveSceneRecipe` still resolves a model by that path and migrates to `SceneRecipe` only in 3c. Documented
on the board from Wave 5 onward, not discovered at audit time.

**Double audit**: `csharp-lowlevel` 4.1/5 and `engine-architect` 4.1/5, both PASS-with-concerns, **neither
found a 🔴**. 12 🟠 findings were applied in this session (see `.absolute-work/board.md` §"Double audit" for
the full list with file references) — the two formats' `ReadCount` hardened against a forged count sizing a
reference-typed array beyond what the compressed blob could produce; `GameWorld.ResolveMeshRefs` sets its
dirty flag before the loop rather than after a fully-successful pass; `SceneMaterializer` validates
`LocalMat` (not just `LocalMesh`); the material-less-mesh convention now matches `ResourceRegistry`'s
(`Materials.Count`, not `0`); NaN bounds and zero-length light directions are rejected at their write
boundary; the TOML reader rejects unknown keys and reads `Double3` fields through a `double`-precision path
(was silently narrowing through `float`); a prefab member's local offset is now rotated by the instance
transform; cook incrementality folds in prefab hashes; scene keys derive via `AssetKey.FromContentPath`
(the exact "directory jettisoned" regression Contenu-1 had already fixed for models). `ModelContent.
SpawnGrid`/`SpawnDropScene` (0 remaining callers after Wave 5) were deleted rather than left dead. All
fixes were re-verified against the pinned captures and HeadlessSim snapshots — byte-identical throughout.

Findings not applied (both auditors agreed they don't block the close) are recorded as Deferred Work on the
board and folded into `docs/BACKLOG.md` §4quater Contenu-3c: the `MaterializeResult` → `PhysicsSystem`/
`RequestRestore` wiring duplicated per host (the concrete shape of the pending `App`/`App.Client` split),
`ResourceRegistry.Unload` now has zero callers, `SceneLoader.LoadHeadless`'s name vs. its client caller,
`AGAPANTHE_VIEW` read directly in `SceneCameraApplier`, and a handful of naming/nit items.

**Contenu domain is now closed (3/3)**: Contenu-1 (asset identity) → Contenu-2 (offline cook) →
Contenu-3a (sim-side identity) → Contenu-3b (declarative scenes, this spec). Contenu-3c (procedural
generators, game-side factory registries, `drive`/`planet*` migration) is deferred, non-urgent backlog —
not a fourth required sub-milestone of this domain.
