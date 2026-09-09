# Contenu-3a — Sim-side asset identity (`AssetRef`) + snapshot v4 + `SceneContext` split

**Status**: draft for scored review
**Date**: 2026-09-09 (session 33)
**Domain**: Contenu (sub-milestone 3a of 3)
**Predecessors**: Contenu-1 (stable asset identity, `AssetKey` + snapshot v3, S31) ·
Contenu-2 (offline cook + manifest + `AssetCatalog`, S32)

---

## 1. Summary

`MeshRef` — the component every drawable entity carries — holds only two process-local
handles into `ResourceRegistry`. Asset identity is **reconstructed at save time** by a GPU-side
lookup (`MeshRefIdentifier` → `ModelKeyIndex.Identify`): a `GameWorld.Save` with no identifier
delegate writes `AssetKey.None` for every drawable. **The simulation does not own the identity
of what it simulates** — it borrows it from the render side at the moment it serializes.

3a makes the entity **carry** its asset identity from spawn, in a component (`AssetRef`), and
serializes that component instead of doing a save-time lookup. Consequences:

- The client `Save` path emits real `AssetKey`s **without a delegate** — the identity is
  already on the entity (threaded from `ResourceRegistry.Load` through `ImportedEntitySpec`).
- Any **headless** populator that stamps identity onto its specs gets a reconstructible
  snapshot for free. In 3a the only such populator is a test fixture; the first production
  one is **3b's `SceneLoader`**. So 3a *enables* "an authoritative server emits state a client
  can reconstruct" — it does not yet *exercise* it end to end. That honesty matters: the 3a
  deliverable is the component + snapshot slot + `ImportedEntitySpec` carrier + resolver
  contract + the `MeshRefIdentifier` deletion, not a running headless server.

This is the last structural prerequisite of the engine-cap anchor "authoritative server,
topology is a deployment choice, not an architecture choice".

Contenu-3 (declarative prefabs & scenes) is **decomposed into three sub-milestones** with a
human gate + double audit between each, per the MP-0 / Contenu pattern:

- **3a (this spec)** — `AssetRef` component (asset identity inside the simulation) + world
  snapshot **v4** + the S30 `SceneContext` sim/presentation split + a `RestoreIfRequested`
  guard-rail. **No authoring format, no cooker, no `SceneLoader`.** Pure foundation.
- **3b** — `.agscene` / `.agprefab` (TOML authoring → deterministic binary blob cooked into
  the manifest) + `SceneLoader` + cooking of procedural assets; proven on the `model` scene.
- **3c** — three game-supplied factory registries (systems, procedural generators,
  command-handlers) + migration of `drive` and the three `planet*` recipes to data.

3a delivers no user-visible feature. Its deliverable is a **capability foundation**: after 3a,
an entity carries its asset identity as component state, `world.Save(stream)` serializes it
with no delegate, and `world.Load` keeps that identity even with no GPU resolver present. A
headless process that populates entities with identity (3b) then gets a reconstructible
snapshot with no extra work.

### Out of scope for 3a

The `.agscene`/`.agprefab` format, the cooker changes, `SceneLoader`, procedural-asset
cooking, the factory registries, migrating any recipe to data, folding `HeadlessSim` onto a
shared population path, texture/environment/font identity in the snapshot, a numeric
manifest-assigned asset id, `RunDedicatedServer`. All of these are 3b or 3c.

---

## 2. Locked decisions (S33 design interview)

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | `AssetRef` is a **managed struct** wrapping a `MeshRefKey` (`AssetKey` + two local indices), **special-cased in serialization** exactly as `MeshRef` is today — not a blittable numeric id. Gated by a **W0 spike** proving the pinned Arch handles a managed component cleanly under AOT; fallback = a blittable id + re-review. | The identity *is* the `AssetKey` string; the numeric-id path couples the snapshot to a manifest version and risks hash collisions. The snapshot key table already exists and already does this transform for `MeshRef`. `AssetRef` is the project's first managed component (`Parent` holds an unmanaged Arch `Entity`). |
| D2 | `MeshRef` **stays** as the render-consumed cache, **derived from `AssetRef`** at materialisation via `MeshRefResolver`. Snapshot v4 **no longer serializes `MeshRef`**. | The render path (`CollectRenderLists`, cull, instance buffer) is unchanged — lowest capture-stability risk. `MeshRef` becomes derived state, like `InstanceSlot`. |
| D3 | The `MeshRefIdentifier` delegate is **deleted**. `Save` reads `AssetRef` directly. | A headless `Save` emits real keys with zero ceremony — that is the point of the milestone. The handle→key lookup (`ModelKeyIndex.Identify`) and its reverse maps become dead code. |
| D4 | Snapshot **v4**; a **v3 file is upgraded in place** on load (v3's `MeshRef` `(keyIdx, localMesh, localMat)` is isomorphic to `AssetRef`). v1/v2 stay refused with their dedicated messages. | The local `planet-challenge` `.save` files keep loading. The v3 read path is small. |
| D5 | `SceneContext` → **full split**: `SimSceneContext` (headless-safe core) + `PresentationSceneContext` (nullable). `ISceneRecipe.Build(SimSceneContext, PresentationSceneContext?)`. | This is the central benefit of the "data-drive all 5 recipes" goal — a recipe (and later a `SceneLoader`) must be constructible without a GPU or a window. A nullable second parameter reads more honestly than a half-empty context. |
| D6 | `HeadlessSim` convergence in 3a = **plumbing + one test** (a fixture recipe builds a world with `presentation: null`). `HeadlessSim` itself is unchanged. | Real shared world-population code needs the format (3b) to know its own shape. Designing the helper now is designing blind. |
| D7 | Restore guard-rail: `SimSceneContext` carries an optional pending snapshot; the host calls **one** `ApplyPendingRestore(resolver?)` after `Build`. The three scattered `PlanetStage.RestoreIfRequested` call sites collapse. | Contenu-1 left "restore is a mandatory unguarded step, wrong order = the resolver throws". The recipe now *declares* it wants a restore instead of calling `Load` itself; the host applies it after asset registration. |

**D5 vs D6 — why the split is safe now but the population helper is not.** The sim/presentation
boundary is a *fact of the current code*: `GameWorld` + `SimulationHost` + `AssetCatalog` are
GPU-free today, the other seven `SceneContext` members are GPU/window objects today. Splitting
the context is re-shelving what already exists along a line that is already there. A shared
*population helper*, by contrast, is a new API whose signature is "what does a scene declare
and how" — that is precisely what the `.agscene` format (3b) defines. Designing it now fixes
its shape before its only caller exists.

---

## 3. Architecture

### 3.1 The `AssetRef` component (`src/Agapanthe.World/`)

```csharp
// Components.cs — component #13, appended
[Component]
internal struct AssetRef
{
    public MeshRefKey Value;   // Agapanthe.Core: (AssetKey Key, int LocalMesh, int LocalMat)
}
```

- Reuses `MeshRefKey` (already `public` in `Agapanthe.Core`) verbatim — no new Core type,
  and `Agapanthe.World` still does not reference `Agapanthe.Rendering`
  (`EngineIsHeadlessTests` guards this).
- Managed struct (holds a `string?` through `AssetKey`). Registered with the **existing**
  `ComponentRegistry.Root<T>()` helper (whatever its current body — do not prescribe a new
  form), appended as the last call in `ComponentRegistry.EnsureInitialized()`. The helper's
  `new T[1]` roots the array type for the ILC; that works for a managed struct just as for an
  unmanaged one (the W0 spike confirms Arch's storage/copy path).
- `ComponentRegistry.All.Count`: 12 → **13** (≤ 32 guard holds).
- Frozen-order test `ComponentRegistryTests.ComponentRegistry_All_MatchesTheFrozenOrder`
  updated to a 13-type list with `AssetRef` last.

**Why component #13 and not a reuse of `MeshRef`'s slot**: the milestone is defined by
`MeshRef` and `AssetRef` coexisting — `AssetRef` is the stored identity, `MeshRef` the
resolved render cache. Every drawable archetype gains one component; a body archetype gains
one component; nodes (no `MeshRef`) are untouched.

### 3.2 Materialisation

- `ImportedEntitySpec` (`src/Agapanthe.Core/ImportedEntitySpec.cs`) gains **one** field
  `public readonly MeshRefKey Identity` and **one** trailing constructor parameter
  `MeshRefKey identity = default`. `default(MeshRefKey) == MeshRefKey.None`, so every existing
  by-hand construction site (`DriveSceneRecipe`, `PlanetContent`, tests, `AotSerializationSmoke`)
  compiles unchanged and gets identity `None` — which is exactly the headless / no-identity
  case (serializes as key index 0). `ImportedEntitySpec` has no `[StructLayout]` and no
  blittable contract, so becoming a managed struct is safe.
- `ResourceRegistry.Load(GraphicsDevice, ModelAsset, DescriptorSetLayout, AssetKey key,
  Double3 worldOrigin)` fills `identity = new MeshRefKey(key, localMeshIndex,
  entry.LocalMaterialIndex)` per drawable — it already has `key` and the local indices;
  `ModelKeyIndex` computes the same triple today.
- `GameWorld.MaterialiseDrawable` (`GameWorld.cs:~248`) and `GameWorld.MaterialiseBody`
  (`GameWorld.Physics.cs:~92`) add `new AssetRef { Value = spec.Identity }` inside the same
  `_world.Create(...)` call, alongside `MeshRef`. No "add `AssetRef` later" path — like
  physics components, it is present from creation so archetypes stay stable.

**Every by-hand copy of a `Load`-sourced spec must thread `.Identity`.** Today
`world.Save(_, IdentifyMeshRef)` resolves the handles of these entities back to real keys at
save time; once `MeshRefIdentifier` is deleted, an entity's key comes *only* from its
`AssetRef`, which comes *only* from `spec.Identity`. Sites that rebuild an `ImportedEntitySpec`
field-by-field from a `Load` result:

| Site | Classification | Action |
|---|---|---|
| `ModelContent.SpawnGrid` (`ModelContent.cs:~376`) | keyed (same asset, instanced) | copy `s.Identity` into each cell's spec |
| `ModelContent.SpawnDropScene` (`ModelContent.cs:~420`) | keyed | copy `s.Identity` |
| `DriveSceneRecipe` steerable body (`DriveSceneRecipe.cs:~90`) | keyed | copy `s0.Identity` |
| `PlanetContent.SetupPlanetDrop` / `SetupPlanetChallenge` probe spec | keyed (`sandbox/probe`) | the spec returned from `registry.Load` already carries identity — pass it through unmodified |
| `PlanetContent.SetupPlanetChallenge` beacon → `SpawnImported` | keyed (`sandbox/beacon`) | spec comes straight from `registry.Load` → identity flows unmodified |
| `ProbeDropSystem.DropOne` / `LandingChallengeSystem.TryShoot` runtime spawn | keyed | the `in ImportedEntitySpec` they hold already carries identity — do not rebuild it without `.Identity` |
| `ModelContent.BuildGroundModel` → its spec | keyed (`sandbox/ground`, a `registry.Load`) | identity flows from `Load` |
| a genuinely hand-authored spec with no asset behind it | procedural | `identity = default` (`None`) is correct |

A regression test (§6.2) asserts the key table of a `grid:` / `planet-challenge` save still
contains the ground / probe / beacon keys.

### 3.3 Snapshot v4 (`src/Agapanthe.World/WorldSerialization.cs`)

- `SerializationVersion = 4`. Header layout unchanged (40 bytes). `componentCount` field = 13.
- **Key table**: mechanism unchanged. Save Pass 1 walks `AssetRef.Value.Key` (instead of
  calling `identify(mr.Mesh, mr.Material)`); non-`None` keys go into the
  `SortedSet<AssetKey>(KeyOrdinal)`. Write / read / strict-ascending-ordinal check on read:
  all unchanged.
- **Write**: `AssetRef` (component index 12) is special-cased like `MeshRef` (index 5) is
  today — `keyIdx(4) | localMesh(4) | localMat(4)`, a `None` key writes canonical `(0,0,0)`.
  `MeshRef` (index 5) is **not written at all** any more (derived on load, like
  `InstanceSlot`). Its presence-mask bit is still set by Save? **No** — Save clears both the
  `MeshRef` and `InstanceSlot` bits (runtime-derived); it sets the `AssetRef` bit.
- The load-side coupling that adds `InstanceSlot { Value = -1 }` keys off the **`AssetRef`**
  bit now, not `MeshRef`.
- **The three concrete-index switches** (`EntityHasComponent`, `WriteComponent`,
  `ReadAndAddComponent`, all guarded to match `ComponentRegistry.All` positionally): add
  `case 12`. `EntityHasComponent` → `e.Has<AssetRef>()`; `WriteComponent` / `ReadAndAddComponent`
  → a `// index 12 (AssetRef) handled by the caller` marker exactly like `case 5` (`MeshRef`)
  today. The "N concrete types" doc comment on the switch block updates 12 → 13.
- **Read (v4)**: for each entity with the `AssetRef` bit → read the 3 `u32`; `keyIdx >=
  keyTable.Length` → `WorldSerializationException` (checked before any short-circuit, as v3
  does); build `new AssetRef { Value = new MeshRefKey(keyTable[keyIdx], (int)localMesh,
  (int)localMat) }` (or `MeshRefKey.None` when `keyIdx == 0`); `entity.Add(assetRef)`. Then
  synthesize `MeshRef`: `keyIdx == 0 || resolve is null` → `MeshRef { Mesh = MeshHandle.Invalid,
  Material = MaterialHandle.Invalid }`, else `new MeshRef(resolve(key, localMesh, localMat))`;
  `entity.Add(meshRef)`. Then `entity.Add(new InstanceSlot { Value = -1 })`.
- **Read (v3 upgrade)**: `Load` accepts `version is 3 or 4` (was a hard `!= 4` throw). A named
  `const int V3ComponentCount = 12`; a v3 header must carry `componentCount == V3ComponentCount`,
  a v4 header `== ComponentRegistry.All.Count` (13). `version` is threaded into the entity
  loop. The only version-conditional branch is at the drawable-identity slot:
  - v4: the entity mask has the `AssetRef` bit (index 12) → read at the index-12 position.
  - v3: the entity mask has the `MeshRef` bit (index 5) → read the same 12 bytes
    `(keyIdx, localMesh, localMat)` at the index-5 position and materialise `AssetRef` from
    them (v3's key table is byte-identical in shape).
  Both paths then run the shared v4 tail: derive `MeshRef` via `resolve`, add `InstanceSlot`.
  Every other v3 component is positionally identical to v4 (v4 only appends index 12), so no
  other branch is needed. The dedicated v1 / v2 refusal messages are unchanged.
- **Deletions**: `public delegate MeshRefKey MeshRefIdentifier(MeshHandle, MaterialHandle)`;
  the `Save(Stream, MeshRefIdentifier?)` overload — only `Save(Stream)` remains.
  `ResourceRegistry.IdentifyMeshRef`; `ModelKeyIndex.Identify`. The reverse maps
  `_byMesh` / `_byMaterial` lose their only *reader* (`Identify`), **but stay populated** —
  `Add` still consults them for its atomic per-handle collision pre-check (the MP-0b W4
  guard: a handle mapped by two assets is a bug class). Simplest change = delete `Identify`,
  keep everything `Add` touches. `Add` still rejects a duplicate key. `MeshRefResolver` is
  **kept** unchanged.

### 3.4 `SceneContext` split (`src/Agapanthe.App/`)

`SceneContext.cs` is replaced by two types:

```csharp
public sealed class SimSceneContext            // headless-safe core
{
    public required GameWorld World { get; init; }
    public required SimulationHost Simulation { get; init; }
    public required AssetCatalog Catalog { get; init; }   // GPU-free (Contenu-2)
    public required string[] Args { get; init; }
    public required HostOptions Options { get; init; }

    // simulation-system registration — the reason the split exists
    public void AddSystem(Stage stage, ISystem system);   // → Simulation's scheduler

    public void RequestRestore(Stream snapshot, SnapshotAllocatorPolicy policy);
    internal bool HasPendingRestore { get; }
    internal SnapshotLoadResult ApplyPendingRestore(MeshRefResolver? resolve);
}

public sealed class PresentationSceneContext   // null when headless
{
    public required GraphicsDevice Device { get; init; }
    public required ResourceRegistry Registry { get; init; }
    public required Renderer Renderer { get; init; }
    public required Camera Camera { get; init; }
    public required FreeCameraController Controller { get; init; }
    public required IWindow Window { get; init; }
    public required RenderList RenderList { get; init; }
    public required FrameOrchestrator Orchestrator { get; init; }   // IRenderSystem registration
}
```

- **System registration.** Recipes today call `ctx.Orchestrator.Add(Stage.Simulation, new
  PhysicsSystem(...))` and `ctx.Orchestrator.Add(new UiRenderSystem(...))`. After the split:
  an `ISystem` (`PhysicsSystem`, `ProbeDropSystem`, `LandingChallengeSystem`, `BenchSpinSystem`,
  `ChurnSystem`) registers via `sim.AddSystem(stage, system)` — headless-reachable. An
  `IRenderSystem` registers via `presentation!.Orchestrator.Add(renderSystem)`. `FrameOrchestrator`
  composes the `SimulationHost`, so `SimSceneContext.AddSystem` forwards to
  `Simulation`'s scheduler and the two stay one object graph.

- `ISceneRecipe.Build(SimSceneContext sim, PresentationSceneContext? presentation)`. The five
  current recipes are client-only; each opens `Build` with `var p = presentation
  ?? throw new InvalidOperationException("<name> requires a presentation context");` and the
  body is a near-mechanical `ctx.X` → `sim.X` / `p.X` rewrite.
- `AppHost.RunClient` builds both contexts, calls `recipe.Build(sim, pres)`, then:
  `if (sim.HasPendingRestore) sim.ApplyPendingRestore(pres.Registry.ResolveMeshRef);` —
  replacing `PlanetStage.RestoreIfRequested`.
- The `options.SavePath` block in `AppHost.RunClient` (`AppHost.cs:~151`, runs right after
  `recipe.Build`) — `world.Save(saveStream, registry.IdentifyMeshRef)` → `world.Save(saveStream)`.
  The delegate is gone; the entities carry their keys. (`SavePath` is already a typed
  `HostOptions` field — no env-var change.)
- `HostOptions` gains `string? LoadPath` ← `AGAPANTHE_LOAD`. (Recipes read several env vars
  by hand — `AGAPANTHE_WORLD_ORIGIN`, `AGAPANTHE_GROUND`, the ~30 tuning vars — those all
  move to scene fields in **3c**, not here. 3a lifts only `AGAPANTHE_LOAD` because the restore
  guard-rail D7 needs it as a typed option.) `PlanetSceneRecipe.Matches` still reads
  `AGAPANTHE_LOAD` directly for its "claim the default scene when a load is requested" side
  condition — `Matches` has no `HostOptions` (unchanged; a later milestone threads an enriched
  token).
- `PlanetStage.RestoreIfRequested` is deleted. `PlanetStage.Build` calls
  `sim.RequestRestore(File.OpenRead(loadPath), SnapshotAllocatorPolicy.AdoptFromHeader)` when
  `LoadPath` is set, **after** it has registered the planet/Sun assets. Each planet recipe
  likewise registers its own assets (probe, beacon) in `Build` before returning; the host's
  single `ApplyPendingRestore` runs after all of that.
- `PlanetChallengeSceneRecipe` F5 quicksave: `world.Save(fs)` (no delegate).
- `EngineIsHeadlessTests`: the static `App.csproj` allowlist is unchanged (App already
  references everything except `Platform`). A new test builds a fixture recipe with
  `Build(sim, presentation: null)` and asserts it populates a world — proving the headless
  contract.

---

## 4. Data model / on-disk format

### 4.1 `MeshRefKey` (unchanged, `Agapanthe.Core`)

`public readonly record struct MeshRefKey(AssetKey Key, int LocalMesh, int LocalMat)` with
`None => default` and `IsNone => Key.IsNone`.

### 4.2 Snapshot v4 layout (delta from v3)

```
header (40 B, unchanged):  "AGWD" | version=4 | componentCount=13 | universeId(16) | nextGlobalId(8) | entityCount(4)
key table (unchanged mechanism):  keyCount(4) | [entry 0: len(2)=0] | { len(2) | utf8 }...  (ordinal-ascending)
per entity:
  globalId(8)
  mask(4)                    // bit 12 (AssetRef) set; bits 5 (MeshRef) & 11 (InstanceSlot) always clear
  components in ascending index order:
    ... index 12 (AssetRef): keyIdx(4) | localMesh(4) | localMat(4)   // (0,0,0) == None
```

Net per-drawable size: identical to v3 (12 bytes moved from the `MeshRef` slot to the
`AssetRef` slot; the mask bit moves).

### 4.3 Version handling

| On-disk version | v4 loader behaviour |
|---|---|
| 4 | native read, `componentCount` must == 13 |
| 3 | in-place upgrade: `MeshRef`-slot bytes → `AssetRef`; `componentCount` must == 12 |
| 2 | refused — "predates stable asset identity (Contenu-1) … resave" (message kept) |
| 1 | refused — "predates universe identity" (message kept) |
| other | refused — generic message |

---

## 5. Error handling

| Condition | Behaviour |
|---|---|
| `Save` with an entity whose `AssetRef.Value.Key` is `None` | key index 0 written (the `None` sentinel) — a headless world with no identity is legal |
| `Load` (v4 or v3), `keyIdx >= keyTable.Length` | `WorldSerializationException` (checked before short-circuit) |
| `Load`, `resolve` is `null` or `keyIdx == 0` | `AssetRef` restored intact; `MeshRef` = `(Invalid, Invalid)` — no throw (this is the authoritative-server / headless case) |
| `Load`, `resolve` throws (unknown key) | propagates — the resolver contract is "must throw for anything it cannot resolve" (unchanged) |
| v3 file, `componentCount != 12` / v4 file, `componentCount != 13` | `WorldSerializationException` ("the component set changed without a version bump") |
| `Build(sim, null)` on a client recipe | `InvalidOperationException` with the recipe name — explicit, first line of `Build` |
| `RequestRestore` called twice | `InvalidOperationException` — a scene restores at most once |
| `ApplyPendingRestore` with a file the resolver can't fully satisfy | resolver throws → propagates to the host, which fails the run with an actionable message (existing teardown path) |

---

## 6. Testing strategy

### 6.1 Deterministic gates (must hold or be re-pinned with justification)

- `AGAPANTHE_SCENE=model` HDR capture MD5 **`9030f6a64e9587b05d5abb99b487b1b9`** — expected
  **unchanged** (adding `AssetRef` to drawable archetypes changes neither Arch chunk order
  nor `CollectRenderLists` iteration; the render query does not mention `AssetRef`). If it
  moves, re-pin is accepted (structural milestone) and the cause must be explained.
- `planet-drop` HDR `12638eddd7f3f67ab161b298ffbcd15e` / UI `034213575932dabcff41c2e0c72addfa`
  — expected unchanged.
- `HeadlessSim` default + `--drive` snapshot MD5s **change** (version 3→4, `componentCount`,
  `MeshRef` no longer serialized / `AssetRef` serialized). Re-pin both, verify **JIT == AOT**,
  gate in `HeadlessSimSnapshotFormatTests`.
- `tools/AotComponentProbe` published NativeAOT: `RuntimeFeature.IsDynamicCodeSupported ==
  False`, `ComponentRegistry.All.Count == 13`, `AotRootingSmoke` + `AotSerializationSmoke`
  pass. `AotSerializationSmoke` **keeps** a non-`None` `identity` on its smoke spec **and**
  loads with a real resolver — deleting the identify delegate must not drop ILC coverage of
  the resolver invoke, the `AssetKey` ctor, and strict UTF-8 decode. `AotRootingSmoke`
  exercises `AssetRef` (a managed component) through Create / archetype move / query /
  deferred `CommandBuffer` — the managed-struct path runs under AOT, not only JIT.

### 6.2 New tests

| Test | Asserts |
|---|---|
| `Save_Headless_EmitsRealAssetKey` | a world materialised from a spec with a non-`None` identity, saved with **no delegate**, produces a key table containing that `AssetKey` |
| `Load_Headless_KeepsAssetRef_MeshRefInvalid` | same snapshot loaded with `resolve: null` → `AssetRef` intact, `MeshRef` = `(Invalid, Invalid)` |
| `Load_WithResolver_DerivesMeshRef` | loaded with a resolver → `MeshRef` matches the resolver's pair, `AssetRef` unchanged |
| `Snapshot_V3_UpgradesToV4` | a **checked-in v3 fixture `.save` binary** (captured from a pre-W1 build — W1 bumps the count to 13) loads; re-saved as v4; `AssetRef` values correct; second save byte-identical to the first v4 save |
| `Snapshot_V4_RoundTrips_ByteIdentical` | `Save → Load → Save` byte-identical (extend the existing v3 round-trip test) |
| `Save_ClientKeyedContent_RetainsKeysInTable` | a `grid:` and a `planet-challenge` world saved → the key table still contains the ground / probe / beacon `AssetKey`s (the regression finding #2 guards) |
| `AssetRef_ManagedStruct_SurvivesStructuralChange` | Create / structural Add-Remove / query / deferred `CommandBuffer` change with `AssetRef` present — JIT test **and** exercised by `AotRootingSmoke` under the probe |
| `ComponentRegistry_All_MatchesTheFrozenOrder` (updated) | 13-type list, `AssetRef` last |
| `Recipe_BuildsHeadless_WithNullPresentation` | a fixture `ISceneRecipe` populates a world (entities + a `sim.AddSystem`) via `Build(sim, null)` |
| `AotSerializationSmoke` (updated) | round-trip **without** an identify delegate, but **with** a non-`None` identity on the spec and a resolver on load |

### 6.3 Double audit + human visual verdict

`csharp-lowlevel` (managed-struct component under AOT, dead-code removal correctness,
zero-alloc hot path unchanged) + `engine-architect` (the `MeshRef`/`AssetRef` split, the
`SceneContext` split, the delegate deletion). Human visual verdict: `model` and
`planet-challenge` render; a `planet-challenge` F5 save + relaunch with `AGAPANTHE_LOAD`
resumes correctly.

---

## 7. Migration path

- No external consumers. Local `planet-challenge` `.save` files: v3 → auto-upgraded on load
  (D4). A fresh F5 save writes v4. Capture one v3 `.save` binary from the current build
  **before W1 (W1 already bumps the component count to 13)** and check it in as `tests/…/fixtures/world-v3.save` for `Snapshot_V3_UpgradesToV4`.
- `MeshRefIdentifier` deletion blast radius (grep `Identify` / `IdentifyMeshRef` /
  `.Save(`): sources — `WorldSerialization.cs`, `ResourceRegistry.cs`, `ModelKeyIndex.cs`,
  `AppHost.cs:156`, `PlanetChallengeSceneRecipe.cs:66`. Tests — `WorldSerializationTests`,
  `WorldSerializationV3Tests` (~12 `Save(ms, Identify)` sites), `ModelKeyIndexTests` (drop
  `Identify` cases, keep `Resolve`), `RenderStageNeutralityTests`, `AppHostContractTests`.
  Every `Save(x, Identify)` → `Save(x)`; entities that need a real key in the table are built
  via `SpawnImported` with a non-`None` `identity` on the spec.
- `WorldSerializationV3Tests` is retargeted to cover v3-as-legacy-upgrade + v4-native.

---

## 8. Execution outline (waves — for the build phase, not part of this spec's gate)

1. **W0 (spike, ~½ day)** — prove the pinned Arch build handles a **managed struct
   `[Component]`** (`AssetRef` is the project's first; `Parent` is unmanaged): Create,
   archetype move, query, `CommandBuffer` deferred change, bulk set — no boxing on the hot
   path, AOT-clean. If it does not hold cleanly, fall back to decision D1-alt (blittable
   numeric id) and re-review. **Also**: capture a `world-v3.save` binary fixture from the
   current (pre-count-bump) build and check it in. **Gate before W1.**
2. **W1** — `AssetRef` component + `MeshRefKey` reuse + `ComponentRegistry.Root<AssetRef>()`
   + frozen-order test (13) + `ImportedEntitySpec.Identity` (ctor param `= default`) +
   `Materialise*` add `AssetRef` + thread `.Identity` through every by-hand spec copy (§3.2
   table). Build green; no serialization change yet (`AssetRef` present, not written).
3. **W2** — snapshot v4: `SerializationVersion = 4`, `const V3ComponentCount = 12`, write
   `AssetRef` (special-case, index 12), stop writing `MeshRef`, mask bits, `case 12` in the
   three switches + the "N concrete types" comment, v4 read + `InstanceSlot` coupling on the
   `AssetRef` bit, v3 (`version is 3 or 4`) upgrade branch at the identity slot. Delete
   `MeshRefIdentifier` + `Save` overload + `ResourceRegistry.IdentifyMeshRef` +
   `ModelKeyIndex.Identify` (keep the `HashSet` collision guard in `Add`). Migrate every
   `Save(x, Identify)` test call. (The v3 fixture was captured in W0, before the count bump.)
   `HeadlessSim` MD5 re-pinned, JIT == AOT.
4. **W3** — `SceneContext` → `SimSceneContext` + `PresentationSceneContext`,
   `SimSceneContext.AddSystem`, `PresentationSceneContext.Orchestrator`,
   `ISceneRecipe.Build(sim, presentation?)`, `AppHost` two-context construction +
   `AGAPANTHE_SAVE` → `world.Save(saveStream)`, `HostOptions.LoadPath`, `RequestRestore` /
   `ApplyPendingRestore`, delete `PlanetStage.RestoreIfRequested`. Rewrite the 5 recipes
   (`ctx.X` → `sim.X` / `p.X`, systems via `sim.AddSystem`). Fixture headless-recipe test.
5. **W4** — captures verified (expected unchanged) / re-pinned with cause, full `dotnet test`,
   AOT publish JIT == AOT, `AotComponentProbe` count 13, double audit, findings applied,
   converge (`CLAUDE.md`, `AVANCEMENT.md`, `BACKLOG.md`, board).

---

## 9. Decision log

| Decision | Chosen | Alternatives rejected |
|---|---|---|
| Sim-side identity representation | managed `AssetRef` wrapping `MeshRefKey`, special-cased in serialization | numeric blittable id from the manifest (manifest↔snapshot coupling, resolve needs the catalog) · hash id (silent collision) · keep `MeshRef`, fix Save another way (does not lift identity into the sim — contradicts the stated structural decision) |
| `MeshRef` fate | kept as derived render cache, dropped from the snapshot | remove `MeshRef` entirely and have the render path read `AssetRef` + a handle cache (larger render-path change, higher capture risk) |
| `MeshRefIdentifier` | deleted | keep as a no-op / keep for symmetry (dead ceremony) |
| v3 snapshot files | upgraded in place | refused like v1/v2 (loses the local `.save` files for no real gain — the data is isomorphic) |
| `SceneContext` | full split now | `SceneLoader` takes the pieces it needs, `SceneContext` unchanged (leaves the S30 debt, blocks the 3b/3c goal) |
| `HeadlessSim` convergence | plumbing + one test | shared population helper now (designing blind before `SceneLoader` exists) |
| Restore guard | `SimSceneContext` pending-snapshot + one host `ApplyPendingRestore` | leave `PlanetStage.RestoreIfRequested` for 3b |
| `ImportedEntitySpec` identity | one trailing ctor param `= default` | separate parallel array / new `SpawnImported` overload (touches every call site) |
| Contenu-3 shape | 3 sub-milestones, human gate between each | one milestone (audit spanning too many heterogeneous subsystems — the pattern MP-0a explicitly rejected) · 2 sub-milestones |
