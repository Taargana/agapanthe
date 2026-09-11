# Absolute Work Board — Contenu-3b : `.agscene`/`.agprefab` + `SceneLoader`

**Status**: `completed` — 2026-09-11, double audit PASS-with-concerns ×2, findings applied, converged
**Spec**: `docs/plans/2026-09-09-content-3b-declarative-scenes-design.md` (APPROVED 4.4/5, scored review 3.3 → 4.4)
**Session**: 34
**Created**: 2026-09-09
**Predecessor board**: `.absolute-work/archive/board-session33-Contenu3a.md` (Contenu-3a, completed)

2ᵉ des 3 sous-jalons de Contenu-3. Format de scène déclaratif : authoring **TOML** cuit hors-ligne
en blobs binaires `.agscene` dans `content.agmanifest`, lu au runtime par un **`SceneLoader`
GPU-free** (nouveau projet `Agapanthe.Scene`) qui peuple un `GameWorld`. Bénéfice : **client et
serveur partagent le peuplement** — `HeadlessSim --scene <clé>` charge le même fichier. La famille
`model` (single/grid/drop) devient data ; prefabs = include cook-time inliné.
**3c (hors scope)** : générateurs procéduraux, registres de fabriques de jeu, migration `drive`/`planet*`.

## Rollback Point

`9a5ed08f435369cedfce91c1c488e26bd42858ae` (clean tree, `docs(contenu-3b): … spec`)

## Project Conventions

- .NET 10, `TreatWarningsAsErrors` (0 warning), xUnit. `dotnet build Agapanthe.slnx` / `dotnet test`.
- NativeAOT: `samples/HeadlessSim` + `tools/AotComponentProbe` publish; AOT publish needs `PATH`
  prefixed with the VS Installer dir (vswhere).
- Cooked-format pattern (`.agfont`/`.agmodel`): binary, LE-explicit, versioned header refused on
  mismatch, total length recomputed from counts before allocating; **reader `public`, writer
  `internal`** + `[assembly: InternalsVisibleTo]`.
- Cook-tool pattern: `tools/*` Exe, **not** AOT, **never** a shipped `ProjectReference`, invoked via
  `dotnet exec` from an MSBuild target; `AssetsPipelineIsolationTests` scans every `.csproj`.
- Module rules (pinned by `EngineIsHeadlessTests`): no `Vk*` outside `Agapanthe.Graphics`; no Arch
  type outside `Agapanthe.World`; `Agapanthe.Engine` closure = `{Core, World}`; `Agapanthe.App` refs
  everything except `Platform`. **New: `Agapanthe.Scene` closure = `{Core, World, Assets}`.**
- Conversation FR; code / commits / docs EN. Board git-tracked.

### Pinned artefacts

- `model` HDR capture MD5 **`9a010fc311dd51b74f755d306d4a819f`** (re-pinned W5 — cooked `scenes/model`:
  DamagedHelmet, studio HDR, declarative directional light, no ground/sky). Human visual verdict **PASS** 2026-09-09.
- `grid` HDR MD5 **`c314e6a1df18eeb665bcc8ca1d7b78af`** (new W5 — cooked `scenes/grid`, 10×10 = 100 helmets,
  frame-bounds camera → frustum-culls the far spread). PASS. (`AGAPANTHE_CULL_STATS` visible-count assertion
  dropped — the bench spinner was `ModelSceneRecipe`'s, now deleted; deferred to 3c.)
- `drop` HDR MD5 **`2fbf87fa403f8141ebfc3b7dcfc5636d`** (new W5 — cooked `scenes/drop`, 20-body cube cluster,
  physics always on). PASS.
- `metalrough` HDR MD5 **`c4cf460574c3036b799564d22c01847b`** (new W5 — cooked `scenes/metalrough`,
  MetalRoughSpheres 5 meshes). PASS.
- `.agmodel` cooked-blob hash re-pinned `a0287c4fced7dde20ad83ed0be1702130590c7f8144b4e612cc14dc3f1d74759` (W1, v2 + bounds).
- `planet-drop` HDR (this env's baseline) **`bc8440ab746c769cc2fa7db80fd16d54`** — **unchanged** under 3b
  (verified byte-identical, `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420`). Doc's `12638edd…` = stale cross-env drift.
- `HeadlessSim` default `6a13dd54c1db32d35a15332bff0395e7` (1857 B) / `--drive`
  `f6053226f8c13b55589b29be103a8e66` (231 B) — default **unchanged** (`BuildScene` hand-rolled).
- `ComponentRegistry.All.Count` = 13 (unchanged — 3b adds no component).
- 740 tests at close (727 at Wave 5 + 1 Wave 6 + 12 from audit-finding tests).

## Waves

### Wave 1 — `.agmodel` v2 + per-mesh bounds

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-001 | code | S | **done** | — | `Agapanthe.Assets.Model.MeshBounds.Compute` (body = `SceneBuilder.ComputeMeshLocalSphere`) + `MeshAsset.BoundsCenter/BoundsRadius` |
| AW-002 | code | M | **done** | AW-001 | `AgModelFormat` **v2** — `boundsCenter f32×3 | boundsRadius f32` after `worldTransform`; v1 → `AgModelException` "re-run the asset cook"; `GltfLoader` fills via `MeshBounds.Compute(positions)` |
| AW-003 | code | S | **done** | AW-001 | `SceneBuilder.BuildEntries` reads `mesh.BoundsCenter/Radius` when `BoundsRadius > 0`, else `ComputeMeshLocalSphere` (= forwarder to `MeshBounds`); `SceneBoundsTests` unchanged (tests the forwarder — the permanent algo test) |
| AW-004 | test | M | **done** | AW-002, AW-003 | `AgModelFormatTests`: `SampleModel` mesh carries bounds, `RoundTrip` asserts them; `Read_RejectsV1WithARecookMessage` |
| AW-005 | infra | S | **done** | AW-004 | `CookerVersion` → `contenu3b-1`; `.agmodel` blob hash re-pinned `a0287c4fced7dde20ad83ed0be1702130590c7f8144b4e612cc14dc3f1d74759`. **`model` `9030f6a6…` + `planet-drop` `bc8440ab…` byte-identical** (the algo is the same code — cooked bounds == old computation, procedural fallback == old). |

### Wave 2 — `.agscene` binary format + catalog

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-006 | code | M | **done** | — | `src/Agapanthe.Assets/Scene/{SceneDefinition,AgSceneException,AgSceneFormat}.cs` — DTO + sub-records, `AGSC` container + DeflateStream + bounds-checked `Reader` ref struct + local key table (ordinal-ascending); `AssetKind.Scene = 3` |
| AW-007 | code | S | **done** | AW-006 | `AssetCatalog.LoadScene(key) → SceneDefinition` (wrong-kind `AssetException`, absent, IO → `AgSceneException`) |
| AW-008 | test | M | **done** | AW-006 | `AgSceneFormatTests` — byte-identical round-trip (full / minimal / fixed-camera), `_Rejects` (magic/version/truncation/payload-ceiling). +7 tests. |

### Wave 3 — cook side (TOML → blob)

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-009 | config | S | **done** | — | `PackageReference Tomlyn 0.19.0` on `Agapanthe.Assets.Pipeline`; `AssetsPipelineIsolationTests.OnlyTheCookSideDeclaresATomlynPackageReference` + `Tomlyn` absent from `Agapanthe.Assets` referenced assemblies |
| AW-010 | code | M | **done** | AW-009 | `SceneTomlReader.ReadScene`/`ReadPrefab` → `AuthoredScene`/`AuthoredPrefab` (`SceneAuthoring.cs` model); bad TOML / missing field / bad-array → `AssetException` naming the path; both-model-and-prefab + no-items rejected |
| AW-011 | code | M | **done** | AW-010, AW-006 | `SceneCompiler.Compile` → flat `SceneDefinition`: `EmitGrid`/`EmitCluster` `Hash(i)` copied from `ModelContent.SpawnGrid`/`SpawnDropScene` (originals kept `internal static`), prefab inline, lights/camera/env/physics translate |
| AW-012 | code | S | **done** | AW-011, AW-006 | `AgSceneWriter.Write`/`WriteFile` (tmp-then-move) + `CookRunner.CookScenes` globes `content/scenes/*.toml`, `ManifestEntry(key, AssetKind.Scene, …)`, incrementality (`anyModelCooked` + `CookerVersion` + toml hash), `PruneOrphanBlobs` extended to `*.agscene` |
| AW-013 | infra | S | **done** | — | `build/Agapanthe.Cook.targets` — `CookAssets`/`IncludeCookedAssets` extracted from `Sandbox.csproj`; Sandbox imports it |
| AW-014 | config | S | **done** | AW-010 | `content/scenes/{model,grid,drop,metalrough,headless-default}.toml` + `content/prefabs/helmet.toml` — pinned params (spec §3.9) |
| AW-015 | test | M | **done** | AW-011, AW-014 | `SceneTomlReaderTests` (5), `SceneCompilerTests` (6: grid unrolls, cluster deterministic, prefab inlines, unknown-model throws, readable blob) |

### Wave 4 — `Agapanthe.Scene` runtime

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-016 | infra | S | **done** | AW-006 | new `src/Agapanthe.Scene/Agapanthe.Scene.csproj` `{Core, World, Assets}`, `IsAotCompatible`; `Agapanthe.slnx` + Tests ref; `EngineIsHeadlessTests` +`[InlineData]` (Scene allowlist). `Scene` in HeadlessSim/App allowlists deferred to W5/W6 (added when they actually reference it) |
| AW-017 | code | S | **done** | — | `GameWorld.ResolveMeshRefs(MeshRefResolver)` — walk `WithAll<AssetRef, MeshRef>`, fill Invalid MeshRef from non-None AssetRef, `_structuralDirty = true` when any resolved; resolver throw propagates |
| AW-018 | code | M | **done** | AW-016, AW-017, AW-007 | `SceneMaterializer.Materialize` (catalog + `Func<AssetKey,ModelAsset>` overloads; GPU-free spawn, `MeshHandle.Invalid` + `AssetRef`, mesh `WorldTransform`∘entity placement — identity fast-path = byte-identical to render path) + `MaterializeResult` + `SceneLoader.LoadHeadless` |
| AW-019 | test | M | **done** | AW-018 | `SceneMaterializerTests` (5), `GameWorldResolveMeshRefsTests` (4), `SceneLoaderHeadlessTests` (1) |

### Wave 5 — client

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-020 | code | M | **done** | AW-018 | `Agapanthe.App/Scene/`: `SceneCameraApplier` (`FrameCamera`/`NarrowBounds` lifted) + `SceneLightRig.Apply` (renamed — `SceneLights` name taken by Rendering) + `SceneInput.EnableFreeFly` (`WireFreeFly` lifted) + `ClientScenePresenter.Apply` (per-key `registry.Load` upload + `ResolveMeshRefs` + lights/env/camera). `Agapanthe.Scene` in App csproj + `EngineIsHeadlessTests` App allowlist |
| AW-021 | code | M | **done** | AW-020, AW-014 | `SceneRecipe(string sceneName) : ISceneRecipe` (one per scene); `SandboxGame.Scenes` = `model`/`grid`/`drop`/`metalrough` + `drive`/`planet*`; **`ModelSceneRecipe` deleted**. Deviation: `ModelContent.ResolveModelKey` + CLI model-arg KEPT — `DriveSceneRecipe` still uses them (drive migrates in 3c); `Sandbox.csproj` cook import was already done in W3 (AW-013) |
| AW-022 | test | M | **done** | AW-021 | `Materialize_IdentityPlacement_ComposesTransform_LikeTheRenderPath` (CPU fidelity: mesh `WorldTransform` split + worldOrigin == render path, byte-exact). Full `CookedSceneModel_RendersLikeDirectLoad` needs GPU → covered by the AW-023 capture gate. `AppHostContractTests` family-token tests kept (they exercise `SelectRecipe`'s generic `Matches`, still used by planet recipes) |
| AW-023 | infra | S | **done** | AW-022 | captures re-pinned + human visual verdict **PASS** (2026-09-09): `model` `9a010fc3…`, `grid` `c314e6a1…`, `drop` `2fbf87fa…`, `metalrough` `c4cf4605…`. All 4 scenes: 0 leak, deterministic. `CULL_STATS` visible-count assertion dropped (bench spinner was `ModelSceneRecipe`'s) → 3c |

### Wave 6 — HeadlessSim convergence

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-024 | code | S | **done** | AW-018, AW-013 | `HeadlessSim --scene <key>` (`RunScene`): `AssetCatalog.Open(<baseDir>/content)` → `LoadScene` → `SceneLoader.LoadHeadless` → attach `PhysicsSystem`. Conflicts with `--drive`/`--load`/`--bodies`. `.csproj` refs `Agapanthe.Scene` + imports `build/Agapanthe.Cook.targets`; `EngineIsHeadlessTests` allowlist +`Scene`. `--load` + default `BuildScene` unchanged |
| AW-025 | infra | S | **done** | AW-024, AW-014 | `--scene headless-default` MD5 **`8a5c0463599cd1e9cd5a585b9da7f3ca`** (1384 B) pinned in `HeadlessSimSnapshotFormatTests` (cooks the real `.toml` end-to-end). **JIT == NativeAOT** verified (win-x64 publish). **Default MD5 `6a13dd54…` unchanged** (JIT + AOT). |

### Wave 7 — verify + mandatory tail

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-026 | test | S | **done** | AW-023, AW-025 | `dotnet test` **740 pass**, 0 warning, 0 leak, 0 validation (Sandbox capture runs) |
| AW-027 | infra | S | **done** | AW-026 | AOT publish Sandbox + HeadlessSim + `AotComponentProbe` (win-x64, VS installer PATH trick). All 4 `model`-family captures + `--scene headless-default` + default byte-identical JIT==NativeAOT. `AotComponentProbe`: `AotRootingSmoke` 13, serialization/accumulator/input smokes PASS. |
| AW-028 | test | — | **done** | code tasks | Self code review — see the two dispatched audits below (AW-031); findings folded in directly rather than a separate pass |
| AW-029 | test | — | **done** | AW-028 | Requirements validation vs Decision Log D1–D10 — see table below, all ✅ except D10 (one documented deviation, unchanged from pre-audit) |
| AW-030 | test | — | **done** | AW-029 | Full project verification: build 0 warning, 740 tests, 0 leak, 0 validation, captures + HeadlessSim snapshots byte-identical pre/post audit fixes, AOT JIT==NativeAOT |
| AW-031 | infra | — | **done** | AW-030 | Double audit `csharp-lowlevel` (4.1/5, PASS-with-concerns) + `engine-architect` (4.1/5, PASS-with-concerns) — **no 🔴 from either**. 8 findings applied (see below); rest recorded as Deferred Work |
| AW-032 | docs | — | **done** | AW-031 | Converge: `CLAUDE.md`, `AVANCEMENT.md`, `BACKLOG.md`, board archived |

## DAG (wave depth)

```
W1: AW-001 ─┬─ AW-002 ─┐
            └─ AW-003 ─┼─ AW-004 ── AW-005
W2: AW-006 ─┬─ AW-007 ─┴─ AW-008
W3: AW-009 ── AW-010 ─┬─ AW-011 ─┬─ AW-012
    AW-013 (∥)        └─ AW-014 ─┴─ AW-015
W4: AW-016 ─┐  AW-017 (∥) ─┐
    (needs AW-006)         ├─ AW-018 ── AW-019
                           ┘  (needs AW-007)
W5: AW-018 ── AW-020 ── AW-021 ── AW-022 ── AW-023
W6: (AW-018 + AW-013) ── AW-024 ── AW-025
W7: (AW-023 + AW-025) ── AW-026 ── AW-027 ── AW-028 ── AW-029 ── AW-030 ── AW-031 ── AW-032
```

**Parallel-safe**: W1 AW-002∥AW-003 (diff files: `AgModelFormat.cs` + Pipeline vs `SceneBuilder.cs`).
W3 AW-013 ∥ the rest (MSBuild file, disjoint). W4 AW-016∥AW-017 (csproj+tests vs `GameWorld.cs`).
Everything else sequential (shared files / interface deps).

**Note on size**: ~32 S/M tasks — above the ~15 budget. The 7 waves are natural pause points; the
user can stop at any wave gate and resume next session (the board persists). No wave alone exceeds
~6 tasks.

## Requirements validation vs Decision Log D1–D10 (AW-029)

- **D1** Procedural assets → 3c. ✅ `model` scene = 1 glTF + real studio HDR + declarative lights/camera; no procedural ground/sky. `model` MD5 re-pinned `9a010fc3…`, human PASS.
- **D2** Scope = `model` family (single/grid/cluster) + `.agprefab` + `[physics]` → engine `PhysicsSystem`. ✅ `content/scenes/{model,grid,drop,metalrough}.toml` + `content/prefabs/helmet.toml`; `drop` cluster wires `PhysicsSystem`.
- **D3** TOML via Tomlyn, cook-side only. ✅ `Tomlyn 0.19.0` on `Agapanthe.Assets.Pipeline` only; `AssetsPipelineIsolationTests.OnlyTheCookSideDeclaresATomlynPackageReference` + `Tomlyn` absent from `Agapanthe.Assets` closure. Runtime reads the blob.
- **D4** `HeadlessSim --scene <key>` loads a cooked `.agscene`; `BuildScene` stays the default. ✅ `RunScene`; default MD5 `6a13dd54…` unchanged (JIT+AOT).
- **D5** `.agmodel` v2 per-mesh bounds, read by SceneBuilder + materializer. ✅ `AgModelFormat` v2, `MeshBounds.Compute` (body copied from `SceneBuilder`), `CookerVersion contenu3b-1`, blob re-pinned. `SceneBuilder` keeps the fallback for procedural (`BoundsRadius == 0`).
- **D6** New `Agapanthe.Scene` = {Core, World, Assets}, GPU-free. ✅ `EngineIsHeadlessTests` `[InlineData]` + HeadlessSim/App allowlists; no `Agapanthe.Engine` ref (returns `PhysicsSettings?`).
- **D7** Cook-time directive expansion → flat blob; runtime = trivial loop. ✅ `SceneCompiler.EmitGrid`/`EmitCluster` (`Hash(i)` copied from `SpawnDropScene`); `SceneMaterializer` = one loop.
- **D8** `.agprefab` = cook-time inlined include; no runtime prefab loader. ✅ `SceneCompiler.ResolveMembers` inlines; no `AssetKind.Prefab`.
- **D9** `world.ResolveMeshRefs(resolver)` after upload. ✅ `GameWorld.ResolveMeshRefs`; `ClientScenePresenter` calls it after `registry.Load` per key.
- **D10** Concrete scene files; `grid:NxN`/`drop:N` tokens + CLI model-arg removed. ⚠️ **partial**: scene files ✅, `grid:`/`drop:` token families removed (`ModelSceneRecipe` deleted) ✅; **CLI model-arg + `ModelContent.ResolveModelKey` KEPT** — `DriveSceneRecipe` still uses them (drive migrates in 3c). Documented deviation.

## Double audit (AW-031)

**`csharp-lowlevel` — PASS-with-concerns, 4.1/5, no 🔴.** New parser is structurally sound (ref-struct
`Reader`, `Take()` before every use, `RequireExhausted`, no `unsafe`/reflection/interop, `IsAotCompatible`).
**`engine-architect` — PASS-with-concerns, 4.1/5, no 🔴.** `Agapanthe.Scene` boundary is clean and real
(not just a comment); the `PhysicsSettings?`-out / caller-builds-`PhysicsSystem` seam is not a leaky
workaround (`PhysicsSettings` already lives in `World`). Neither audit found a blocker.

**Applied (both audits, this session):**
- 🟠 `AgSceneFormat.ReadCount`/`AgModelFormat.ReadCount` gained a `minRecordBytes` divisor — the old
  "count ≤ remaining bytes" check let a forged count size a reference-typed array (`SceneEntity[]`,
  `MeshAsset[]`, …) far beyond what the compressed blob could ever decompress to before the loop threw
  on the first field read. Every `new T[]`-driving call site updated with its record's minimum byte size.
- 🟠 `GameWorld.ResolveMeshRefs`: `_structuralDirty = true` moved from "after a fully-successful pass" to
  "before the loop" — a partial resolve (the resolver threw mid-iteration, per its Contenu-3a contract)
  used to leave already-rewritten `MeshRef`s without forcing the persistent-slot rebuild they need.
- 🟠 `WorldTransformForTest` gated on `Has<WorldTransform>() && Has<WorldPosition>()` (was `WorldTransform`
  alone) — the exact "wrong-archetype `Get<T>` = silent OOB read" pattern MP-0d's audit flagged as 🔴 elsewhere.
- 🟠 `SceneMaterializer.Materialize` now bounds-checks `LocalMat` (was only `LocalMesh`) — headless has no
  resolver to catch a bad material index later, unlike the client's `ModelKeyIndex.Resolve`.
- 🟠 `SceneCompiler`'s material-less-mesh convention (`MaterialIndex == -1`) now resolves to
  `model.Materials.Count` (the render registry's appended-default-material slot), not `0` — was silently
  wrong the first time a material-less mesh shared a model with a real material.
- 🟠 `AgModelFormat.Read` now rejects a non-finite `BoundsCenter`/`BoundsRadius` (the only v2 floats that
  skipped the existing `EnsureFinite` sweep) — a NaN passed the `BoundsRadius > 0f` branch silently.
- 🟠 `SceneCompiler.ToLight` rejects a zero-length directional-light direction at cook time — `Normalize`
  on it produced `(NaN,NaN,NaN)` with no validation-layer message, poisoning CSM silently.
- 🟠 `SceneTomlReader` now rejects unknown TOML keys per block (`RejectUnknownKeys`) — the class doc and
  the spec both promised this; nothing enforced it. A typo (`fov` for `fov_y`) silently took the default.
- 🟠 `SceneTomlReader.Double3Opt` reads through a new `Doubles()` helper (was `Floats()` → `float[]`) —
  `world_origin` / point-light `position` / fixed-camera `position` were quantized to float precision
  before ever reaching the `f64` blob field, defeating the engine's own double-precision-world thesis.
- 🟠 `SceneCompiler.ResolveMembers`/`Place`: a prefab member's local offset is now transformed by the
  *instance's* rotation/scale (was left in prefab-local space) — invisible with today's single-entity
  `helmet` prefab, silently wrong the first multi-part rotated prefab.
- 🟠 `CookRunner.CookScenes` incrementality now folds every `content/prefabs/*.toml` hash into a scene's
  state entry (was: only the scene `.toml` + `anyModelCooked`) — editing a prefab touched neither and left
  a stale `.agscene` shipped.
- 🟠 `CookRunner.CookScenes` scene keys now derive via `AssetKey.FromContentPath` (was
  `"scenes/" + GetFileNameWithoutExtension`) — the exact "directory jettisoned" bug Contenu-1's audit
  fixed for models had silently regressed for scenes; a subdirectory under `content/scenes/` would have
  collided instead of coexisting.
- Test coverage added to match: 6 new `AgSceneFormatTests` negative cases (forged count vs. minRecordBytes,
  non-ascending key table, out-of-range key index, unknown light kind, trailing bytes), 3 new
  `SceneTomlReaderTests` (unknown top-level/item key, `world_origin` double precision), 4 new
  `SceneMaterializerTests`/`SceneCompilerTests` (LocalMat bounds ×2, prefab-offset rotation,
  material-less-mesh convention) — **740 tests total** (+12 from the pre-audit 728), all captures and
  HeadlessSim snapshots re-verified byte-identical after every fix.
- `ModelContent.SpawnGrid`/`SpawnDropScene` (~75 lines) **deleted** — zero callers since `ModelSceneRecipe`
  was removed in Wave 5, and the "copied verbatim into `SceneCompiler`" claim had no drift guard while they
  sat dead (architect F9). `SceneCompilerTests` covers the cooked replacements directly.

**Not applied — recorded as Deferred Work below** (both auditors agreed these do not block the close):
`Agapanthe.Scene` glue-assembly question (F7), `ResourceRegistry.Unload` now has zero callers (F8),
`SceneLoader.LoadHeadless` naming (client calls it too, contrary to the audit brief's assumption — the
name is misleading, not wrong), `AGAPANTHE_VIEW` read directly in `SceneCameraApplier` (App env-read
invariant), point-light overflow / duplicate-directional silent-drop in `SceneLightRig`, cooked-HDR-path
warn-vs-throw asymmetry, `Scene` naming collisions (`Agapanthe.Scene` / `Agapanthe.Assets.Scene` /
`Rendering.SceneLights`), a few nits (dead `WriteVector2`/`ReadVector2` in `AgModelFormat`, double
dictionary lookup in `SceneMaterializer`).

## Deferred Work

- **3c** — migrate `drive` + `planet*` to `SceneRecipe`; then delete `ModelContent.ResolveModelKey` + the
  Sandbox CLI model-arg (D10 tail); procedural generators (ground/grass/sky/sphere) as `.agenv` +
  real `AssetKind.Environment`; game system/command-handler factory registries; runtime prefab instancing.
- **`AGAPANTHE_CULL_STATS` visible-count assertion** for `grid` — the bench spinner (`BenchSpinSystem`)
  was `ModelSceneRecipe`'s; a cooked scene has no hook for it yet. Re-add when `grid` grows a bench mode in 3c.
- Full-GPU `CookedSceneModel_RendersLikeDirectLoad` fidelity test — covered for now by the CPU
  transform-composition test + the capture gate (a headless-GPU test harness is still absent, project-wide).
- `SceneCamera.Fixed` + `SceneRestore` blocks are parsed/round-tripped but unused (reserved for `drive`/`planet*` in 3c).
- 🟠 **`Agapanthe.Engine` cannot name `MaterializeResult`** (closure `{Core, World}` pinned) and `Agapanthe.App`
  carries Vulkan, so `result.Physics → PhysicsSystem` / `result.RestorePath → RequestRestore` wiring is
  hand-duplicated per host (`SceneRecipe`, `HeadlessSim.RunScene`) and will triple at `RunDedicatedServer`.
  Correct call for 3b; the resolution (a thin `Agapanthe.Engine.Scene` glue assembly, or letting `Engine`
  take `Assets`+`Scene`) is the concrete shape of the pending `App`/`App.Client` split — decide it there.
- 🟠 **`ResourceRegistry.Unload` has zero callers** — its only exerciser (`ModelSceneRecipe` +
  `AGAPANTHE_UNLOAD_TEST`) was deleted in Wave 5. `Unload` needs a real `GraphicsDevice`, so it cannot be a
  headless unit test; a leak-guard for it (the per-material descriptor-set leak `AVANCEMENT.md` records
  hiding this exact way) needs a live-GPU exercise path again, ideally as part of 3c's `SceneRecipe`
  reload/swap story.
- 🟡 `SceneLoader.LoadHeadless` is called by the client too (`SceneRecipe.cs`) despite its name — either
  fold it into `SceneMaterializer` or rename when 3c touches this file next.
- 🟡 `SceneCameraApplier` reads `AGAPANTHE_VIEW` directly (`Environment.GetEnvironmentVariable`), against
  the S30 invariant that all env reads live in `HostOptions.FromEnvironment` — thread a `HostOptions.ViewOverride`
  when this file is next touched.
- 🟡 `SceneLightRig`: a point light beyond `Points.Length` is silently dropped, a second `directional`
  block silently overwrites the first — should name the limit / reject the duplicate when authored.
- 🟡 A cooked scene naming a missing HDR warns and renders without IBL; every other cooked reference (a
  missing model) throws. Acceptable pending Contenu-2b (HDR is a raw runtime path, not an `AssetKey`, so
  it cannot resolve through the same manifest-driven "always throw" contract yet) — revisit there.
- 🟡 Naming collision: `Agapanthe.Scene` (assembly+namespace) vs `Agapanthe.Assets.Scene` vs
  `Rendering.SceneLights`/`SceneBuilder` forces a qualifier or an alias at 3 call sites. Livable; cheapest
  to fix at a future rename, not now.

## Wave Log

- **2026-09-09** — board created, rollback point `9a5ed08`. Contenu-3a board archived. Starting Wave 1.
- **2026-09-09** — Wave 1 done (AW-001…005). `.agmodel` **v2** (per-mesh bounds). Captures
  byte-identical. Blob hash re-pinned. **Gate before Wave 2.**
- **2026-09-09** — Wave 2 done (AW-006…008). `.agscene` binary format (`AGSC`, DeflateStream,
  bounds-checked reader) + `SceneDefinition` DTO + `AssetKind.Scene` + `AssetCatalog.LoadScene`.
  `dotnet build` 0 warning; `dotnet test` **704 pass**. **Gate before Wave 3 (cook-side / TOML).**
- **2026-09-11** — Wave 7 done, milestone **CLOSED**. Full verify (740 tests, 0 warning, 0 leak, 0
  validation), AOT publish Sandbox+HeadlessSim+probe JIT==NativeAOT. Double audit `csharp-lowlevel` +
  `engine-architect`, both **PASS-with-concerns 4.1/5, no 🔴**. 12 🟠 findings applied (bounds-check
  hardening on both cooked formats' `ReadCount`, `ResolveMeshRefs` dirty-flag-before-partial-failure,
  `LocalMat` validation, material-less-mesh convention fix, NaN bounds rejection, zero-direction light
  rejection, TOML unknown-key rejection, TOML double-precision authoring, prefab-offset rotation, cook
  incrementality for prefabs + scene key derivation via `AssetKey.FromContentPath`) + dead code removed
  (`ModelContent.SpawnGrid`/`SpawnDropScene`, 0 callers). All 4 captures + 2 HeadlessSim snapshots
  re-verified byte-identical after every fix. See "Double audit" + "Deferred Work" sections above.
  Converging docs next.
- **2026-09-09** — Wave 6 done (AW-024…025). `HeadlessSim --scene <key>` loads the same cooked
  `.agscene` the Sandbox does (`RunScene`). `HeadlessSim.csproj` refs `Agapanthe.Scene` + imports the
  cook targets (StbImageSharp enters the AOT closure — accepted, no Vulkan). `--scene headless-default`
  MD5 `8a5c0463…` pinned + **JIT == NativeAOT**; default `6a13dd54…` **unchanged**. `dotnet test`
  **728 pass**. **Gate before Wave 7.**
- **2026-09-09** — Wave 5 done (AW-020…023). Visual verdict **PASS** on all 4 cooked `model`-family
  scenes; MD5s re-pinned (see Pinned artefacts). **Gate before Wave 6 (HeadlessSim convergence).**
- **2026-09-09** — Wave 5 code done (AW-020…022). `Agapanthe.App/Scene/`: `SceneCameraApplier`,
  `SceneLightRig`, `SceneInput`, `ClientScenePresenter`, `SceneRecipe(string)`. `SandboxGame` swapped
  to `new SceneRecipe("model"|"grid"|"drop"|"metalrough")`; `ModelSceneRecipe` deleted. +1 test.
  `dotnet build` 0 warning; `dotnet test` **727 pass**. **AW-023 capture gate: awaiting human visual
  verdict on the new `model`/`grid` composition + new MD5s.**
- **2026-09-09** — Wave 4 done (AW-016…019). `Agapanthe.Scene` project (`{Core, World, Assets}`,
  GPU-free, allowlisted). `GameWorld.ResolveMeshRefs(MeshRefResolver)`. `SceneMaterializer` +
  `MaterializeResult` + `SceneLoader.LoadHeadless` — cooked `SceneDefinition` → live entities with
  `MeshHandle.Invalid` + `AssetRef` identity, physics block → `PhysicsSettings?` for the caller.
  +10 tests. `dotnet build` 0 warning; `dotnet test` **726 pass**. **Gate before Wave 5 (client).**
- **2026-09-09** — Wave 3 done (AW-009…015). Cook-side TOML → `.agscene`: `Tomlyn 0.19.0` (cook-only,
  guarded), `SceneAuthoring`/`SceneTomlReader`/`SceneCompiler`/`AgSceneWriter`, `CookRunner.CookScenes`
  (7 assets: 2 models + 5 scenes), `build/Agapanthe.Cook.targets` extracted + imported by Sandbox,
  6 `.toml` files. +11 tests. `dotnet build` 0 warning; `dotnet test` **715 pass**. Cooker verified
  end-to-end (`grid.agscene` 526 B / 100 entities). **Gate before Wave 4 (`Agapanthe.Scene` runtime).**
