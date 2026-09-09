# Absolute Work Board — Contenu-3a : sim-side asset identity + snapshot v4 + SceneContext split

**Status**: `executing` — Wave 4: AW-023 double audit done + findings applied, AW-024 converge
**Spec**: `docs/plans/2026-09-09-content-3a-sim-asset-identity-design.md` (APPROVED 4.55/5, scored review 3.2 → 4.55)
**Session**: 33
**Created**: 2026-09-09
**Predecessor board**: `.absolute-work/archive/board-session32-Contenu2.md` (Contenu-2, completed)

First of 3 sub-milestones of **Contenu-3** (declarative prefabs & scenes), decomposed like
MP-0 / Contenu — human gate + double audit between each. **3a** = foundation, no authoring
format: `AssetRef` component (asset identity carried inside the simulation), world snapshot
**v4** (serializes `AssetRef`, drops `MeshRef`, deletes `MeshRefIdentifier`, upgrades v3),
`SceneContext` → `SimSceneContext` + `PresentationSceneContext`, and a `RestoreIfRequested`
guard-rail on the sim context. No user-visible feature; the deliverable is a capability
foundation (a headless populator that stamps identity gets a reconstructible snapshot for
free — first such populator is 3b's `SceneLoader`).

## Rollback Point

`17458f975d5446942eeb7f3eb8d55335de4d5f76` (clean tree, `docs(contenu-3a): … spec`)

## Project Conventions

- .NET 10, `TreatWarningsAsErrors` (0 warning), xUnit. `dotnet build Agapanthe.slnx` / `dotnet test`.
- NativeAOT: `samples/HeadlessSim` + `tools/AotComponentProbe` publish; AOT publish needs
  `PATH` prefixed with the VS Installer dir (vswhere).
- Module rules (pinned by `EngineIsHeadlessTests`): no `Vk*` outside `Agapanthe.Graphics`;
  no Arch type outside `Agapanthe.World`; `Agapanthe.Engine` closure = `{Core, World}`;
  `Agapanthe.App` refs everything except `Platform`.
- Cooked-format / serialization pattern: binary, little-endian explicit, versioned header
  refused outright on mismatch, total length recomputed from counts before allocating.
- Acceptance gate: 0 warning · `dotnet test` green · 0 `ResourceTracker` leak · 0 validation
  message · pinned capture MD5s · Sandbox + HeadlessSim JIT == NativeAOT · double audit
  (`csharp-lowlevel` + `engine-architect`) · human visual verdict.
- Conversation FR; code / commits / docs EN. Board git-tracked.

### Pinned artefacts

- `model` HDR capture MD5 `9030f6a64e9587b05d5abb99b487b1b9` — **unchanged** (W1 + W2 verified).
- `planet-drop` HDR — this env's baseline is **`bc8440ab746c769cc2fa7db80fd16d54`** (doc's
  `12638edd…` is stale cross-env drift, like `model`'s was in Contenu-2). `git stash` A/B proves
  3a leaves it **byte-identical**. UI baseline TBD in AW-018. Capture cmd:
  `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420 AGAPANTHE_OVERLAY=0`.
- `HeadlessSim` default **`b3fa79d8b960a40013f6b2a5142d85fa`** (1842 B) / `--drive`
  **`f8120d258e407d72f653e72f02fa5cd9`** (210 B) — v4, re-pinned JIT == AOT (AW-013).
- `ComponentRegistry.All.Count` = **13**.

## Waves

### Wave 0 — spike + fixture  ·  GATE: AW-001 verdict decides D1 vs D1-alt

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-001 | code | S | **done** | — | Managed Arch `[Component]` spike — **D1 HOLDS** |
| AW-002 | infra | S | **done** | — | `Fixtures/world-v3.save` captured (685 B, v3, count 12) |

**AW-001 verdict**: `AssetRef` added as real component #13 (managed struct wrapping `MeshRefKey`),
`Root<AssetRef>()`, `ImportedEntitySpec.Identity`, both `Materialise*` sites. Build 0 warning.
JIT: 63/64 World tests pass (only `ComponentRegistry_All_MatchesTheFrozenOrder` red = AW-004).
**AOT probe PASS**: `IsDynamicCodeSupported=False`, 13 components, `AotRootingSmoke` iterated 13
(managed component survives Create / archetype-move / query / CommandBuffer / structural
add-remove under NativeAOT), `AotSerializationSmoke` byte-identical. **No fallback to D1-alt.**
Serialization: `AssetRef` excluded from the mask like `InstanceSlot` (v4 write path = W2), so
W1 changes zero snapshot bytes beyond `componentCount` 12→13.

### Wave 1 — `AssetRef` component + materialisation

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-003 | code | S | **done** | AW-001 | `AssetRef` component (#13) + `Root<AssetRef>()` |
| AW-004 | test | S | **done** | AW-003 | `ComponentRegistryTests` frozen order → 13 (`AssetRef` last) |
| AW-005 | code | S | **done** | — | `ImportedEntitySpec.Identity` + ctor param `MeshRefKey identity = default` |
| AW-006 | code | M | **done** | AW-003, AW-005 | `ResourceRegistry.Load` fills identity; `Materialise*` add `AssetRef`; `.Identity` threaded through 5 by-hand spec sites (`SpawnGrid`, `SpawnDropScene`, `DriveSceneRecipe`, `LandingChallengeSystem`, `ProbeDropSystem`) |
| AW-007 | test | S | **done** | AW-006 | `AssetRefComponentTests` (3 cases: stamp, None default, survives structural churn) + `AotRootingSmoke` iterates 13 |

### Wave 2 — snapshot v4

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-008 | code | M | **done** | AW-006 | v4 write: `SerializationVersion=4`, `V3ComponentCount=12`, special-case `AssetRef` idx 12, `MeshRef` no longer written, `case 12` in 3 switches + "13 concrete types" comment |
| AW-009 | code | M | **done** | AW-008 | `Load` accepts `version is 3 or 4`; per-version componentCount check; `ReadDrawableIdentity` → `(AssetRef, MeshRef)`; identity slot = idx 12 (v4) / idx 5 (v3); `InstanceSlot` re-add keyed on `hadIdentity` |
| AW-010 | code | M | **done** | AW-009 | deleted `MeshRefIdentifier` + `Save(Stream, MeshRefIdentifier?)` + `ResourceRegistry.IdentifyMeshRef` + `ModelKeyIndex.Identify` (dicts kept for `Add` guard). `Save(stream)` at `AppHost` + F5. |
| AW-011 | test | M | **done** | AW-010 | migrated `WorldSerializationTests`, `RenderStageNeutralityTests`, `ModelKeyIndexTests` (dropped `Identify` cases), retargeted `WorldSerializationV3Tests` (v3-legacy-upgrade + v4-native, `Save_WritesVersion4`). Deleted `WorldV3FixtureGenerator`. |
| AW-012 | test | M | **done** | AW-010, AW-002 | `WorldSerializationV3Tests`: `Save_UnkeyedDrawable_RoundTripsAsNone`, `Save_AfterLoadWithoutResolver_KeepsTheKey_ByteIdentical`, `Load_UpgradesV3Fixture_ToV4_PreservingAssetIdentity` (fixture), `Load_RejectsV3Fixture_WhenComponentCountIsNot12`, `RoundTrip_ResolverRebuildsMeshRef_AssetRefIntact_ByteIdentical`. `AssetRefComponentTests` covers headless emit/keep. |
| AW-013 | infra | M | **done** | AW-010 | `HeadlessSim` MD5 re-pinned **JIT == AOT**: default `b3fa79d8b960a40013f6b2a5142d85fa` (1842 B) / `--drive` `f8120d258e407d72f653e72f02fa5cd9` (210 B). `AotSerializationSmoke` rewritten: identity on specs + resolver on load. AOT probe PASS (count 13). |

### Wave 3 — `SceneContext` split

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-014 | code | M | **done** | AW-010 | `SimSceneContext.cs` (World, Simulation, Catalog, Args, Options, `AddSystem`, `RequestRestore`/`HasPendingRestore`/`ApplyPendingRestore`) + `PresentationSceneContext.cs`. `SceneContext.cs` deleted. |
| AW-015 | code | M | **done** | AW-014 | `ISceneRecipe.Build(SimSceneContext, PresentationSceneContext?)`; `HostOptions.LoadPath` ← `AGAPANTHE_LOAD`; `AppHost` builds both contexts + `ApplyPendingRestore(registry.ResolveMeshRef)` after `Build`. |
| AW-016 | code | M | **done** | AW-015 | 5 recipes + `PlanetStage` + `RecipeInput` rewritten (`ctx.X` → `sim.X`/`p.X`, `sim.AddSystem`, `?? throw` guard). `PlanetStage.RestoreIfRequested` → `RequestRestoreIfRequested` (declares, host applies). F5 `Save(fs)`. |
| AW-017 | test | M | **done** | AW-016 | `AppHostContractTests`: `FakeRecipe.Build` new sig, `LoadPath` in the env test, `Recipe_BuildsHeadless_WithNullPresentation` (populates + `AddSystem` + tick via `Build(sim, null)`), `SimSceneContext_RequestRestore_Twice_Throws`. `EngineIsHeadlessTests` unchanged + green. |

### Wave 4 — verify + mandatory tail

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-018 | test | S | **done** | AW-013, AW-017 | captures verified: `model` `9030f6a6…` / `planet-drop` HDR `bc8440ab…` (env baseline) / UI `0698bd74…` — 3a is a rendering no-op (`git stash` A/B). Doc's `12638edd`/`03421357` = stale cross-env drift, not gates in code. `dotnet test` **694 pass**. |
| AW-019 | infra | S | **done** | AW-018 | **Sandbox JIT == NativeAOT** (`model` `9030f6a6…` / `planet-drop` `bc8440ab…` byte-identical, AOT publish). `HeadlessSim` JIT==AOT (W2). `AotComponentProbe` AOT PASS, count 13, `IsDynamicCodeSupported=False`. |
| AW-020 | test | — | **done** | code tasks | Self review: diff clean, no off-spec/off-convention. Added `ClientKeyedContent_RebuiltSpec_KeepsIdentityInTheKeyTable` (finding #2 regression guard). |
| AW-021 | test | — | **done** | AW-020 | Requirements validation: D1–D7 all satisfied; §6 acceptance bar met (managed component AOT-proven, v4 + v3 upgrade, split, restore guard, MD5 re-pin, probe count 13, new snapshot tests). |
| AW-022 | test | — | **done** | AW-021 | Full verification: `dotnet build` 0 warning · `dotnet test` **694 / 0 skip / 0 fail** · 0 leak · 0 validation · captures ✓ · Sandbox + HeadlessSim + probe JIT == AOT. |
| AW-023 | infra | — | **done** | AW-022 | Double audit: `csharp-lowlevel` **3.9/5 PASS-with-concerns** · `engine-architect` **4.0/5 PASS-with-concerns**. Both found the SAME **🔴 F1** (see below) + 9 🟠/🟡. Findings applied (10 of 12). |
| AW-024 | docs | — | **running** | AW-023 | Converge: `CLAUDE.md`, `AVANCEMENT.md`, `BACKLOG.md`, spec §outcome, archive board. |

### Double audit — findings & disposition

**🔴 F1 (both audits) — `LandingChallengeSystem` seeded against an empty world on resume.** D7 moved the
`AGAPANTHE_LOAD` restore from *inside* `Build` to *after* it; the system's constructor seeded `_shotsIssued`
from `QuerySurfaceContacts` → 0 on a resume (full fresh budget, un-latched Won/Lost). **FIXED**: lazy seed on
the first `Execute` (+ in `TryShoot` for a same-tick B press) — order-independent. + test
`ApplyPendingRestore_PopulatesTheWorld_RequestAlone_DoesNot` pins that the world is populated by
`ApplyPendingRestore`, not `RequestRestore`. Sandbox save→reload verified (0 leak, restore-from-path logged).

**Applied (🟠/🟡):**
- F2 (both) — `RequestRestore(Stream)` → `RequestRestore(string path)`; the file opens inside
  `ApplyPendingRestore` under its own `using`, the caller never holds a handle. (`+PendingRestorePath`.)
- F2 arch — **structural gate**: `EngineIsHeadlessTests.SimSceneContext_NamesNoGpuOrWindowType` reflects every
  member (public + internal) and rejects any Graphics/Rendering/Engine.Render/Silk.NET type.
- F3 arch — `[Conditional("DEBUG")]` `AppHost.WarnIfDrawablesMissingIdentity` after `Build` (via
  `GameWorld.DrawablesMissingIdentity()`): a by-hand spec copy that dropped `.Identity` now warns instead of
  silently serialising `None`.
- F4 (both) — `AotRootingSmoke` specs carry a real `AssetKey("aot/root")` **and despawn a keyed drawable**
  (forces Arch's swap-backfill over the managed `AssetRef[]`); `AssetRefComponentTests` despawns 2 mid-chunk
  keyed bodies. Probe re-run PASS (iterated 13).
- F4 arch — `HeadlessSim` `BuildScene`/`RunDrive` specs carry `AssetKey("headless/body")` /
  `"headless/drive-body"` — the shipped headless binary now proves genuine identity. **MD5 re-pinned JIT == AOT**:
  default `6a13dd54c1db32d35a15332bff0395e7` (1857 B) / `--drive` `f6053226f8c13b55589b29be103a8e66` (231 B).
- F6 LL — restore log carries the source path again.
- F7 LL — `V4ComponentCount` frozen const + `Debug.Assert` against the live registry.
- F8 LL — over-long key → `WorldSerializationException`, not raw `OverflowException`.
- F9 LL — stale `WriteMeshRef`/`ReadMeshRef` comments in the dispatch switches corrected.
- F9 arch — `ModelKeyIndex._byMesh`/`_byMaterial` (dead payload) → `HashSet<MeshHandle>`/`<MaterialHandle>`.
- F10 LL / F8 arch — `HostOptions.LoadPath` doc + `PlanetSceneRecipe.Matches` comment: a programmatic
  `LoadPath` does not drive default-scene selection (env only).

**Deferred (→ 3b/3c + board §Deferred + BACKLOG):**
- F3 (both) — **managed `AssetRef[]` GC mark cost** not measured (`dotnet-counters` not installed;
  installing a global tool unprompted is out of scope). Both audits agree the implementation is *sound*; this is
  a measurement gap. Fallback recorded: D1-alt (blittable numeric id + side table). Measure at 3b on
  `grid:100x100` Release+AOT.
- The rule "**nothing in `Build` may read world content**" (F1 generalised) — write it into `ISceneRecipe`.
- The 5+ field-by-field `ImportedEntitySpec` copies → delete when `SceneLoader` is the single materialisation
  path (3b).
- `Agapanthe.App` → `App` + `App.Client` assembly split before a real `RunDedicatedServer` (Vulkan is in
  App's closure today).
- `PlanetStage.LoadMode` — recipes still branch on "am I resuming?"; 3b should remove it.

## Deferred Work

- **Managed `AssetRef[]` GC mark cost** (audit F3) — unmeasured; fallback = D1-alt (blittable id + side
  table). Measure `grid:100x100` Release+AOT with `dotnet-counters` at 3b.
- **"`Build` must not read world content"** (audit F1 generalised) — the restore is now deferred; write the
  rule into `ISceneRecipe`'s contract so 3b's `SceneLoader` respects it.
- **5+ field-by-field `ImportedEntitySpec` copies** (audit F3) — `SpawnGrid`, `SpawnDropScene`,
  `DriveSceneRecipe`, `LandingChallengeSystem.TryShoot`, `ProbeDropSystem.DropOne` thread `.Identity` by hand;
  delete when `SceneLoader` becomes the single materialisation path (3b).
- **`Agapanthe.App` → `App` + `App.Client` split** — `ISceneRecipe`/`SimSceneContext` live in `App`, whose
  closure pulls Vulkan; a real `RunDedicatedServer` links `Silk.NET.Vulkan`. Tackle before that host (2nd
  slice / dedicated server).
- **`PlanetStage.LoadMode`** — recipes still branch on "am I resuming?"; 3b removes it.
- **Non-model asset identity** (textures / environment / fonts) still outside the snapshot — Contenu-2b / later.
- `SimSceneContext` `IDisposable` not needed now that `RequestRestore` takes a path (no held stream); revisit
  if a future field owns a resource.
- `planet-drop` HDR / UI capture hashes not test-pinned (needs a headless-GPU test harness) — `model` and
  `HeadlessSim` are test-pinned; these stay prose gates, re-baselined this session (see Pinned artefacts).

## Wave Log

- **2026-09-09** — board created, rollback point `17458f9`. Starting Wave 0.
- **2026-09-09** — Wave 0 done. AW-002: v3 fixture checked in. AW-001: managed component
  spike PASS under NativeAOT — `AssetRef` (component #13). D1 holds. **Gate before Wave 1.**
- **2026-09-09** — Wave 1 done (AW-003…007). `dotnet build` 0 warning; `dotnet test`
  **690 pass / 3 skip**. AOT probe PASS (count 13). `model` capture unchanged.
- **2026-09-09** — Wave 2 done (AW-008…013). Snapshot **v4**: `AssetRef` serialized (MeshRef's
  old 3-u32 shape), `MeshRef` dropped, `MeshRefIdentifier` + `ModelKeyIndex.Identify` deleted,
  v3→v4 in-place upgrade (fixture test green). `dotnet build` 0 warning; `dotnet test`
  **691 pass / 0 skip / 0 fail**. AOT probe PASS. `HeadlessSim` MD5 re-pinned JIT == AOT.
  **Captures: `git stash` A/B proves 3a is a rendering no-op** (`model` + `planet-drop` HDR
  byte-identical before/after). **Gate before Wave 3 (`SceneContext` split).**
- **2026-09-09** — Wave 3 done (AW-014…017). `SceneContext` → `SimSceneContext` (headless-safe)
  + `PresentationSceneContext` (nullable). 5 recipes + `PlanetStage` + `RecipeInput` rewritten.
  Restore guard-rail: recipe declares `RequestRestore`, host applies one `ApplyPendingRestore`
  after `Build`. `dotnet build` 0 warning; `dotnet test` **693 / 0 skip / 0 fail**. Sandbox
  captures verified (`model` `9030f6a6…`, `planet-drop` HDR `bc8440ab…` — both unchanged),
  0 leak / 0 validation. **planet-challenge save→load cycle works**: v4 snapshot (header
  `AGWD | 4 | 13`), `AppHost: [Contenu-3a] world restored — 3 entities` via the new
  `RequestRestore`→`ApplyPendingRestore` path. **Gate before Wave 4 (verify + tail + audit).**
