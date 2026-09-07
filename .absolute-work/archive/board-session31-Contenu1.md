# Absolute Work Board — Contenu-1 : stable asset identity

**Status**: `completed` 2026-09-07 — W1–W4 + tail + double audit (both PASS-with-concerns 4.3/5,
no blocker) + audit findings applied. `dotnet test` **659 passed**, 0 warning, HeadlessSim
JIT==AOT re-pinned, `AotComponentProbe` PASS JIT+AOT, captures unchanged, round-trip renders.
**Human visual verdict still due** before marking the milestone `done` (shared with Agapanthe.App S30).
**Spec**: `docs/plans/2026-09-07-content-asset-identity-design.md` (APPROVED 4.60/5, reviewer 2 rounds)
**Session**: 31
**Created**: 2026-09-07

First of 3 sub-milestones of the **Contenu** domain (decomposed like MP-0): **1. asset identity**
(this) · 2. offline cook + manifest · 3. declarative prefabs/scenes. Human greenlight between
sub-milestones. This one: `AssetKey`, `ModelKeyIndex`, snapshot **v3** (`MeshRef` = `(key, local
indices)` + a load-time resolver — VS-1's deferred "Option 2"). Ends "different asset load order
→ silently wrong drawable" (VS-1 decision 1's documented limit, worsened by S30).

## Project Conventions

- .NET 10, `TreatWarningsAsErrors` (0 warning), xUnit, `dotnet build Agapanthe.slnx` / `dotnet test`.
- NativeAOT: `samples/HeadlessSim` + `tools/AotComponentProbe` publish; AOT publish needs `PATH`
  prefixed with `/c/Program Files (x86)/Microsoft Visual Studio/Installer` (vswhere).
- No `Vk*` outside `Agapanthe.Graphics`; no Arch type outside `Agapanthe.World`;
  `Agapanthe.Engine` closure = `{Core, World}`; `Agapanthe.App` refs everything except `Platform`
  (all pinned by `EngineIsHeadlessTests`). **`Agapanthe.World` does not reference `Agapanthe.Rendering`**
  — the `MeshRefIdentifier`/`MeshRefResolver` delegates name only `Core` types.
- `GraphicsException` = `Agapanthe.Graphics` (available to `Rendering`). `WorldSerializationException` = `World`.
- Blittable snapshot = zero reflection, deterministic (byte-identical = gate), little-endian.
- Conversation FR; code / commits / docs EN.

### Gates (acceptance bar — unchanged)

- `dotnet build` 0 warning · `dotnet test` green (+ ~18 new/changed).
- Capture ×3 (Debug, 1280×720), `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420
  AGAPANTHE_OVERLAY=0 AGAPANTHE_DROP_EVERY=12 AGAPANTHE_CAPTURE=<> AGAPANTHE_CAPTURE_UI=<>`:
  HDR `12638eddd7f3f67ab161b298ffbcd15e` / UI `034213575932dabcff41c2e0c72addfa` **unchanged**
  (pixel captures — no snapshot written).
- `HeadlessSim` default (`--ticks 600 --bodies 8`) + `--drive` (`--ticks 600`) snapshot MD5s:
  **new, re-pinned**, JIT == NativeAOT. (Current v2 values: `7e8dc68f5a25914c84677a7a53ad3a58`
  1868 B / `97e786f0455a53d856b9ba4affca1003`.)
- `AotComponentProbe` PASS · NativeAOT publish of `HeadlessSim` PASS · `EngineIsHeadlessTests` green.
- Double audit (`csharp-lowlevel` + `engine-architect`) · **human visual verdict**: save a
  `planet-challenge` / `planet-drop`, relaunch with `AGAPANTHE_LOAD` → the world restores and
  **renders** (not an invisible scene).

## Rollback Point

`a155f5d95e31b621f6baf5efc54694e0620d7a88` (tree clean; the board + the new spec file are the
only staged/untracked changes at DECOMPOSE time).

## Progress

### W1 DONE (2026-09-07) — AW-001..005

- **AW-001** `src/Agapanthe.Core/AssetKey.cs` (`readonly record struct`, `string? Value` null-iff-None,
  ctor normalises `\`→`/` / trim / collapse `//`, rejects `.`/`..`/leading-`/`/empty/whitespace,
  `None => default`) + `MeshRefKey.cs` (`(AssetKey, int, int)`, `None => default`). `AssetKeyTests` (18).
- **AW-002** `src/Agapanthe.Rendering/ModelKeyIndex.cs` (`internal`, GPU-free — `Dictionary<AssetKey, Entry>`
  + reverse `Dictionary<MeshHandle,…>` / `<MaterialHandle,…>`; `Add` rejects None/dup, `Remove` drops
  forward+reverse, `Clear`, `Contains`, `Identify`→`MeshRefKey`, `Resolve`→handles). `ModelKeyIndexTests` (7,
  incl. the round-trip + the "remove/re-add resolves fresh handles" = the bug being fixed).
- **AW-003** `ResourceRegistry`: `Load(…, AssetKey key, …)` — key validated at the TOP (None → `ArgumentException`,
  dup → `GraphicsException`) before any GPU work; `_keyIndex.Add` is the last statement; `LoadedModel.Key`;
  `Unload`/`Dispose` remove/clear `_keyIndex`. New `public IdentifyMeshRef` / `ResolveMeshRef`.
- **AW-004** 10 Sandbox call sites keyed: `models/<file>` (glTF ×2), `models/unload-probe` (unload test,
  `Unload` each cycle), `sandbox/ground`, `sandbox/planet-surface`, `sandbox/sun-surface`, `sandbox/probe`,
  `sandbox/beacon`. No behaviour change.
- **AW-005 gate**: `dotnet build` (slnx) 0 warning · `dotnet test` **642 passed** (+25) · `HeadlessSim` default
  `7e8dc68f5a25914c84677a7a53ad3a58` **unchanged** (still v2) · capture `planet-drop` HDR `12638edd…` /
  UI `03421357…` **unchanged** · `model` / `grid:8x8` / `planet-challenge` / `drive` launch clean, 0 leak,
  no key collision.

### W2 DONE (2026-09-07) — AW-006..009

- **AW-006** `WorldSerialization` v3: `SerializationVersion = 3`; `MeshRefIdentifier` / `MeshRefResolver`
  delegates (name only `Core` types — no `World`→`Rendering` edge); `KeyOrdinal` comparer
  (`string.CompareOrdinal`). `Save(Stream, MeshRefIdentifier?)` — pass 1 fills `MeshRefKey?[]` +
  `SortedSet<AssetKey>`, writes the key table (entry 0 = `None`/byteLen 0, 1.. sorted ordinal),
  pass 2 indexed loop → `WriteMeshRef` (3× u32). `Load(Stream, policy, MeshRefResolver?)` — reads
  the table (validates `keyCount ≥ 1`, entry-0 byteLen 0, each key through the `AssetKey` ctor),
  `ReadMeshRef` (keyIdx 0 or `resolve == null` → `MeshHandle.Invalid`; OOR → `WorldSerializationException`;
  else `resolve(...)`). `WriteComponent`/`ReadAndAddComponent` **case 5 removed**. v1 **and** v2
  refused with dedicated messages. Existing 1-/2-arg overloads delegate with `null`.
- **AW-007** `WorldSerializationV2Tests.cs` → `WorldSerializationV3Tests.cs` (`Save_WritesVersion3`
  = `3u`, `Load_RefusesV2` names "v2", + 8 Contenu-1 tests: key in table, delegates rebuild handles
  + byte-identical re-save, deterministic ordinal table run-to-run, no-resolver → `Invalid` even
  with a populated table, no-identifier → `None` round-trip, resolver-throws propagates, keyIdx
  beyond table → typed throw). `WorldSerializationTests.Load_RejectsOutOfRangeMaskBit` re-derives
  the mask offset past the key table; `RoundTrip…CollectsToTheSameRenderCount` +
  `RenderStageNeutralityTests` now pass identify/resolve (a load that then renders needs live
  handles for the sort key). New `GameWorld.MeshRefForTest(ulong)` hook.
- **AW-008** HeadlessSim re-pinned, **JIT == NativeAOT**: default `80ced166fdf3076119a62970f63d683b`
  (1842 B, was `7e8dc68f…` 1868 — +6 key table, −8×4 MeshRef) · `--drive`
  `cf01492e8a9688b666e01d2ef9d63869` (210 B, was `97e786f0…` 208). `AotComponentProbe`
  `AotSerializationSmoke` still byte-identical (no resolver → `MeshRef` ⇄ `Invalid`); AOT publish PASS.
- **AW-009 gate**: `dotnet build` (slnx) 0 warning · `dotnet test` **650 passed** · `planet-drop`
  capture HDR `12638edd…` / UI `03421357…` **unchanged**, 0 leak (198 resources), 0 validation.

### W3 DONE (2026-09-07) — AW-010..012

- **AW-010** `AppHost.cs` `AGAPANTHE_SAVE` → `world.Save(saveStream, registry.IdentifyMeshRef)`.
- **AW-011** F5 quicksave (`PlanetChallengeSceneRecipe`) → `world.Save(fs, registry.IdentifyMeshRef)`.
  `AGAPANTHE_LOAD` → `world.Load(stream, AdoptFromHeader, ctx.Registry.ResolveMeshRef)`.
- **AW-010b (blocking discovery, in scope)** — the v3 resolver **correctly refused** a `planet-challenge`
  reload: `PlanetStage.Build` restored the snapshot *before* `SetupPlanetChallenge` registered
  `sandbox/beacon` / `sandbox/probe`, so `ResolveMeshRef` threw `GraphicsException("snapshot references
  asset 'sandbox/beacon' which is not loaded")` — **exactly the "wrong asset load order → silently wrong
  drawable" bug this milestone closes**, now loud. Fix: `PlanetStage.Build` no longer loads; it returns
  `LoadPath` and a new `PlanetStage.RestoreIfRequested(ctx, in info)` is called by each recipe **after**
  it has registered its own probe/beacon assets (`planet` right away, `planet-drop` after `SetupPlanetDrop`,
  `planet-challenge` after `SetupPlanetChallenge`). `PlanetInfo.LoadMode` is now a computed property.
- **AW-012 gate**: `dotnet build` 0 warning · `dotnet test` **650 passed** · `planet-challenge` save
  (v3, byte 4 = `03`) → reload: **3 entities restored**, 1.64 M / 2.76 M non-zero pixels (planet + beacon
  render), 0 leak (205 resources), 0 validation · `planet-drop` save → reload renders, 0 leak · `model` /
  `grid:20x20` / `drive` launch clean, 0 leak · capture ×3 HDR `12638edd…` / UI `03421357…` **unchanged**.

### W4 IN PROGRESS (2026-09-07)

- **AW-013 Self Code Review** DONE — full diff re-read against spec + Decision Log:
  - one `AssetKey` key type everywhere (`ResourceRegistry` dict + snapshot `keyToIndex` both
    `Dictionary<AssetKey, …>`); `None => default`, `IsNone => IsNullOrEmpty` — no `""`/`null` split.
  - `WriteComponent`/`ReadAndAddComponent` case 5 truly gone (comment marker, falls to `default:` throw).
  - `_keyIndex` cleaned in `Unload` (`Remove(loaded.Key)`) + `Dispose` (`Clear()`); key validated
    before any GPU side effect (`_keyIndex.Add` is the last statement of `Load`, pure insert).
  - ordinal sort (`string.CompareOrdinal`) — `.Value` never null in the comparer (None keys never
    enter `distinctKeys`).
  - no `Agapanthe.World → Rendering` edge — delegates name only `Core` types.
  - **applied during review**: the v3 key table is now read + validated in `Load` **before** any
    state mutation (`_universeId` / `_nextGlobalId`), preserving the MP-0b "a rejected Load leaves
    the world intact" guarantee (was: read after the universe/allocator assignments).
- **AW-014 Requirements Validation** DONE — spec acceptance bar + Decision Log + Error Handling
  table walked line by line, all satisfied. One **intentional deviation**: `ReadMeshRef` does the
  `keyIdx >= keyTable.Length` range check **before** the `keyIdx == 0 || resolve is null`
  short-circuit (spec pseudocode had it after) — stricter: a corrupt key index is a typed throw
  even on the no-resolver path, never a silent `Invalid`.
- **AW-015 double audit** — `csharp-lowlevel` **4.3/5 PASS-with-concerns** (no blocker) ·
  `engine-architect` **4.3/5 PASS-with-concerns** (no blocker). The architect's headline: the
  mechanism *found its own bug* in W3 (AW-010b) — the strongest possible signal for an identity
  milestone.

  **Findings applied (W4):**
  - `AotSerializationSmoke` now passes an identifier + resolver → the v3 key-table path (UTF-8
    encode/decode, `SortedSet<AssetKey>` comparer, `AssetKey` ctor, resolve invoke) is instantiated
    under NativeAOT (was never reached by the null-delegate smoke). No pin moved.
  - `Load` rejects a non-strictly-ascending / duplicate key table on read (symmetry with the
    "Duplicate GlobalId" guard) — a forged table no longer loads-then-re-saves to different bytes.
  - Strict UTF-8 decode of key-table entries (`UTF8Encoding(throwOnInvalidBytes: true)`) — invalid
    bytes throw `WorldSerializationException` instead of substituting U+FFFD (matches the spec's
    Error Handling row).
  - `WriteMeshRef` writes `(0, 0, 0)` for a `None` key — canonical (`ReadMeshRef` ignores the local
    indices for key 0).
  - `ModelKeyIndex.Identify(Invalid, Invalid) → MeshRefKey.None` — re-saving a world that was loaded
    without a resolver no longer throws (`F5` on a headless-origin snapshot).
  - `ModelKeyIndex.Add` checks every handle for a collision **before** mutating (atomic) and throws
    a named `GraphicsException` instead of a silent indexer overwrite (the MP-0b W4 bug class).
  - blittable-format header comment names `MeshRef`; `ResourceRegistry.Load`'s duplicate-key message
    is now distinct from `ModelKeyIndex`'s; `WorldSerializationTests` `GameWorld` back under `using`;
    `AssetKey` doc-comment states case is significant.
  - **+9 tests** (659 total): key-table rejection branches (`keyCount 0`, entry-0 byteLen, non-ascending,
    invalid UTF-8), corrupt-table-leaves-world-intact, `Identify(Invalid)`, handle-collision, case
    sensitivity, re-save-after-resolverless-load.

  **Findings deferred (see §Deferred Work + `docs/BACKLOG.md`):**
  - **sim-side asset identity** (architect finding 1 — the structural one): `MeshRef` is render-local,
    a `Save` with no identifier writes `None`, so an authoritative server can't emit visually
    reconstructible state. Direction: a sim-side `AssetRef` component, `MeshRef` its client
    projection. **The decision Contenu-3 must take** — added to `BACKLOG.md §4quater`.
  - Sandbox keys from `Path.GetFileName` (dir discarded) → content-root-relative keys, a
    `AssetKey.FromContentPath` policy — Contenu-2 (when a content root exists).
  - `RestoreIfRequested` is an unguarded mandatory step ("forgot the call" → silent empty world);
    the load path is recipe-owned while save is host-owned (`AGAPANTHE_SAVE` re-read in
    `PlanetChallengeSceneRecipe` — a regression of the S30 W5 cleanup). Fix ties to the S30
    `SceneContext` sim/presentation split — Contenu-3 at the latest.
  - `checked((ushort))` key-length bound → `AssetKey` ctor invariant; `MeshRefKey` negative-index
    ctor guard — both minor.

- **CONVERGE** — `CLAUDE.md` (§Contenu paragraph + the persistent-debt `Generation` line rewritten:
  the "different asset load order breaks silently" debt is **paid for mesh + material**),
  `docs/AVANCEMENT.md` (§Reprise → Contenu-2), `docs/BACKLOG.md` (Contenu 3-way decomposition, VS-1
  debt `:337` delivered, new deferred rows), spec §Execution outcome, board archived.

---

## Tasks

### Wave W1 — `AssetKey` + `ModelKeyIndex` + call sites (format untouched)

#### AW-001 — `AssetKey` + `MeshRefKey` (Core)
- **Type**: code · **Size**: S · **Deps**: none
- **Files**: `src/Agapanthe.Core/AssetKey.cs` (new), `src/Agapanthe.Core/MeshRefKey.cs` (new or fold into AssetKey.cs)
- `readonly record struct AssetKey` — `string? Value` (null **iff** `None`/`default`); ctor
  `ArgumentException.ThrowIfNullOrWhiteSpace` then `Normalise` (`\`→`/`, trim, collapse `//`,
  reject `.`/`..` segments + leading `/` → `FormatException`; never yields `""`).
  `AssetKey.None => default`; `IsNone => string.IsNullOrEmpty(Value)`; `ToString()` → `"<none>"`.
- `readonly record struct MeshRefKey(AssetKey Key, int LocalMesh, int LocalMat)` —
  `None => default`, `IsNone => Key.IsNone`.
- **Acceptance**: builds 0 warning; `Agapanthe.Core.csproj` unchanged.

#### AW-002 — `ModelKeyIndex` (Rendering, GPU-free)
- **Type**: code · **Size**: M · **Deps**: AW-001
- **Files**: `src/Agapanthe.Rendering/ModelKeyIndex.cs` (new)
- Holds `Dictionary<AssetKey, Entry(MeshHandle[] Meshes, MaterialHandle[] Materials)>` +
  reverse `Dictionary<MeshHandle, (AssetKey Key, int Local)>` + `Dictionary<MaterialHandle, …>`.
  `Add(AssetKey key, MeshHandle[], MaterialHandle[])` (defensive: `key.IsNone` → `ArgumentException`,
  duplicate → `GraphicsException`), `Remove(AssetKey)` (drops forward + reverse entries),
  `MeshRefKey Identify(MeshHandle, MaterialHandle)` (unknown / cross-model → `GraphicsException`),
  `(MeshHandle, MaterialHandle) Resolve(AssetKey, int localMesh, int localMat)` (key absent /
  index OOR → `GraphicsException`).
- **Acceptance**: builds; **no `GraphicsDevice` reference** (pure over handle arrays).

#### AW-003 — `ResourceRegistry` gains the key
- **Type**: code · **Size**: M · **Deps**: AW-002
- **Files**: `src/Agapanthe.Rendering/ResourceRegistry.cs`
- `Load(GraphicsDevice, ModelAsset, DescriptorSetLayout, AssetKey key, Double3 worldOrigin = default)`
  — **validate `key` at the TOP** (before `new DescriptorAllocator`): `key.IsNone` →
  `ArgumentException`; `_keyIndex` already has `key` → `GraphicsException`. `LoadedModel` gains
  `AssetKey Key`. `_keyIndex.Add(key, meshHandles, materialHandles)` is the **last** statement
  (pure insert, cannot throw — key already proven). `Unload` / `Dispose` each `_keyIndex.Remove(loaded.Key)`
  / clear.
- `public MeshRefKey IdentifyMeshRef(MeshHandle, MaterialHandle)` → `_keyIndex.Identify(...)`.
- `public (MeshHandle Mesh, MaterialHandle Material) ResolveMeshRef(AssetKey, int, int)` →
  `_keyIndex.Resolve(...)` (key absent → `GraphicsException` "snapshot references asset '{key}'
  which is not loaded — load it before restoring").
- The existing minting-rollback `catch` (`:126-142`) needs no `_keyIndex` change (`Add` is after it).
- **Acceptance**: builds; existing renderer/registry tests green.

#### AW-004 — Sandbox call sites name their assets
- **Type**: code · **Size**: S · **Deps**: AW-003 · **owns** `samples/Sandbox/*` this wave
- **Files**: `samples/Sandbox/Content/ModelContent.cs` (`BuildGroundModel` caller side — the
  recipes), `Content/PlanetContent.cs` (`SetupPlanetScene` planet/sun, `SetupPlanetDrop` probe,
  `SetupPlanetChallenge` probe/beacon), `Scenes/ModelSceneRecipe.cs` (glTF +
  `AGAPANTHE_UNLOAD_TEST` reuse `"models/<name>#unload"` + `Unload` each cycle),
  `Scenes/DriveSceneRecipe.cs` (glTF + ground). Keys: `"models/<file>"`, `"sandbox/ground"`,
  `"sandbox/planet-surface"`, `"sandbox/sun-surface"`, `"sandbox/probe"`, `"sandbox/beacon"`.
- **Acceptance**: builds; every scene launches clean; captures unchanged (behaviour identical).

#### AW-005 — W1 gate
- **Type**: docs · **Size**: S · **Deps**: AW-004
- `dotnet build` (slnx) 0 warning · `dotnet test` green (tests 1–8) · capture `planet-drop` HDR
  `12638edd…` / UI `03421357…` **unchanged** · `HeadlessSim` default + `--drive` **unchanged**
  (still v2 — `MeshRef` bytes not touched). Record in `## Progress`.

### Wave W2 — snapshot v3

#### AW-006 — `WorldSerialization` v3
- **Type**: code · **Size**: M · **Deps**: AW-001
- **Files**: `src/Agapanthe.World/WorldSerialization.cs`
- `private const uint SerializationVersion = 3`. Key table after the 40-byte header (`keyCount`
  u32 ≥ 1; entry 0 = `byteLen 0`; entries 1.. = distinct non-`None` keys, sorted **ordinal**
  via `Comparer<AssetKey>.Create((a,b) => string.CompareOrdinal(a.Value, b.Value))`).
- `public delegate MeshRefKey MeshRefIdentifier(MeshHandle mesh, MaterialHandle material);`
  `public delegate (MeshHandle Mesh, MaterialHandle Material) MeshRefResolver(AssetKey key, int localMesh, int localMat);`
- `Save(Stream, MeshRefIdentifier? identify = null)` — pass 1 fills `MeshRefKey?[] meshKeys`
  (parallel to the sorted entity list) + a `SortedSet<AssetKey>` (ordinal); writes the table +
  builds `Dictionary<AssetKey, uint> keyIndex`. Pass 2 = **indexed** `for` loop; `index ==
  MeshRefIndex` → `WriteMeshRef(s, meshKeys[i] ?? MeshRefKey.None, keyIndex)` (3× `WriteU32`).
- `Load(Stream, SnapshotAllocatorPolicy, MeshRefResolver? resolve = null)` — reads the table;
  `index == MeshRefIndex` → `ReadMeshRef(s, keyTable, resolve)` (keyIdx 0 or `resolve == null` →
  `MeshRef { Mesh = MeshHandle.Invalid, Material = MaterialHandle.Invalid }`; keyIdx OOR → throw;
  else `resolve(...)` — which **must throw** for what it can't resolve).
- `WriteComponent`/`ReadAndAddComponent` **case 5 deleted** (falls to `default:` throw).
- Existing `Save(Stream)` / `Load(Stream)` / `Load(Stream, policy)` overloads kept (delegate `null`).
- v2/v1 → `WorldSerializationException` ("format v2, which predates stable asset identity
  (Contenu-1) — resave with this build first"), mirroring the v1 message.
- Key-table validation: `keyCount == 0`, `keyIdx ≥ keyCount`, truncated UTF-8, a key the ctor
  rejects → `WorldSerializationException`.
- **Acceptance**: builds 0 warning; a bare `Save` → header byte 4..8 == `3`.

#### AW-007 — v3 serialization tests
- **Type**: test · **Size**: M · **Deps**: AW-006
- **Files**: `tests/Agapanthe.Tests/WorldSerializationV2Tests.cs` → **rename** to
  `WorldSerializationV3Tests.cs`; `tests/Agapanthe.Tests/WorldSerializationTests.cs` (modify)
- Rename `Save_WritesVersion2` → `Save_WritesVersion3` (`3u`); keep `Load_RefusesV1…` + add
  `Load_RefusesV2` (craft a v3 save, set bytes 4..8 = 2, assert message names "v2"). Fold in
  tests 9–14: header v3, refuses v2, no-resolver → `MeshHandle.Invalid`, delegates round-trip
  through keys (`Save(Load(x)) == x` byte-identical), byte-identical run-to-run, resolver-throws
  propagates and leaves no usable world.
- `WorldSerializationTests.Load_RejectsOutOfRangeMaskBit` (`:156-164`) — re-derive the first
  entity's offset past the key table (`40 + 4 + Σ entry sizes`) instead of hard-coded `bytes[51]`.
- **Acceptance**: all green.

#### AW-008 — HeadlessSim re-pin + AOT smoke
- **Type**: infra · **Size**: M · **Deps**: AW-006
- **Files**: `tests/Agapanthe.Tests/HeadlessSimSnapshotFormatTests.cs`
- Run `HeadlessSim --ticks 600 --bodies 8 --save` + `--drive --ticks 600 --save`; record the new
  MD5s; NativeAOT publish + run → identical; update `ExpectedMd5` (`:68`) + `ExpectedDriveMd5`
  (`:147`). Verify `AotSerializationSmoke` (`WorldSerialization.cs:335`) still round-trips
  byte-identically (no resolver → `MeshRef` ⇄ `Invalid`); run `AotComponentProbe` publish.
- **Acceptance**: both MD5s pinned, JIT == AOT; `AotComponentProbe` PASS.

#### AW-009 — W2 gate
- **Type**: docs · **Size**: S · **Deps**: AW-007, AW-008
- `dotnet build` 0 warning · `dotnet test` green (tests 9–18) · pixel captures **unchanged** ·
  HeadlessSim MD5s **changed + pinned** JIT==AOT. Record in `## Progress`.

### Wave W3 — wire the delegates

#### AW-010 — `AGAPANTHE_SAVE` → identifier
- **Type**: code · **Size**: S · **Deps**: AW-006, AW-003
- **Files**: `src/Agapanthe.App/AppHost.cs` (the `AGAPANTHE_SAVE` block, `:139`)
- `world.Save(saveStream)` → `world.Save(saveStream, registry.IdentifyMeshRef)`.
- **Acceptance**: builds; a save of `planet-drop` writes a v3 file with a populated key table.

#### AW-011 — `AGAPANTHE_LOAD` + F5 → resolver
- **Type**: code · **Size**: S · **Deps**: AW-010
- **Files**: `samples/Sandbox/Content/PlanetStage.cs` (`AGAPANTHE_LOAD` block),
  `samples/Sandbox/Scenes/PlanetChallengeSceneRecipe.cs` (F5 quicksave → identifier)
- `world.Load(loadStream, SnapshotAllocatorPolicy.AdoptFromHeader, ctx.Registry.ResolveMeshRef)`;
  F5 `world.Save(fs, ctx.Registry.IdentifyMeshRef)`.
- **Acceptance**: builds.

#### AW-012 — W3 gate
- **Type**: infra · **Size**: M · **Deps**: AW-011
- `AGAPANTHE_SCENE=planet-challenge AGAPANTHE_SAVE=ch.save` → `AGAPANTHE_LOAD=ch.save` relaunch:
  the beacon + probes restore **in place and render** (headless capture of the loaded scene:
  0 leak, 0 validation, a non-empty image). Same for `planet-drop` via `AGAPANTHE_SAVE` +
  `AGAPANTHE_LOAD`. `model` / `grid:20x20` / `drive` launch clean. Captures ×3 unchanged.
- **Acceptance**: round-trip renders; all in `## Progress`.

### Wave W4 — mandatory tail

#### AW-013 — Self Code Review
- **Type**: docs · **Deps**: AW-012
- Re-read the full diff against the spec + Decision Log. Check: one `AssetKey` key type
  everywhere; `WriteComponent` case 5 truly deleted; `_keyIndex` cleaned in `Unload`/`Dispose`;
  key validation before any GPU side effect; ordinal sort; no `Agapanthe.World`→`Rendering` edge.

#### AW-014 — Requirements Validation
- **Type**: docs · **Deps**: AW-013
- Walk the spec's acceptance bar + every Decision Log row + the Error Handling table line by line.

#### AW-015 — Full Project Verification + double audit + human verdict + CONVERGE
- **Type**: docs · **Deps**: AW-014
- `dotnet build` 0 warning · `dotnet test` green · `EngineIsHeadlessTests` green · captures ×3 ·
  HeadlessSim ×2 JIT==AOT (new pins) · `AotComponentProbe` · 0 leak / 0 validation.
- Double audit: `csharp-lowlevel` + `engine-architect` subagents; apply findings.
- Human verdict: save/load a planet scene → restores + renders.
- CONVERGE: `CLAUDE.md` (§Contenu, module note), `docs/AVANCEMENT.md` (§Reprise → Contenu-2),
  `docs/BACKLOG.md` (§4quater — the Contenu 3-way decomposition; VS-1 debt `:337` marked
  delivered), spec §Execution outcome, archive board, suggest commit.

---

## Dependency graph

```
W1:  AW-001 ──┬─ AW-002 ── AW-003 ── AW-004 ── AW-005
              └──────────── AW-006 (World, may start with W2)

W2:  AW-006 ──┬─ AW-007 ──┐
              └─ AW-008 ──┼─ AW-009
                          ┘

W3:  AW-006,003 ── AW-010 ── AW-011 ── AW-012

W4:  AW-012 ── AW-013 ── AW-014 ── AW-015
```

## Wave / parallelism plan

| Wave | Sequential | Parallel-safe | Gate |
|---|---|---|---|
| W1 | AW-001 → AW-002 → AW-003 → AW-004 → AW-005 | — (Core → Rendering → Sandbox chain) | human greenlight |
| W2 | AW-006 → AW-009 | {AW-007, AW-008} parallel (disjoint test files) | human greenlight |
| W3 | AW-010 → AW-011 → AW-012 | — (delegates then callers) | human greenlight |
| W4 | AW-013 → AW-014 → AW-015 | — | human verdict + CONVERGE |

## Deferred Work

- **Contenu-2** — offline cook + content manifest + dependency graph (`tools/AssetCooker`,
  deterministic keyed blobs, glTF import → offline). Own spec + board + audit.
- **Contenu-3** — declarative `.agscene` / `.agprefab` (authoring format, NOT a save) +
  `SceneLoader`; the 5 Sandbox recipes → data. Own spec + board + audit.
- GUID identity + `.meta` sidecars (the "designed with an editor" version of `AssetKey`).
- Content-hash identity (a Contenu-2 cache concern, not a save identity).
- Texture / environment / font assets in the key scheme (this milestone: mesh + material only —
  the only handles `MeshRef` carries).
- A runtime `AssetKey → source path` loader (the recipe still calls `GltfLoader.Load` +
  `registry.Load` explicitly).
- Asset hot-reload.
- `MeshRef` still 12 B in the snapshot (`u32` ×3) — a `u16` key index + `u16` local indices (6 B)
  is a later compaction if it ever matters.
- **Sim-side asset identity** (architect audit finding 1) — `MeshRef` is render-local; a headless
  `Save` writes `None`, so an authoritative server cannot emit visually reconstructible state. A
  sim-side `AssetRef { assetId, localMesh, localMat }` with `MeshRef` as its resolved client
  projection. **Contenu-3's structural decision.** Also in `docs/BACKLOG.md §4quater`.
- **Content-root-relative keys** — Sandbox derives keys from `Path.GetFileName` (directory
  discarded → two homonymous models collide). An `AssetKey.FromContentPath(root, path)` policy.
  Contenu-2 (when a content root exists).
- **`RestoreIfRequested` guard** — an unenforced mandatory step (a recipe that forgets it →
  silent empty world in `AGAPANTHE_LOAD` mode); load is recipe-owned, save host-owned
  (`PlanetChallengeSceneRecipe` re-reads `AGAPANTHE_SAVE` — a regression of the S30 W5 env-var
  cleanup). Fix ties to the S30 `SceneContext` sim/presentation split — Contenu-3 at the latest.
- `checked((ushort))` key-byte-length bound → an `AssetKey` ctor invariant; `MeshRefKey`
  negative-index ctor guard — both minor, practically unreachable.
