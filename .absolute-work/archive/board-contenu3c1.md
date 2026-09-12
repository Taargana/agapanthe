# Absolute Work Board — Contenu-3c-1 : format v2 + registres + `planet`/`planet-drop`

**Status**: `completed` — 2026-09-12, all 8 waves done, double audit findings applied, docs converged (§8 outcome added to spec)
**Spec**: `docs/plans/2026-09-11-content-3c-scene-systems-design.md` (APPROVED 4.30/5, scored review 3.70 → 4.30)
**Session**: 35
**Created**: 2026-09-11
**Predecessor board**: `.absolute-work/archive/board-session34-Contenu3b.md` (Contenu-3b, completed)

1ʳᵉ des 3 sous-phases de Contenu-3c (dernier sous-jalon du domaine Contenu).
Bump `.agscene` v2 (attracteur physique, caméra `Fixed` cuite, environnements
`Black`/`ProceduralSky`), deux registres de fabriques (`ISceneSystemFactory` côté
`Agapanthe.App`, générateurs procéduraux cook-time côté `Agapanthe.Assets.Pipeline`),
migration de `planet` + `planet-drop` vers `SceneRecipe`/`.agscene`.
**3c-2 (hors scope)** : `LandingChallenge` + migration `planet-challenge`.
**3c-3 (hors scope)** : générateur `ground_quad` + migration `drive` + nettoyage final.

## Rollback Point

`910306c` (clean tree, `feat(contenu-3b): declarative scenes — .agscene/.agprefab + SceneLoader`)

## Project Conventions

- .NET 10, `TreatWarningsAsErrors` (0 warning), xUnit. `dotnet build Agapanthe.slnx` / `dotnet test`.
- NativeAOT: `samples/HeadlessSim` + `tools/AotComponentProbe` publish; AOT publish needs `PATH`
  prefixed with the VS Installer dir (vswhere).
- Cooked-format pattern (`.agscene`/`.agmodel`): binary, LE-explicit, versioned header refused on
  mismatch, `Reader` ref struct with `Take`/`ReadCount(minRecordBytes)` (hardened Contenu-3b audit)
  before allocating; **reader `public`, writer `internal`** + `[assembly: InternalsVisibleTo]`.
- Closed tagged unions for a cooked format's variant data (`SceneLightKind`/`SceneCameraMode`/
  `SceneEnvironmentMode`, now `SceneSystemKind`) — never an open/generic key-value bag.
- Cook-tool pattern: `tools/*`/cook-side code never a shipped `ProjectReference`;
  `AssetsPipelineIsolationTests` scans every `.csproj`.
- Module rules (pinned by `EngineIsHeadlessTests`): `Agapanthe.Scene` closure = `{Core, World,
  Assets}` — **must not** reference `Agapanthe.Engine` (this is why `MaterializeResult`/`SceneSystem`
  carry only data; the caller in `Agapanthe.App` constructs the concrete `ISystem`).
- Conversation FR; code / commits / docs EN. Board git-tracked.

### Pinned artefacts (before 3c-1)

- `model` HDR MD5 `9a010fc311dd51b74f755d306d4a819f` / `grid` `c314e6a1df18eeb665bcc8ca1d7b78af` /
  `drop` `2fbf87fa403f8141ebfc3b7dcfc5636d` / `metalrough` `c4cf460574c3036b799564d22c01847b` —
  **unchanged** (this milestone touches no code path they exercise beyond the `.agscene` version
  bump forcing a re-cook of their unchanged content).
- `planet-drop` HDR (this env's baseline) `bc8440ab746c769cc2fa7db80fd16d54` — **will be re-pinned**
  (new cooked composition replaces the hand-coded `PlanetDropSceneRecipe`).
- `planet` HDR — **not previously pinned** (no capture gate existed for it pre-3c); **new pin this
  milestone**.
- `HeadlessSim` default `6a13dd54c1db32d35a15332bff0395e7` (1857 B) / `--drive`
  `f6053226f8c13b55589b29be103a8e66` (231 B) / `--scene headless-default`
  `8a5c0463599cd1e9cd5a585b9da7f3ca` (1384 B) — all **unchanged** (none of these scenes declare a
  `[[system]]`, so the v2 format bump changes their blob bytes but not their materialized content).
- `ComponentRegistry.All.Count` = 13 (unchanged — 3c adds no ECS component).
- 740 tests at HEAD.

## Waves

### Wave 1 — `.agscene` v2 format (DTO + binary)

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-001 | code | M | **done** | — | `SceneDefinition.cs`: `ScenePhysics` attractor fields, `SceneCamera.MoveSpeed`/`ShadowDistance`, `SceneEnvironmentMode.{ProceduralSky,Black}`, new `SceneSystemKind`(only `ProbeDrop` — `LandingChallenge` deferred to 3c-2, not pre-declared, YAGNI)/`SceneSystem`, `SceneDefinition.Systems` |
| AW-002 | code | M | **done** | AW-001 | `AgSceneFormat.cs` → **v2**: `[physics]` attractor block, `[fixed]` camera 2 fields, `[environment]` 2 new no-payload modes, `[[system]]` section after `[restore]` (key table extended to fold in `SceneSystem.ProbeModel`); v1 → `AgSceneException` "re-run the asset cook" |
| AW-003 | test | M | **done** | AW-002 | `AgSceneFormatTests`: +8 tests (attractor round-trip, `Fixed` w/ new fields, `ProceduralSky`/`Black`, one `ProbeDrop` system incl. probe-only key-table slot, v1 rejection message, unknown system kind, forged systemCount) |

### Wave 2 — cook-side TOML authoring (attractor/camera/environment/system)

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-004 | code | S | **done** | AW-001 | `SceneAuthoring.cs`: `AuthoredPhysics` attractor fields, `AuthoredCamera.MoveSpeed`/`ShadowDistance`, `AuthoredEnvironment` gains `ProceduralSky`/`Black`, new `AuthoredSystem` + `AuthoredScene.Systems` |
| AW-005 | infra | S | **done** | — | Extracted `SceneTomlReader`'s private scalar helpers to `internal static TomlHelpers` (new file) — pure refactor |
| AW-006 | code | M | **done** | AW-004, AW-005 | `SceneTomlReader.cs`: `[[system]]` reader (`probe_drop` kind, per-kind key list), `[physics]` reads `mu`/`attractor_center`/`surface_radius` (all-or-nothing), `[camera]` reads `move_speed`/`shadow_distance`, `[environment]` accepts `procedural_sky`/`black` (mutually exclusive with `hdri` and each other) |
| AW-007 | code | M | **done** | AW-006, AW-002 | `SceneCompiler.cs`: `ToPhysics`(inline)/`ToEnvironment`/`ToSystem` — `ToSystem` resolves probe mesh 0 + material via the appended-default convention, validates the model exists at cook time |
| AW-008 | test | M | **done** | AW-007 | `SceneTomlReaderTests` +11 (system happy path, missing/unknown kind, missing/unknown probe_drop key, attractor happy+partial, fixed camera new fields, both new env modes, hdri+black conflict) + `SceneCompilerTests` +3 (`ToPhysics` attractor, `ToSystem` resolve, `ToSystem` unknown model) |

### Wave 3 — procedural generator registry (cook-time only)

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-009 | code | M | **done** | AW-005 | `Procedural/{ProceduralGenerators,UvSphereGenerator,ProceduralTomlReader}.cs` — `UvSphereGenerator` reimplements `Primitives.UvSphere`'s tessellation directly into SoA arrays (cook-side must not reference `Agapanthe.Rendering`), reduced material-factor TOML surface |
| AW-010 | code | S | **done** | AW-009 | `CookRunner.cs`: new `CookProcedural` phase before `CookScenes` — globs `content/procedural/*.toml`, keys via `AssetKey.FromContentPath`, incrementality = `SHA256(toml bytes)` alone, writes `AssetKind.Model` blobs. `CookerVersion` → `contenu3c-1` |
| AW-011 | infra | S | **done** | AW-010 | `build/Agapanthe.Cook.targets`: confirmed the existing `content/**/*` glob already covers `content/procedural/` — doc comment added, no functional change needed |
| AW-012 | test | M | **done** | AW-010 | `UvSphereGeneratorTests` (9, incl. geometry-matches-`Primitives.UvSphere` verbatim-copy proof) + `CookRunnerProceduralTests` (4: model-kind blob, incremental, key subdirectory, scene referencing a procedural model end-to-end) |

### Wave 4 — `Agapanthe.Scene` materializer (runtime template for probes)

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-013 | code | S | **done** | AW-002 | `SceneMaterializer.cs`: model-loading loop also loads every `SceneSystem.ProbeModel`; new **public** `BuildRuntimeTemplate(AssetKey, int, int, IReadOnlyDictionary<AssetKey,ModelAsset>) → ImportedEntitySpec` |
| AW-014 | test | S | **done** | AW-013 | `SceneMaterializerTests` +4: probe-only model lands in `Models`, `BuildRuntimeTemplate` bounds/transform/identity, unknown model throws, out-of-range mesh throws |

### Wave 5 — `Agapanthe.App` scene-system registry + environment/camera plumbing

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-015 | code | S | **done** | AW-013 | `Scene/ISceneSystemFactory.cs`, `IGame.SceneSystems => []`, `SimSceneContext.SceneSystemFactories` (required), `AppHost` threads `game.SceneSystems`; 3 test call sites in `AppHostContractTests` updated |
| AW-016 | code | M | **done** | AW-015, AW-013 | `SceneRecipe.Build`: throws before the presentation tail if `def.Systems.Count > 0 && presentation is null`; dispatch loop after `ClientScenePresenter.Apply` throws for an unregistered kind |
| AW-017 | code | M | **done** | AW-002 | `Scene/ProceduralSky.cs` (relocated `ModelContent.BuildSkyEnvironment` verbatim) + `Scene/BlackEnvironment.cs` (relocated `PlanetContent.BuildBlackEnvironment` verbatim); `ClientScenePresenter.ApplyEnvironment` rewritten as a full switch (derives sun direction from the scene's directional light for `ProceduralSky`); `SceneCameraApplier`'s `Fixed` branch copies `MoveSpeed`/`ShadowDistance` when `> 0` |

### Wave 6 — HeadlessSim guard + Sandbox client wiring

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-018 | code | S | **done** | AW-013 | `samples/HeadlessSim/Program.cs`: `RunScene` hard-errors (exit 1, named-system message) when `result.Definition.Systems.Count > 0`, right after `SceneLoader.LoadHeadless` returns |
| AW-019 | code | M | **done** | AW-015, AW-013 | `samples/Sandbox/Systems/ProbeDropSystemFactory.cs`: `BuildRuntimeTemplate` → `ResolveMeshRef` → concrete `ImportedEntitySpec` → `new ProbeDropSystem(...)`; wires `RecipeInput.WireProbeKey`→`DropOne()` via `ApplyCommand`; guards single-slot `ApplyCommand` |
| AW-020 | code | S | **done** (partial, by design) | AW-019, AW-016 | `SandboxGame.SceneSystems => [new ProbeDropSystemFactory()]` added now. **Deviation**: the `Scenes` list's `new SceneRecipe("planet"/"planet-drop")` entries deferred to Wave 7 (added atomically with authoring the TOML + deleting the old hand-coded recipes) — adding them now would be dead weight, since `PlanetSceneRecipe`/`PlanetDropSceneRecipe` still match first by list order |

### Wave 7 — content authoring + old-code deletion

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-021 | config | M | **done** | AW-009, AW-007 | Baked numbers derived via a throwaway single-file script (`dotnet run bake_planet.cs`, scratchpad) reproducing `SetupPlanetScene`/`FramePlanetCamera`/`SetupPlanetDrop`/`FramePlanetDropCamera`'s exact formulas at their env-var defaults. Authored `content/procedural/{planet-surface,sun-surface,probe}.toml` + `content/scenes/{planet,planet-drop}.toml`. **Deviation**: full bit-identical reproduction of the old float-rounding quirks was NOT pursued (no prior pinned hash exists for `planet`/`planet-drop` — the actual gate is new-MD5 + human visual verdict, matching the spec's §5.1) |
| AW-022 | code | M | **done** | AW-020, AW-021 | Deleted `Scenes/PlanetSceneRecipe.cs`, `Scenes/PlanetDropSceneRecipe.cs`. Deleted `PlanetContent.SetupPlanetDrop` + `SandboxCameras.FramePlanetCamera`/`FramePlanetDropCamera` (only-used-by-them). Kept `BuildSphereModel`/`SetupPlanetScene`/`BuildBlackEnvironment`/`PlanetStage.*`/`FramePlanetChallengeCamera`/`SetupLights`/`FrameCamera`/`NarrowBounds`/`ResolveModelKey` — still needed by `planet-challenge`/`drive`. `SandboxGame.Scenes` cut over: `planet`/`planet-drop` are now `SceneRecipe(string)`; `planet-challenge`/`drive` stay hand-coded |

### Wave 8 — verify + mandatory tail

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| AW-023 | test | S | **done** | AW-022 | Full `dotnet test` **778→780 pass** (2 tests added post-audit); `planet`/`planet-drop` MD5s pinned + human visual verdict PASS; `--scene planet-drop` → exit 1 confirmed; `model`/`grid`/`drop`/`metalrough`/default snapshot/`planet-challenge`/`drive` all re-verified unaffected |
| AW-024 | infra | S | **done** | AW-023 | AOT publish Sandbox + HeadlessSim + `AotComponentProbe`, all win-x64. **JIT == NativeAOT** on every capture (model/grid/drop/metalrough/planet/planet-drop) + every HeadlessSim snapshot (default/headless-default/planet/planet-drop-refusal). `AotComponentProbe` PASS (count 13). 0 leak on every Sandbox run. **Re-run post-audit-fixes**: all MD5s unchanged from the pre-audit pins (`planet` `99e2f4a3…`, `planet-drop` `81ddf074…`, HeadlessSim default `6a13dd54…`) — `planet-drop`'s capture window has no probe spawned (needs a B keypress), so the attractor fix is invisible to the pinned pixel output; the fix is covered by the new unit tests instead |
| AW-025 | test | — | **done** | AW-024 | Self code review: `git status --porcelain` diff scanned end-to-end — no stray `.csproj`/project-reference changes (topology intact), no leftover dead code beyond the deliberate deletions, `build/Agapanthe.Cook.targets` doc-comment-only change |
| AW-026 | test | — | **done** | AW-025 | Requirements validation vs Decision Log D1–D10 — see table below, all ✅ |
| AW-027 | test | — | **done** | AW-026 | Full verification: build 0 warning, 778 tests, 0 leak, 0 validation, all pinned captures/snapshots byte-identical JIT==NativeAOT (see AW-023/024) |
| AW-028 | infra | — | **done** | AW-027 | Double audit `csharp-lowlevel` + `engine-architect`, findings applied — see below |
| AW-029 | docs | — | todo | AW-028 | Converge: `CLAUDE.md`, `AVANCEMENT.md`, `BACKLOG.md`, spec §outcome, board archive; present 3c-2 scope for the next gate |

## Double audit findings (AW-028)

Both `csharp-lowlevel` and `engine-architect` dispatched in parallel against the full Wave 1-7 diff.

**🔴 Blocking (found independently by both audits, fixed):**
- `SceneMaterializer.Materialize` parsed/compiled/round-tripped the Newtonian attractor (`Mu`/`AttractorCenter`/`SurfaceRadius`) through the entire pipeline but never called `PhysicsSettings.WithAttractor(...)` — always used the 3-arg uniform-gravity ctor, silently giving `planet-drop` **zero gravity**. Fixed: `Mu > 0.0` now branches to `.WithAttractor(...)`. Regression tests added (`Materialize_AttractorPhysics_AppliesWithAttractor`, `Materialize_MuZero_TakesTheUniformGravityPath_ByteIdenticalToBefore`) — csharp-lowlevel explicitly flagged the missing `ScenePhysics → PhysicsSettings` test tier.

**🟠 Applied:**
- `BuildRuntimeTemplate` missing a `localMat` bounds check (mirrors the pre-existing entity-path check) — added.
- `CookProcedural`/`CookScenes` blindly stripped a `.toml` suffix by slice — a near-miss extension from Win32 wildcard matching could silently mis-key a file. Added `StripTomlExtension` helper that verifies the suffix and throws `AssetException` otherwise.
- `SimSceneContext` would have transitively named a GPU-coupled type (`ISceneSystemFactory.Create` names `PresentationSceneContext`) — moved `SceneSystemFactories` from `SimSceneContext` to `PresentationSceneContext`; `AppHost.cs` and the 3 broken `AppHostContractTests.cs` call sites updated.
- Single-slot `SimulationHost.ApplyCommand` guard was duplicated per-factory (easy to forget in a future factory) — hoisted into `SceneRecipe`'s dispatch loop via `ReferenceEquals`, removed from `ProbeDropSystemFactory`.
- `AGAPANTHE_LOAD` silently ignored for every cooked `SceneRecipe` scene with no `[restore]` block (true since Contenu-3b, not a 3c-1 regression — the spec's §3.5 "unaffected" claim was inaccurate) — added a `Log.Warn`, matching `DriveSceneRecipe`'s existing precedent.

**🟡 Noted, deferred** (nits from both audits — `ProbeRadius`/`Every` validation, `MoveSpeed`/`ShadowDistance` sentinel semantics, duplicate-`Kind` factory silently picking the first match, `ApplyEnvironment` missing a `default` arm, committing the `bake_planet.cs` derivation script for traceability): non-blocking, rolled into 3c-2/3c-3 Deferred Work below rather than expanding this board.

Post-fix re-verification: 780 tests green, `planet`/`planet-drop`/HeadlessSim MD5s all unchanged, JIT==NativeAOT re-confirmed on Sandbox + HeadlessSim AOT publishes, D9 guard re-confirmed on the AOT binary (exit 1).

## Requirements validation vs Decision Log D1–D10 (AW-026)

- **D1** Full migration of all 4 recipes. ✅ **Partial by design**: `planet`/`planet-drop` migrated this phase (3c-1); `planet-challenge`/`drive` remain hand-coded, explicitly deferred to 3c-2/3c-3 per the phase split — not a shortfall, the locked scope for THIS board.
- **D2** Procedural geometry → cook-time generators; procedural environment stays runtime, deferred to 2b. ✅ `UvSphereGenerator` (cook-time, verified byte-for-byte vs `Primitives.UvSphere`); `ProceduralSky`/`BlackEnvironment` relocated verbatim to `Agapanthe.App`, no `AssetKind.Environment` touched.
- **D3** Two registries, not three. ✅ `ISceneSystemFactory` (client, via `IGame`) + `ProceduralGenerators` (cook-time-only, invisible to `IGame`). No command-handler registry — `ProbeDropSystemFactory` wires its own input.
- **D4** `.agscene` v2, version-bump-and-recook. ✅ `AgSceneFormat.Version = 2`, v1 rejected with a re-cook message, `CookerVersion` → `contenu3c-1`.
- **D5** Planet camera framers → cook-time trig baked into `SceneCamera.Fixed`. ✅ `MoveSpeed`/`ShadowDistance` fields added; `FramePlanetCamera`/`FramePlanetDropCamera` deleted, zero new runtime camera logic (`SceneCameraApplier`'s `Fixed` branch only copies 2 more fields).
- **D6** `drive` drops its CLI arg. **Deferred to 3c-3** (unchanged this board — `drive` wasn't in 3c-1's scope).
- **D7** `AGAPANTHE_SAVE` collision resolved via `SceneSystem.QuicksavePath`. **Deferred to 3c-2** (the field only matters once `LandingChallenge` exists — `planet-challenge` still uses the old F5/env-var path this board).
- **D8** Entity name→`GlobalId` lookup dropped from scope. ✅ Never built — `ProbeDropSystem`'s `Centre` is plain `Double3` data, no entity reference needed.
- **D9** `HeadlessSim` hard-errors on scene systems. ✅ Verified live: `--scene planet-drop` → exit 1 with the named-system message; `--scene planet` (no systems) → exit 0.
- **D10** 3-phase gated execution. ✅ This board is 3c-1 exactly as scoped; 3c-2/3c-3 explicitly deferred with their own Deferred Work entries below.

## DAG (wave depth)

```
W1: AW-001 ── AW-002 ── AW-003
W2: AW-004 ─┐  AW-005 (∥) ─┐
            ├─ AW-006 ─┬─ AW-007 ─┬─ AW-008
    (needs AW-001)     │(needs AW-002)
                        └──────────┘
W3: (needs AW-005) AW-009 ── AW-010 ─┬─ AW-011 (∥)
                                     └─ AW-012
W4: (needs AW-002) AW-013 ── AW-014
W5: (needs AW-013) AW-015 ── AW-016
    (needs AW-002) AW-017 (∥ with AW-015/016 — disjoint files)
W6: (needs AW-013) AW-018 (∥)
    (needs AW-015,013) AW-019 ── (needs AW-016) AW-020
W7: (needs AW-009,AW-007) AW-021 ── (needs AW-020) AW-022
W8: (needs AW-022) AW-023 ── AW-024 ── AW-025 ── AW-026 ── AW-027 ── AW-028 ── AW-029
```

**Parallel-safe**: W2 AW-005 ∥ AW-004 (disjoint files — extraction vs. new DTO fields).
W3 AW-011 ∥ AW-012 (MSBuild glob vs. tests, disjoint). W5 AW-017 ∥ {AW-015, AW-016} (env/camera
files vs. registry/dispatch files, disjoint). W6 AW-018 ∥ {AW-019} (HeadlessSim vs. Sandbox,
disjoint projects). Everything else sequential (shared files / interface deps).

**Note on size**: 29 tasks across 8 waves — the 3-phase split (3c-1/3c-2/3c-3) already keeps this
under the ~40-task fused-board estimate the spec flagged as over budget; 3c-1 alone is comparable
in size to Contenu-3b's 7-wave board. No wave alone exceeds ~5 tasks.

## Deferred Work

_(carried from the spec's §7 "Out of scope" — nothing new discovered yet during planning)_

- **3c-2** — `SceneSystemKind.LandingChallenge` + `LandingChallengeSystemFactory` +
  `SceneSystem.QuicksavePath` wiring + `planet-challenge` migration. Gated on this board's close.
- **3c-3** — `ground_quad`/grass-image generator + `drive` migration (dropping its CLI arg,
  confirmed trade-off) + delete `SandboxCameras`/`PlanetContent`/`PlanetStage`/`RecipeInput`/
  `ModelContent.ResolveModelKey` entirely + `BenchSpinSystem`/`ChurnSystem` if still 0 callers.
- Procedural environment → real `.agenv`/`AssetKind.Environment` — Contenu-2b, untouched here.
- Entity name→`GlobalId` lookup — dropped from scope entirely (no consumer, D8).
- A generic "command-handler" registry — rejected (D3); each system factory wires its own input.
- Single-slot `SimulationHost.ApplyCommand`/`InputMap`/`SampleInput` — guarded (AW-019), not fixed
  at the framework level; a future 3rd input-wiring scene needs real multiplexing.
- `AGAPANTHE_INPUT_DEBUG`'s verbose free-fly logging — silently lost for `planet`/`planet-drop`
  (routes through `SceneInput.EnableFreeFly`, which has no debug branch); not ported.
- `Agapanthe.App`→`App`/`App.Client` split, `MaterializeResult` wiring dedup, `ResourceRegistry.
  Unload`'s zero-caller gap, `AssetRef[]` GC-cost measurement — all pre-existing deferred items,
  unaffected by this milestone.

## Wave Log

- **2026-09-11** — board created, rollback point `910306c`. Contenu-3b board archived. Spec
  approved 4.30/5 after 2 review iterations. Task graph ready. **Gate before Wave 1.**
- **2026-09-11** — Wave 1 done (AW-001…003). `.agscene` bumped to **v2**: `ScenePhysics` attractor,
  `SceneCamera.MoveSpeed`/`ShadowDistance`, `SceneEnvironmentMode.{ProceduralSky,Black}`,
  `SceneSystemKind`/`SceneSystem` (only `ProbeDrop` — `LandingChallenge` deferred to 3c-2, kept out
  per YAGNI rather than pre-declared per the spec's original snippet), key table extended to fold in
  probe-only models. v1 rejected with a re-cook message. `dotnet build` 0 warning; `dotnet test`
  **747 pass** (+7 from Wave 1's own tests). **Gate before Wave 2 (cook-side TOML authoring).**
- **2026-09-11** — Wave 2 done (AW-004…008). `SceneAuthoring`/`SceneTomlReader`/`SceneCompiler`
  extended: `[[system]] kind="probe_drop"`, `[physics]` attractor (all-or-nothing), `[camera]`
  `move_speed`/`shadow_distance`, `[environment]` `procedural_sky`/`black` (mutually exclusive).
  `TomlHelpers.cs` extracted for Wave 3 reuse. `ToSystem` validates the probe model at cook time.
  `dotnet build` 0 warning; `dotnet test` **761 pass** (+14). **Gate before Wave 3 (procedural
  generator registry).**
- **2026-09-11** — Wave 3 done (AW-009…012). Cook-time procedural generator registry:
  `UvSphereGenerator` (verified byte-for-byte against `Primitives.UvSphere`'s tessellation),
  `ProceduralGenerators` dispatch, `ProceduralTomlReader`. `CookRunner.CookProcedural` phase produces
  ordinary `AssetKind.Model` blobs (a scene references `procedural/xxx` exactly like a glTF model —
  verified end-to-end). `CookerVersion` → `contenu3c-1`. `dotnet build` 0 warning; `dotnet test`
  **774 pass** (+13). **Gate before Wave 4 (`Agapanthe.Scene` materializer: `BuildRuntimeTemplate`).**
- **2026-09-11** — Wave 4 done (AW-013…014). `SceneMaterializer` loads `SceneSystem.ProbeModel`s
  into `MaterializeResult.Models` (fixes the "how does a system's probe get uploaded" gap the spec
  review caught) + new public `BuildRuntimeTemplate` — the GPU-free `ImportedEntitySpec` template a
  client-side `ISceneSystemFactory` resolves handles onto. `dotnet build` 0 warning; `dotnet test`
  **778 pass** (+4). **Gate before Wave 5 (`Agapanthe.App` scene-system registry).**
- **2026-09-11** — Wave 5 done (AW-015…017). `ISceneSystemFactory` registry threaded through
  `IGame`/`SimSceneContext`/`AppHost`; `SceneRecipe.Build` dispatches `def.Systems` after the
  presentation tail. `ProceduralSky`/`BlackEnvironment` relocated verbatim into `Agapanthe.App/Scene/`;
  `ClientScenePresenter.ApplyEnvironment` now a full switch. `SceneCameraApplier`'s `Fixed` branch
  copies the 2 new fields. No new tests this wave (plumbing exercised end-to-end once `planet`/
  `planet-drop` are wired in Waves 6-7). `dotnet build` 0 warning; `dotnet test` **778 pass**
  (unchanged). **Gate before Wave 6 (HeadlessSim guard + Sandbox client wiring).**
- **2026-09-11** — Wave 6 done (AW-018…020). `HeadlessSim.RunScene` refuses (exit 1) a scene
  declaring systems. `ProbeDropSystemFactory` wired: `BuildRuntimeTemplate` → `ResolveMeshRef` →
  `ProbeDropSystem`, self-wires `ApplyCommand`/B-key via the existing `RecipeInput`, guards the
  single-slot fragility. `SandboxGame.SceneSystems` registered; the `Scenes`-list cutover to
  `SceneRecipe("planet"/"planet-drop")` deferred to Wave 7 by design (avoids dead-weight entries
  while `content/scenes/{planet,planet-drop}.toml` don't exist yet). `dotnet build` 0 warning;
  `dotnet test` **778 pass** (unchanged); `model` capture re-verified byte-identical (`9a010fc3…`)
  after the full re-cook the `.agscene`/`CookerVersion` bumps forced. **Gate before Wave 7 (author
  `planet`/`planet-drop` content + cut over + delete old code).**
- **2026-09-12** — Wave 7 code done (AW-021…022). `planet`/`planet-drop` migrated to cooked
  `SceneRecipe`. New MD5s: `planet` `99e2f4a345a75a51d0c498c17ed08908`, `planet-drop`
  `81ddf07438af72f7b828bc0acbc5c2e2` (pending human visual verdict). `HeadlessSim --scene planet-drop`
  confirmed exit 1 (system guard); `--scene planet` runs (exit 0, no systems). Default MD5
  `6a13dd54…` unchanged. `model`/`grid`/`drop`/`metalrough` captures + `planet-challenge`/`drive`
  (still hand-coded) re-verified unaffected. `dotnet build` 0 warning; `dotnet test` **778 pass**
  (unchanged — no new tests, this wave is content + deletion). Human visual verdict **PASS**
  (2026-09-12) on both `planet` and `planet-drop`. MD5s pinned. **Wave 7 closed. Gate before Wave 8
  (final verify + AOT + mandatory tail + double audit).**
