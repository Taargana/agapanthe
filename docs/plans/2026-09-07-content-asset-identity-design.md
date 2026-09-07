# Contenu — sub-milestone 1: stable asset identity — Design Spec

> Domain **Contenu** (backlog §4quater "Ensuite", item after `Agapanthe.App`), **decomposed into
> 3 sub-milestones** (like MP-0). Interview: session 31, 2026-09-07. This spec frames the 3 and
> fully designs **Contenu-1**. Spec rev 3 (reviewer rounds 1–2 applied, APPROVED 4.60/5).

## Summary

Give every render asset a **stable, path-based identity** (`AssetKey`), and change the VS-1
snapshot so a `MeshRef` is stored as `(asset key, local mesh index, local material index)` and
re-resolved at load through a caller-supplied delegate — the "Option 2" VS-1 named and deferred.
This ends the "different asset load order → silently wrong drawable" fragility (VS-1 decision 1's
documented limit, made worse by S30) without adding a cook pipeline or an authoring format
(those are Contenu-2 and Contenu-3).

## Context

### The domain, decomposed

| # | Sub-milestone | Delivers | Status |
|---|---|---|---|
| **Contenu-1** | **stable asset identity** | `AssetKey`; `ResourceRegistry` key↔handle map; snapshot **v3** (`MeshRef` = key + local indices + a load-time resolver) | **this spec** |
| Contenu-2 | offline cook + manifest + dependency graph | `tools/AssetCooker` (FontCooker pattern); deterministic keyed binary blobs; glTF import moves offline; a content manifest (key → blob + deps) | framed only |
| Contenu-3 | declarative prefabs & scenes | `.agscene` / `.agprefab` (**authoring** format, NOT a save-game); `SceneLoader`; the 5 Sandbox `ISceneRecipe`s become data | framed only |

Ordering is forced: Contenu-3 scenes reference cooked assets **by key**; Contenu-2 keys its
blobs with the **`AssetKey`** scheme Contenu-1 establishes. Each layer is its own spec + board +
double audit + human verdict, with a human greenlight between them (MP-0 pattern).

### Why Contenu-1 first — the concrete debt

- **`src/Agapanthe.World/WorldSerialization.cs`** writes `MeshRef` (`{ MeshHandle Mesh;
  MaterialHandle Material; }`, `Components.cs:81`, 16 B, `[Sequential]`) with a **raw blittable
  bulk-copy** (`WriteComponent` case 5, `:418`). `MeshHandle`/`MaterialHandle` are
  `(int Index, uint Generation)` — **process-local slots** of `ResourceRegistry`
  (`Handles.cs:14`).
- VS-1 (spec `2026-07-24-vs1-world-serialization-design.md`, **decision 1**): the load contract
  is "Option 1" — the caller re-loads *the same assets in the same order* before `Load`, so
  `ResourceRegistry` mints the same `(Index, Generation)` pairs and the serialized handles
  resolve. **Documented limit: "casse silencieusement si l'ordre de chargement des assets
  change."** VS-1 named the fix — *"Option 2 : `MeshRef` sérialisé comme `(clé, index local)` +
  résolveur au load"* — and deferred it "au jour du streaming/prefabs … une couche qu'on conçoit
  avec un vrai client, pas à l'aveugle." We now have the client (Sandbox + the `Agapanthe.App`
  contract).
- `Generation` is **not validated at load** (backlog `:337`).
- **S30 made it worse**: `PlanetSceneRecipe` / `PlanetDropSceneRecipe` /
  `PlanetChallengeSceneRecipe` load the glTF *after* `PlanetStage.Build` calls
  `SetupPlanetScene`; the pre-extraction `Program.cs` loaded it *before*. A pre-S30 `.save`'s
  handles no longer resolve.

### What exists

- **`src/Agapanthe.Rendering/ResourceRegistry.cs`** — engine-wide GPU-resource owner + handle
  resolver. `Load(GraphicsDevice, ModelAsset, DescriptorSetLayout, Double3 worldOrigin = default)
  → (int ModelId, ImportedEntitySpec[] Specs)`. Global generational `SlotTable<Mesh>` /
  `<Material>`. `LoadedModel(ModelResources, MeshHandle[] MeshHandles, MaterialHandle[]
  MaterialHandles)` per load. `Unload(int)` bumps generations. `Resolve(handle)`. **No notion of
  an asset key; no reverse `handle → source` map.** `GraphicsException` (`Agapanthe.Graphics`)
  is the layer's error type.
- **`src/Agapanthe.World/WorldSerialization.cs`** — `Save(Stream)`, `Load(Stream)` →
  `Load(Stream, SnapshotAllocatorPolicy.AdoptFromHeader)`, `Load(Stream, SnapshotAllocatorPolicy)
  → SnapshotLoadResult`. `private const uint SerializationVersion = 2` (`:39`). Header 40 B:
  `magic | version | componentCount | universeId(16) | nextGlobalId | entityCount`. Entities
  sorted by `GlobalId`, `presenceMask` u32 over `ComponentRegistry.All`. `WriteComponent` /
  `ReadAndAddComponent` are `static` per-index `switch`. `v1` refused with a dedicated message
  (`:196-201`). `internal int AotSerializationSmoke()` (`:335`) — `Save→Load→Save`, asserts
  byte-identity; called by `tools/AotComponentProbe/Program.cs:36`.
  `Agapanthe.World` **does not reference `Agapanthe.Rendering`** (`EngineIsHeadlessTests`).
- **`SnapshotAllocatorPolicy`** (`Agapanthe.World`, no data — `AdoptFromHeader`/`KeepMine`) and
  **`SnapshotLoadResult(UniverseOutcome, int EntityCount)`** — the "the host decides, this layer
  takes no dependency" pattern (MP-0b W4).
- **Callers of `registry.Load`** (all in `samples/Sandbox` after S30): `Content/ModelContent.cs`
  `BuildGroundModel`; `Content/PlanetContent.cs` `SetupPlanetScene` (planet + sun) /
  `SetupPlanetDrop` (probe) / `SetupPlanetChallenge` (probe + beacon); `Scenes/ModelSceneRecipe.cs`
  (the glTF, + the ground); `Scenes/DriveSceneRecipe.cs` (the glTF, + the ground). ~10 call
  sites. Plus the `AGAPANTHE_UNLOAD_TEST` throwaway loads in `ModelSceneRecipe`.
- **Callers of `world.Save` / `world.Load`**: `AppHost.RunClient` (`AGAPANTHE_SAVE`, after
  `recipe.Build`); `PlanetStage.Build` (`AGAPANTHE_LOAD`); `PlanetChallengeSceneRecipe` (F5
  quicksave); `samples/HeadlessSim/Program.cs` (`--save` / `--load`); tests.
- **Determinism pins**: `HeadlessSimSnapshotFormatTests.cs:68` `ExpectedMd5 =
  "7e8dc68f5a25914c84677a7a53ad3a58"` (default `--ticks 600 --bodies 8`), `:147`
  `ExpectedDriveMd5 = "97e786f0455a53d856b9ba4affca1003"` (`--drive`). Sandbox HDR/UI capture
  MD5s `12638edd…` / `03421357…` — **pixel** captures, written with no snapshot, unaffected.
- **Precedents for a deterministic keyed asset**: `tools/FontCooker` (`.ttf` → `.agfont`),
  `tools/ShaderPrecompiler` (GLSL → `.spv`, key = SHA256 of the include-resolved source). This
  milestone adds **neither** — it establishes the key scheme they will use.

### Out of scope (Contenu-2 / -3)

Offline cooker / content manifest / dependency graph; declarative `.agscene` / `.agprefab` /
`SceneLoader`; data-driven item/recipe definitions; GUID identity + `.meta` sidecars;
content-hash identity; texture / environment / font assets in the key scheme (this milestone
covers **mesh + material**, the only things `MeshRef` carries and the only handles the snapshot
holds); a runtime `AssetKey → source path` loader (the recipe still calls `GltfLoader.Load` and
`registry.Load` explicitly); asset hot-reload.

## Design

### Architecture

```
  src/Agapanthe.Core
    AssetKey        readonly record struct — path-based stable id; AssetKey.None
    MeshRefKey      readonly record struct (AssetKey Key, int LocalMesh, int LocalMat)

  src/Agapanthe.Rendering/ResourceRegistry.cs
    Load(device, model, layout, AssetKey key, worldOrigin)   key required, unique per registry
    LoadedModel { …, AssetKey Key }
    public MeshRefKey IdentifyMeshRef(MeshHandle, MaterialHandle)          handle → (key, local, local)
    public (MeshHandle, MaterialHandle) ResolveMeshRef(AssetKey, int, int) (key, local, local) → handles

  src/Agapanthe.World/WorldSerialization.cs        (does NOT reference Rendering)
    delegate MeshRefKey MeshRefIdentifier(MeshHandle mesh, MaterialHandle material)
    delegate (MeshHandle, MaterialHandle) MeshRefResolver(AssetKey key, int localMesh, int localMat)
    Save(Stream, MeshRefIdentifier? identify = null)
    Load(Stream, SnapshotAllocatorPolicy, MeshRefResolver? resolve = null)
    snapshot v3: header version=3 + key table + MeshRef = (u32 keyIdx, u32 localMesh, u32 localMat)

  samples/Sandbox    every registry.Load gains an AssetKey; AGAPANTHE_SAVE/_LOAD wire the delegates
  samples/HeadlessSim    unchanged — Save/Load with no delegate → MeshRef ⇄ AssetKey.None ⇄ Invalid
```

`Agapanthe.Rendering` already references `Agapanthe.Core`; `Agapanthe.World` already references
`Agapanthe.Core`. The `MeshRefIdentifier` / `MeshRefResolver` signatures name only `Core` types
(`AssetKey`, `MeshRefKey`) and `Core` handle types (`MeshHandle`, `MaterialHandle`) — no new
project edge, `EngineIsHeadlessTests` unaffected.

### Components

| Component | Responsibility | File | Notes |
|---|---|---|---|
| `AssetKey` | Path-based stable identity of a render asset. | `src/Agapanthe.Core/AssetKey.cs` (new) | `readonly record struct`; `string? Value` (null **iff** `None`/`default`); ctor normalises `\`→`/`, trims, collapses `//`, rejects null/empty/whitespace and `.` / `..` segments and a leading `/` (`Normalise` never yields `""`); `AssetKey.None => default`; `IsNone => string.IsNullOrEmpty(Value)`; `ToString()` → `"<none>"` for None. |
| `MeshRefKey` | The serialised identity of one drawable: which asset + which local mesh/material within it. | `src/Agapanthe.Core/MeshRefKey.cs` (new, or in `AssetKey.cs`) | `readonly record struct MeshRefKey(AssetKey Key, int LocalMesh, int LocalMat)`. `MeshRefKey.None` = `(AssetKey.None, 0, 0)`. |
| `ModelKeyIndex` | **GPU-free** bookkeeping: `AssetKey` ⇄ handles, no device. Holds `Dictionary<AssetKey, Entry>` where `Entry(MeshHandle[] Meshes, MaterialHandle[] Materials)`, plus reverse `Dictionary<MeshHandle, (AssetKey Key, int Local)>` and `Dictionary<MaterialHandle, (AssetKey Key, int Local)>`. `Add(key, meshHandles, materialHandles)` / `Remove(key)` / `Identify(mesh, material) → MeshRefKey` / `Resolve(key, localMesh, localMat) → (MeshHandle, MaterialHandle)`. | `src/Agapanthe.Rendering/ModelKeyIndex.cs` (new) | Extracted so tests 5–8 run without a `GraphicsDevice` (the suite has none). `ResourceRegistry` **composes** one; its own methods forward. |
| `ResourceRegistry.Load` | + `AssetKey key` param (required, non-`None`, unique per registry). | `src/Agapanthe.Rendering/ResourceRegistry.cs` (modify) | **Validate `key` at the TOP of `Load`, before `new DescriptorAllocator(device)`**: `key.IsNone` → `ArgumentException`; `_keyIndex` already holds `key` → `GraphicsException`. (Validating after `SceneBuilder.Build` + `_models.Add` would leak the uploaded model on a duplicate — the exact class `:86-88` guards against.) `LoadedModel` gains `AssetKey Key`. `_keyIndex.Add(key, meshHandles, materialHandles)` is then the **last** statement — a pure insert that cannot throw (the key was already proven non-`None` and absent). Signature: `Load(GraphicsDevice, ModelAsset, DescriptorSetLayout, AssetKey key, Double3 worldOrigin = default)`. |
| `ResourceRegistry.IdentifyMeshRef` | `public MeshRefKey IdentifyMeshRef(MeshHandle mesh, MaterialHandle material)` → `_keyIndex.Identify(mesh, material)`. | same | Invariant: `mesh` and `material` from the **same** model (a drawable = one glTF); handle from no loaded model, or a cross-model pair → `GraphicsException`. |
| `ResourceRegistry.ResolveMeshRef` | `public (MeshHandle Mesh, MaterialHandle Material) ResolveMeshRef(AssetKey key, int localMesh, int localMat)` → `_keyIndex.Resolve(...)`. | same | `key` not loaded, or index out of range → `GraphicsException` ("snapshot references asset '{key}' which is not loaded — load it before restoring"). |
| `ResourceRegistry.Unload` / `Dispose` | each freed model also `_keyIndex.Remove(loaded.Key)` (which drops the forward entry AND its reverse handle entries). | same | `Unload` removing the key is what makes test 5's "the key is reusable after `Unload`" hold. `Dispose` clears the whole `_keyIndex`. `_models` ids stay non-recycled (`_models[id] = null`) — unchanged. |
| `MeshRefIdentifier` / `MeshRefResolver` | The `World`↔`Rendering` seam delegates (see §Interfaces). | `src/Agapanthe.World/WorldSerialization.cs` (new delegates) | `MeshRef` is `internal` → `MeshRefResolver` returns a handle pair, the serialiser builds the `MeshRef`. |
| `Save` / `Load` | v3 format: key table + indexed `MeshRef`. Optional `identify` / `resolve` delegates. | `src/Agapanthe.World/WorldSerialization.cs` (modify) | `SerializationVersion = 3`. Existing `Save(Stream)` / `Load(Stream)` / `Load(Stream, policy)` overloads kept (delegate `null`). v2 refused. |
| Sandbox callers | Every `registry.Load` names its asset; `AGAPANTHE_SAVE`/`_LOAD` wire the delegates. | `samples/Sandbox/Content/*`, `Scenes/*`, `src/Agapanthe.App/AppHost.cs` (the `AGAPANTHE_SAVE` block) | `world.Save(stream, ctx.Registry.IdentifyMeshRef)` (method-group → `MeshRefIdentifier`); `world.Load(stream, policy, ctx.Registry.ResolveMeshRef)`. |
| `HeadlessSimSnapshotFormatTests` | Re-pin both MD5s (v3 changes the `MeshRef` bytes). | `tests/Agapanthe.Tests/HeadlessSimSnapshotFormatTests.cs` (modify) | New `ExpectedMd5` + `ExpectedDriveMd5`, verified JIT == NativeAOT. |
| `AotComponentProbe` | `AotSerializationSmoke` still round-trips (no resolver → `MeshRef` ⇄ `None`). | `src/Agapanthe.World/WorldSerialization.cs` `AotSerializationSmoke` (verify), `tools/AotComponentProbe` (re-run) | The smoke asserts byte-identity of re-save; `None` round-trips to `None`. Update the smoke's expected entity count / comment if needed. |

### Data model

```csharp
// src/Agapanthe.Core/AssetKey.cs
namespace Agapanthe.Core;

/// <summary>A stable, path-based identity for a render asset — the key a snapshot stores so a
/// MeshRef survives a change in asset load ORDER (VS-1 "Option 2"). NOT a file path the runtime
/// opens, NOT a content hash: renaming an asset is a deliberate act that invalidates old saves.
/// <para><see cref="None"/> is literally <c>default(AssetKey)</c> — the ONE unconstructable state,
/// with <see cref="Value"/> null. The ctor rejects null/empty/whitespace, so a value-bearing key
/// can never collide with None, and record-struct equality gives <c>default == None</c> for free.</para></summary>
public readonly record struct AssetKey
{
    /// <summary>The normalised key, e.g. "models/DamagedHelmet.glb" or "sandbox/planet-surface".
    /// <c>null</c> iff this is <see cref="None"/> / <c>default</c>.</summary>
    public string? Value { get; }

    public AssetKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = Normalise(value); // '\'→'/', trim, collapse '//', reject '.'/'..' segments and a leading '/'
                                  //   → never returns "" (whitespace already rejected)
    }

    /// <summary>The sentinel "no asset" — how an unresolvable/headless MeshRef round-trips.
    /// <c>AssetKey.None == default(AssetKey)</c>.</summary>
    public static AssetKey None => default;

    public bool IsNone => string.IsNullOrEmpty(Value);

    public override string ToString() => IsNone ? "<none>" : Value!;

    private static string Normalise(string value) { /* … throws FormatException on a bad segment … */ }
}

// src/Agapanthe.Core/MeshRefKey.cs
public readonly record struct MeshRefKey(AssetKey Key, int LocalMesh, int LocalMat)
{
    public static MeshRefKey None => default; // (default(AssetKey), 0, 0)
    public bool IsNone => Key.IsNone;
}
```

**One key type everywhere.** `ResourceRegistry`'s `Dictionary<AssetKey, …>` and the snapshot
writer's `key → u32 index` map are **both keyed by `AssetKey`** (never by `string`). `AssetKey`
is a `record struct` over a nullable string, so `EqualityComparer<AssetKey>.Default` and
`GetHashCode` are well-defined for both value-bearing keys and `default`/`None`.

**Snapshot v3 layout** (`WorldSerialization.cs`):

```
header (40 B, UNCHANGED size)
  magic (4) | version = 3 (u32) | componentCount (u32) | universeId.High (u64) | universeId.Low (u64)
  | nextGlobalId (u64) | entityCount (u32)

key table  (NEW, immediately after the header)
  keyCount (u32)                     — always >= 1
  keyCount × { byteLen (u16) | UTF-8 bytes }
     entry 0 = AssetKey.None, written as byteLen = 0 (no bytes); entries 1.. sorted ORDINAL by Value

entities   (UNCHANGED except MeshRef)
  entityCount × {
    globalId (u64) | presenceMask (u32)
    for each set component index, in registry order:
      … existing blittable payloads …
      MeshRef (index 5):  keyIdx (u32) | localMesh (u32) | localMat (u32)     — was 16 B blittable
  }
```

- `keyIdx == 0` ⇒ `AssetKey.None`. A snapshot with no drawables still writes `keyCount = 1`.
- Ordinal sort → byte-identical re-save (`Save(Load(x)) == x` stays a gate).

### Interfaces / behaviour

**`ResourceRegistry`** (all `public`):

```csharp
public (int ModelId, ImportedEntitySpec[] Specs) Load(
    GraphicsDevice device, ModelAsset model, DescriptorSetLayout materialSetLayout,
    AssetKey key, Double3 worldOrigin = default);
// key.IsNone  → ArgumentException
// key already loaded (not Unloaded) → GraphicsException($"asset key '{key}' is already loaded")

public MeshRefKey IdentifyMeshRef(MeshHandle mesh, MaterialHandle material);
// mesh in no loaded model → GraphicsException
// material in a different model than mesh → GraphicsException
// mesh from a model whose key was never set (impossible after this change) → GraphicsException

public (MeshHandle Mesh, MaterialHandle Material) ResolveMeshRef(
    AssetKey key, int localMesh, int localMat);
// key not loaded → GraphicsException($"snapshot references asset '{key}' which is not loaded")
// localMesh / localMat out of range for that model → GraphicsException
```

**`WorldSerialization`**:

```csharp
public delegate MeshRefKey MeshRefIdentifier(MeshHandle mesh, MaterialHandle material);
public delegate (MeshHandle Mesh, MaterialHandle Material) MeshRefResolver(
    AssetKey key, int localMesh, int localMat);

public void Save(Stream stream, MeshRefIdentifier? identify = null);
public SnapshotLoadResult Load(Stream stream);                                    // → AdoptFromHeader, resolve = null
public SnapshotLoadResult Load(Stream stream, SnapshotAllocatorPolicy policy);    // resolve = null
public SnapshotLoadResult Load(Stream stream, SnapshotAllocatorPolicy policy, MeshRefResolver? resolve);
```

**The static dispatch seam changes.** Today `WriteComponent(Stream, Entity, int index)` /
`ReadAndAddComponent(Stream, Entity, int index)` are `private static void` and case 5 is
`WriteBlittable(s, e.Get<MeshRef>())` / `e.Add(ReadBlittable<MeshRef>(s))`. v3:

```csharp
// case 5 is pulled OUT of the generic dispatch (it is no longer a blittable), the rest unchanged:
private static void WriteMeshRef(Stream s, in MeshRefKey k, IReadOnlyDictionary<AssetKey, uint> keyIndex)
{
    WriteU32(s, k.IsNone ? 0u : keyIndex[k.Key]);
    WriteU32(s, (uint)k.LocalMesh);
    WriteU32(s, (uint)k.LocalMat);
}
private static MeshRef ReadMeshRef(Stream s, AssetKey[] keyTable, MeshRefResolver? resolve)
{
    var keyIdx = ReadU32(s); var localMesh = ReadU32(s); var localMat = ReadU32(s);
    if (keyIdx == 0 || resolve is null)
        return new MeshRef { Mesh = MeshHandle.Invalid, Material = MaterialHandle.Invalid };  // (-1, 0)
    if (keyIdx >= (uint)keyTable.Length) throw new WorldSerializationException($"MeshRef key index {keyIdx} out of range.");
    var (m, mat) = resolve(keyTable[keyIdx], (int)localMesh, (int)localMat);
    return new MeshRef { Mesh = m, Material = mat };
}
```

`WriteComponent`/`ReadAndAddComponent` keep their signatures but **case 5 is deleted** from both
(index 5 now falls through to the `default:` `WorldSerializationException` — an accidental
generic dispatch of `MeshRef` is loud, never a stray 16-byte blittable in a v3 stream). The
entity loop calls `WriteMeshRef` / `ReadMeshRef` directly for `index == MeshRefIndex`: the
**Load** loop already special-cases `ParentIndex` this way (`:288`); the **Save** loop
(`:125-131`) gains its first such branch. The no-resolver path writes `MeshHandle.Invalid` =
`(-1, 0)` explicitly
(a bare `new MeshRef()` would be `(0, 0)`, which resolving later would *not* reject — test 11
pins `MeshHandle.Invalid`).

**`Save` data flow**:
1. `FlushStructuralChanges()`; gather + sort entities by `GlobalId` into `entities` (unchanged).
2. **Pass 1 — key table.** Allocate `var meshKeys = new MeshRefKey?[entities.Count];`. For
   `i` in `0..entities.Count`: if `entities[i].Entity` has `MeshRef`, `meshKeys[i] =
   identify is null ? MeshRefKey.None : identify(mr.Mesh, mr.Material)` and add
   `meshKeys[i].Value.Key` to `var distinct = new SortedSet<AssetKey>(AssetKeyOrdinalComparer)`
   unless `IsNone`. Write `keyCount = distinct.Count + 1`; write index 0 as `byteLen = 0`
   (`AssetKey.None`); write each of `distinct` (`byteLen` + UTF-8 of `.Value`). Build
   `Dictionary<AssetKey, uint> keyIndex` from the written positions.
3. **Pass 2 — entities.** `for (var i = 0; i < entities.Count; i++)` (an INDEXED loop, so it can
   read `meshKeys[i]`) — the existing per-component inner loop, except `index == MeshRefIndex`
   calls `WriteMeshRef(s, meshKeys[i] ?? MeshRefKey.None, keyIndex)`. Every other component
   byte-for-byte as v2.

`AssetKeyOrdinalComparer` = `Comparer<AssetKey>.Create((a, b) => string.CompareOrdinal(a.Value,
b.Value))` — **ordinal**, so the byte layout is stable across locales (the plain
`SortedSet<string>()` / `SortedSet<AssetKey>()` default comparer is culture-sensitive and would
break `Save(Load(x)) == x` on a non-invariant machine).

**`Load` data flow**:
1. Read + validate the header (`version == 3`; `1`/`2` → the dedicated rejection). No mutation yet.
2. Read the key table into `AssetKey[] keyTable` — `keyTable[0] = AssetKey.None` (`byteLen == 0`
   asserted), the rest `new AssetKey(Encoding.UTF8.GetString(...))` (a malformed UTF-8 run or a
   segment the ctor rejects → `WorldSerializationException`, wrapping the `FormatException`).
3. Existing entity loop; `index == MeshRefIndex` → `ReadMeshRef(s, keyTable, resolve)` (above),
   then `entity.Add(that)`.
4. `InstanceSlot` re-add on `MeshRef` presence, pass-2 parent links, `SnapshotLoadResult` —
   unchanged.

### Callers — the wiring

- `samples/Sandbox/Content/ModelContent.cs` `BuildGroundModel` is called by the recipes, which
  pass the key: `registry.Load(device, ModelContent.BuildGroundModel(size), layout,
  new AssetKey("sandbox/ground"), origin)`.
- `Content/PlanetContent.cs`: `SetupPlanetScene` → `new AssetKey("sandbox/planet-surface")` /
  `"sandbox/sun-surface"`; `SetupPlanetDrop`/`SetupPlanetChallenge` probe →
  `"sandbox/probe"`; beacon → `"sandbox/beacon"`. (Same key across drop/challenge is fine — they
  never coexist.)
- `Scenes/ModelSceneRecipe.cs` / `DriveSceneRecipe.cs`: the glTF →
  `new AssetKey($"models/{Path.GetFileName(modelPath)}")`. The `AGAPANTHE_UNLOAD_TEST` throwaway
  loads already `Unload` each cycle (`ModelSceneRecipe.cs`), so they reuse one key
  (`AssetKey("models/<name>#unload")`) — `Load` + `Unload` N times, the key freed each time.
  Moved in W1 with the other call sites.
- `src/Agapanthe.App/AppHost.cs`, the `AGAPANTHE_SAVE` block (`:139`, in the same `window.Loaded`
  lambda, after `recipe.Build`): `world.Save(saveStream)` → `world.Save(saveStream,
  registry.IdentifyMeshRef)`. `registry` is the lambda local (declared `:54`, assigned `:86`);
  definite-assignment makes `ResourceRegistry?` non-null there, so the method-group conversion to
  `MeshRefIdentifier` needs no null check.
- `samples/Sandbox/Content/PlanetStage.cs` `Build`, the `AGAPANTHE_LOAD` block: `world.Load(
  loadStream, SnapshotAllocatorPolicy.AdoptFromHeader, ctx.Registry.ResolveMeshRef)`.
- `samples/Sandbox/Scenes/PlanetChallengeSceneRecipe.cs` F5 quicksave: `world.Save(fs,
  ctx.Registry.IdentifyMeshRef)`.
- `samples/HeadlessSim/Program.cs`: **unchanged** — `world.Save(output)` / `world.Load(input)`,
  no delegate. Its bodies' `new MeshHandle(0, 1)` serialise as `None`, load back as `Invalid`.

## Error Handling

| Failure | Handling |
|---|---|
| `new AssetKey("")` / whitespace / `"a/../b"` / `"/abs"` | `ArgumentException` / `FormatException` at construction — a bad key is a programming error, caught at the call site, not at save time. |
| `registry.Load` with a key already loaded | `GraphicsException` — a recipe registering the same asset twice is a bug. |
| `Save` with `identify` given, but a `MeshRef` holds a handle from no loaded model | `identify` (i.e. `IdentifyMeshRef`) throws `GraphicsException`; it propagates out of `Save` (the caller's save fails loudly, no partial file trusted). |
| `Save` with `identify == null` and the world has drawables | Every `MeshRef` → `MeshRefKey.None`. Legitimate for a headless world; **lossy** for a client that forgot the delegate (the reload draws nothing) — documented, and the human verdict for the planet scenes catches it. |
| `Load` v3 references asset key `X`, `resolve` given, `X` not loaded | `resolve` (i.e. `ResolveMeshRef`) throws `GraphicsException`; the loader is mid-populate, so this world is discarded (Load is all-or-nothing by contract, `:171-173`) — same as any malformed-stream throw. |
| `Load` v3, `resolve == null`, snapshot has non-`None` keys | Every `MeshRef` → `Invalid` handles. A headless load of a client save: the entities restore, nothing draws (there is no renderer). Fine. A client that forgot the delegate: `CollectRenderLists` → `registry.Resolve(Invalid)` → `GraphicsException` on the first frame. Loud, not silent. |
| `Load` sees `version == 2` (or 1) | `WorldSerializationException`: "Snapshot is format v2, which predates stable asset identity (Contenu-1). There is no automatic upgrade — resave it with this build first." (mirrors the v1 message at `:198`). |
| Key table `keyCount == 0`, or a `keyIdx` ≥ `keyCount`, or a truncated UTF-8 run | `WorldSerializationException` (the existing malformed-stream guard style — validate before use, never index out of range). |

## Testing Strategy

New: `tests/Agapanthe.Tests/AssetKeyTests.cs`, `ModelKeyIndexTests.cs`. Modify (rename):
`WorldSerializationV2Tests.cs` → **`WorldSerializationV3Tests.cs`** (its version cases change,
and the new v3 round-trip / key-table cases fold in — one file). Modify:
`WorldSerializationTests.Load_RejectsOutOfRangeMaskBit`, `HeadlessSimSnapshotFormatTests.cs`
(re-pin). **All GPU-free** — the identity bookkeeping is `ModelKeyIndex`, which takes plain
`MeshHandle[]` / `MaterialHandle[]`, no `GraphicsDevice` (the suite has never created one, and
there are no `[Trait]`-marked renderer tests to imitate — this is why the extraction exists).

| # | Test | Cases (input → expected) |
|---|---|---|
| 1 | `AssetKey_Normalises` | `new AssetKey("models\\x.glb")` → `Value == "models/x.glb"`; `" a/b "` → `"a/b"`; `"a//b"` → `"a/b"`. |
| 2 | `AssetKey_RejectsBadInput` | `""` / `"  "` → `ArgumentException`; `"a/../b"`, `"a/./b"`, `"/abs"` → `FormatException`. |
| 3 | `AssetKey_None_Is_Default` | `AssetKey.None == default(AssetKey)`; `AssetKey.None.IsNone` true; `default(AssetKey).IsNone` true; `AssetKey.None.Value is null`; `AssetKey.None.ToString() == "<none>"`; `new AssetKey("x") != AssetKey.None`. |
| 4 | `AssetKey_EqualityAndDictionaryKey` | two `new AssetKey("models/x.glb")` equal + same hash; a `Dictionary<AssetKey, int>` with both a value key and `AssetKey.None` works (`None` and `default` collapse to one entry). |
| 5 | `ModelKeyIndex_Add_RejectsNoneAndDuplicate` | `Add(AssetKey.None, …)` → `ArgumentException`; `Add("m/a", …)` twice → `GraphicsException` on the second; after `Remove("m/a")`, `Add("m/a", …)` again succeeds. |
| 6 | `ModelKeyIndex_IdentifyThenResolve_RoundTrips` | `Add("m/a", [M0,M1], [T0,T1])`; `Identify(M1, T0)` → `MeshRefKey("m/a", 1, 0)`; `Resolve("m/a", 1, 0)` → `(M1, T0)`. |
| 7 | `ModelKeyIndex_UnknownOrCrossModel_Throws` | `Identify(new MeshHandle(999,0), default)` → `GraphicsException`; `Add("m/a", [M0], [T0])` + `Add("m/b", [M1], [T1])`, `Identify(M0, T1)` (cross-model) → `GraphicsException`; `Resolve(new AssetKey("nope"), 0, 0)` → `GraphicsException`; `Resolve("m/a", 5, 0)` (index OOR) → `GraphicsException`. |
| 8 | `ModelKeyIndex_RemoveThenReAdd_ResolvesFreshHandles` | `Add("m/a", [Mold], [Told])` → `Identify(Mold, Told)` OK; `Remove("m/a")` → `Identify(Mold, Told)` now throws; `Add("m/a", [Mnew], [Tnew])` → `Resolve("m/a", 0, 0)` → `(Mnew, Tnew)` ≠ old. **This is the bug the milestone fixes** — a different load order re-resolves by key. |
| 9 | `WorldSerializationV3_Header_IsVersion3` | a fresh `Save` → bytes 4..8 == `3`. |
| 10 | `WorldSerializationV3_RefusesV2` | hand-craft a v2 header → `Load` throws `WorldSerializationException` naming v2. |
| 11 | `WorldSerializationV3_NoResolver_MeshRefBecomesInvalid` | world with a drawable (any handle) → `Save(stream)` (no `identify`) → `Load(stream)` (no `resolve`) → the restored entity's `MeshRef.Mesh == MeshHandle.Invalid`. Key table has exactly 1 entry (`None`). |
| 12 | `WorldSerializationV3_WithDelegates_RoundTripsThroughKeys` | fake `identify` returns `("a", 2, 5)` for a known handle, `("b", 0, 0)` for another; `Save` → key table `["", "a", "b"]` (ordinal); `Load` with a `resolve` that maps `("a",2,5)→H1`, `("b",0,0)→H2` → the two entities get `H1` / `H2`. Assert `Save(Load(x)) == x` (byte-identical). |
| 13 | `WorldSerializationV3_Save_IsByteIdenticalRunToRun` | same world, two `Save` (with a deterministic `identify`) → identical byte arrays (ordinal key-table sort). |
| 14 | `WorldSerializationV3_ResolverThatThrows_PropagatesAndLeavesNoUsableWorld` | `resolve` throws for one key → `Load` throws; the world is not returned in a half state. |
| 15 | `AotSerializationSmoke` (existing, `WorldSerialization.cs:335`) | still passes — no resolver, `MeshRef` ⇄ `MeshHandle.Invalid`, re-save byte-identical (the smoke asserts `Save(Load(a)) == Save(a)`, and `Invalid` → `None`-index `0` → `Invalid` on both saves). Its `MeshHandle(1,2)` etc. inputs (`WorldSerialization.cs:343-350`) now serialise as key-index 0; the restored count is unchanged. |
| 16 | `HeadlessSimSnapshotFormatTests.cs` (existing, `:68`/`:147`) | **re-pin** `ExpectedMd5` and `ExpectedDriveMd5` to the v3 values; assert JIT == NativeAOT (the test already does the AOT half). |
| 17 | `WorldSerializationV2Tests.cs` (existing → V3) | `:32` `Save_WritesVersion2` → assert `3u`, rename `Save_WritesVersion3`; `:39` `Load_RefusesV1…` keep + add a `Load_RefusesV2` sibling (craft a v3 save, set bytes 4..8 to `2`, assert the dedicated message names "v2"). |
| 18 | `WorldSerializationTests.Load_RejectsOutOfRangeMaskBit` (existing, `:156-164`) | the hard-coded `bytes[51]` assumed entities begin at offset 40; the key table now sits there. Re-derive the first entity's offset (`40 + 4 + Σ key-table entry sizes`) or parse it, then set the mask MSB. |

**Manual / CI gates:**
- `dotnet build` 0 warning · `dotnet test` green (+ ~15 new).
- HDR `12638eddd7f3f67ab161b298ffbcd15e` / UI `034213575932dabcff41c2e0c72addfa` — **unchanged**
  (pixel captures, no snapshot).
- `HeadlessSim` default + `--drive` snapshot MD5s: **new, pinned**, JIT == NativeAOT.
- `AotComponentProbe` publish + run PASS · NativeAOT publish of `HeadlessSim` PASS.
- `EngineIsHeadlessTests` green (no new project edge).
- Double audit (`csharp-lowlevel` + `engine-architect`).
- **Human visual verdict**: `AGAPANTHE_SCENE=planet-challenge AGAPANTHE_SAVE=ch.save` → fly, drop
  a couple of probes, `F5`; relaunch `AGAPANTHE_SCENE=planet-challenge AGAPANTHE_LOAD=ch.save` →
  the beacon + probes restore in place and render (not invisible). Same for `planet-drop` with
  `F5`? (no F5 there — use `AGAPANTHE_SAVE` + relaunch `AGAPANTHE_LOAD`).

## Migration Path

Four waves, each ends green with the HDR/UI captures unchanged.

| Wave | Content | Gate |
|---|---|---|
| **W1** | `AssetKey` + `MeshRefKey` (Core) + tests 1–4. `ModelKeyIndex` (Rendering, GPU-free) + tests 5–8. `ResourceRegistry`: `Load` gains a **required** `AssetKey key`, `LoadedModel.Key`, composes a `ModelKeyIndex`, `IdentifyMeshRef` / `ResolveMeshRef` forward to it, `Unload`/`Dispose` remove/clear it. **Because `key` is required, every `registry.Load` call site moves in this wave** (~10, all in `samples/Sandbox` — `Content/ModelContent`, `Content/PlanetContent` ×4, `ModelSceneRecipe`, `DriveSceneRecipe`, `AGAPANTHE_UNLOAD_TEST`). Mechanical, no behaviour change. *(Making the param optional would let this churn defer to W3 — not chosen: an unkeyed load has no honest default and would just re-introduce the ambiguity.)* | build 0 warn · test green · captures + `HeadlessSim` snapshots **unchanged** (still v2; `MeshRef` bytes untouched). |
| **W2** | `WorldSerialization` v3: `SerializationVersion = 3`, key table, `MeshRef` = `(keyIdx, localMesh, localMat)`, the 2 delegates, the overloads, v2 refusal. Tests 9–14. `Save(Stream)` / `Load(Stream[, policy])` still compile for every current caller (delegate `null`). | build 0 warn · tests 9–14 green · **`HeadlessSim` snapshots change** — record the new MD5s, verify JIT == AOT, re-pin (test 16). `AotSerializationSmoke` (test 15) green. |
| **W3** | Wire the delegates: `AppHost` `AGAPANTHE_SAVE` → `Save(stream, registry.IdentifyMeshRef)`; `PlanetStage` `AGAPANTHE_LOAD` + `PlanetChallengeSceneRecipe` F5 → `Load(stream, policy, registry.ResolveMeshRef)`. | `planet-challenge` / `planet-drop` save → relaunch `AGAPANTHE_LOAD` restores + **renders** (0 leak, 0 validation). `model` / `grid:` / `drive` launch clean. Captures ×3 unchanged. |
| **W4** | Mandatory tail: Self Code Review → Requirements Validation → Full Project Verification + double audit + human verdict. CONVERGE: `CLAUDE.md` (§Contenu, dette), `docs/AVANCEMENT.md` (§Reprise → Contenu-2), `docs/BACKLOG.md` (§4quater — the 3-way decomposition, VS-1 debt `:337` marked delivered), this spec's §Execution outcome, archive the board, suggest a commit. | all gates; audits applied; board `completed`. |

**Rollback**: W1's tree-clean hash. Each wave is a commit-able unit.

## Resolved during review (were open questions)

- **`default(AssetKey)` vs `AssetKey.None`** → `AssetKey.None => default`, `Value` is `string?`
  and null iff `None`, `IsNone => string.IsNullOrEmpty(Value)`, the ctor rejects null/empty/
  whitespace and `Normalise` never returns `""`. So `default == None` is true, a value-bearing
  key can never equal `None`, and one key type (`AssetKey`) is used for both maps. Pinned by test 3.
- **GPU-free identity tests** → the bookkeeping is `ModelKeyIndex` (plain handle arrays, no
  `GraphicsDevice`); tests 5–8 exercise it directly. `ResourceRegistry` composes one.
- **`AGAPANTHE_UNLOAD_TEST`** → the throwaway loads `Unload` immediately (they already do,
  `ModelSceneRecipe.cs`), so the key frees each cycle; they reuse one key
  (`AssetKey("models/<name>#unload")`), `Load` + `Unload` N times.

## Open Questions

None.

## Decision Log

| Decision | Options considered | Chosen | Rationale |
|---|---|---|---|
| Domain structure | one big milestone (3 layers); **decompose into 3 sub-milestones**; just layer 1 | **decompose into 3** | 25–35 tasks across 3 heterogeneous subsystems — the exact shape MP-0 was decomposed for ("un double audit sur des sous-systèmes hétérogènes ne vaut rien"). Sequential dependency (scenes → cooked assets by key → the key scheme). Spec frames all 3; executes Contenu-1. |
| Identity scheme | **path-based key**; GUID + `.meta` sidecar; content-hash | **path-based key** | No sidecar infra; the Sandbox assets already have logical names; the cooker (Contenu-2) keys its blobs the same way. Identity ≠ content — a rename is a deliberate save-invalidating act (Godot `res://` model). GUID is the "designed with an editor" version — deferred. Content-hash breaks a save on any deliberate re-cook. |
| `AssetKey` type | `readonly record struct` in `Core`; bare `string`; hashed `u64` | **`readonly record struct` in `Core`** | Type safety (a file path ≠ an asset key), one normalisation point, free `IEquatable`. In `Core`: both `World` (serialises) and `Rendering` (resolves) reference it. The per-file key table already solves compactness, so no need for a lossy `u64` hash. |
| Procedural assets (no source file) | caller names them; hash of build params | **caller names them** | Explicit and controlled; the recipe knows what it built. A param hash is opaque in a save and couples the key to the build code. |
| `World`↔`Rendering` seam | **delegates on `Save`/`Load`**; an `IAssetResolver` interface in `Core` | **delegates** | Matches `SnapshotAllocatorPolicy` / `SnapshotLoadResult` — "the host decides, this layer takes no dependency". An interface is more ceremony for two functions; revisit if the resolver grows (textures/env/audio) in Contenu-2. |
| Headless / no-resolver case | **`AssetKey.None` sentinel**; `HeadlessSim` switches bodies to `Invalid`; `Save` requires a resolver when drawables exist | **`AssetKey.None` sentinel** | A server legitimately has no assets — it should not need a trivial resolver. `HeadlessSim` stays untouched. A resolver that *is* given must throw on an unknown handle (never a silent `None`) — that keeps the client path strict. |
| v2 snapshots | **refuse with a dedicated message**; best-effort upgrade to `None` | **refuse** | Consistent with v1→v2. `.save` files in the wild are quicksaves / dev saves, all relaunch-only. A best-effort upgrade produces a world with silently-broken drawables — the exact bug being fixed. |
| `AssetKey.None` representation | `None` = `new(false)` with `Value = ""`; **`None` = `default(AssetKey)`, `Value` null** | **`None` = `default`** | `record struct` `==` compares `Value` with `EqualityComparer<string>.Default` → `"" != null`, so a `new(false)` sentinel would make `default != None` and a `MeshRefKey` arriving as `default` would miss the sentinel. `None => default` + `IsNone => IsNullOrEmpty` makes them one state, and the ctor (rejects empty/whitespace, `Normalise` never yields `""`) guarantees no value-bearing key collides. |
| GPU-free identity tests | mark device-backed tests `[Trait("gpu")]`; **extract `ModelKeyIndex`** | **extract `ModelKeyIndex`** | The test suite has never instantiated a `GraphicsDevice` and there is no `[Trait]` renderer-test convention to follow. The `AssetKey`↔handle bookkeeping is pure — a small class over `MeshHandle[]`/`MaterialHandle[]` that `ResourceRegistry` composes. Tests 5–8 stay unit tests. |
| Snapshot key type | `string` map in `Save`, `AssetKey` map in `ResourceRegistry`; **`AssetKey` everywhere** | **`AssetKey` everywhere** | Two keying conventions for one concept is exactly what makes `default(AssetKey)` dangerous. `AssetKey` (a `record struct` over `string?`) has a well-defined `GetHashCode`/`Equals` for both cases; a custom ordinal `Comparer<AssetKey>` gives the deterministic sort. |

## Execution outcome (2026-09-07, session 31)

Delivered in 4 waves + mandatory tail, board `.absolute-work/archive/board-session31-Contenu1.md`.

- **W1** — `AssetKey` + `MeshRefKey` (`Core`), `ModelKeyIndex` (`Rendering`, GPU-free),
  `ResourceRegistry.Load(…, AssetKey key, …)` with the key validated before any GPU side effect,
  10 Sandbox call sites keyed. No behaviour change; captures + HeadlessSim snapshots unchanged.
- **W2** — snapshot **v3**: `SerializationVersion = 3`, `MeshRefIdentifier` / `MeshRefResolver`
  delegates (name only `Core` types — no `World → Rendering` edge), ordinal key table after the
  40-byte header, `MeshRef` = `keyIdx | localMesh | localMat` (16 B → 12 B), `WriteComponent` /
  `ReadAndAddComponent` case 5 removed, **v1 and v2** refused with dedicated messages.
  `WorldSerializationV2Tests.cs` → `V3Tests.cs`. HeadlessSim re-pinned JIT == NativeAOT:
  `80ced166…` (1842 B) / `cf01492e…` (210 B).
- **W3** — `AGAPANTHE_SAVE` / F5 → `registry.IdentifyMeshRef`; `AGAPANTHE_LOAD` →
  `registry.ResolveMeshRef`. **The resolver caught a real bug in its own validation run**:
  `PlanetStage.Build` restored the snapshot before `SetupPlanetChallenge` registered
  `sandbox/beacon` — the exact "wrong asset load order → silently wrong drawable" this milestone
  closes, now a loud `GraphicsException`. Fix: `PlanetStage.Build` no longer loads; each recipe
  calls a new `PlanetStage.RestoreIfRequested` **after** registering its own assets.
- **W4** — double audit (`csharp-lowlevel` + `engine-architect`, both **4.3/5
  PASS-with-concerns**, no blocker). Applied: `AotSerializationSmoke` now exercises the v3
  key-table path under AOT; `Load` rejects a non-ascending / duplicate key table; strict UTF-8
  decode of key entries; canonical `(0,0,0)` for a `None` `MeshRef`; `ModelKeyIndex.Identify`
  treats a fully-`Invalid` pair as `None` (re-save after a resolver-less load); `ModelKeyIndex.Add`
  is atomic + throws named instead of silent indexer overwrite; 9 tests added (659 total).

**Deviation from spec** (intentional, stricter): `ReadMeshRef` checks `keyIdx` against the table
**before** the `keyIdx == 0 || resolve is null` short-circuit — a corrupt index is a typed throw
even on the no-resolver path.

**Deferred** (board §Deferred Work + `BACKLOG.md §4quater`): sim-side asset identity (a server
cannot emit visually reconstructible state — Contenu-3's structural decision); content-root-relative
keys (Contenu-2); a `RestoreIfRequested` guard (ties to the S30 `SceneContext` split).

**Gates**: `dotnet build` 0 warning · `dotnet test` 659 passed · HeadlessSim JIT == AOT re-pinned ·
`AotComponentProbe` PASS JIT + AOT · HDR `12638edd…` / UI `03421357…` unchanged · `planet-challenge`
/ `planet-drop` round-trip renders, 0 leak, 0 validation. **Human visual verdict: DUE** (shared
with the Agapanthe.App S30 verdict).
