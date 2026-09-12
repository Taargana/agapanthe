# Absolute Work Board — Contenu-3c-3 : `ground_quad` generator + migrate `drive` + final cleanup

**Status**: `completed` — 2026-09-12, all 6 waves done, double audit findings applied, docs converged (§12 outcome added to spec) — closes Contenu-3c (3/3)
**Spec**: `docs/plans/2026-09-11-content-3c-scene-systems-design.md` §11 (design confirmed with the user
2026-09-12 — not a fresh brainstorm/spec-review cycle, D1-D10 and the registry architecture stay locked; §11
is the concrete decomposition of `drive`'s genuinely different input shape)
**Session**: 37
**Rollback point**: commit `aa7dedb` (Contenu-3c-2, closed)

## Scope

Final of 3 gated sub-phases of Contenu-3c. `.agscene` v3→v4: `SceneSystemKind.DriveControl`, `SceneSystem`'s
probe fields relaxed to optional, `MaterializeResult.SpawnedEntities`. New cook-time `ground_quad` generator.
Migrate `drive` off `DriveSceneRecipe` onto `SceneRecipe("drive")` + authored TOML (drops its CLI arbitrary-model
arg per D6, already accepted). Widen `SceneRecipe`'s single-slot guard to `InputMap`/`SampleInput` (closes 3c-2's
🟡 F4). Delete `DriveSceneRecipe.cs`, the now-fully-dead `ModelContent.cs`/`SandboxCameras.cs`, plus pre-existing
0-caller debt (`RecipeInput.WireFreeFly`, `BenchSpinSystem`, `ChurnSystem`) swept in the same pass.

**Out of scope**: procedural environment as real `.agenv` (Contenu-2b), entity name→`GlobalId` (D8), `world_origin`
not applied to `AttractorCenter`/`Centre`/`ZoneCenter` (3c-1/3c-2 debt, still inert).

## Project conventions (unchanged from 3c-1/3c-2)

.NET 10 solution, xUnit tests in `tests/Agapanthe.Tests`, `dotnet build`/`dotnet test` via `Agapanthe.slnx`, TDD
expected, PowerShell primary shell. Cooked-format pattern: reader `public`/writer `internal`, `Reader` ref struct
bounds-checked via `ReadCount(minRecordBytes)`. Commits on explicit request only.

## Wave 1 — format v4

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| CW-001 | code | M | **done** | — | `SceneDefinition.cs`: `SceneSystemKind.DriveControl = 2`; `SceneSystem.ProbeModel`/`ProbeLocalMesh`/`ProbeLocalMat`/`ProbeRadius` relaxed `required`→defaulted; new `ControlledEntityIndex`/`MoveSpeed` fields; `MaterializeResult.SpawnedEntities` (new `IReadOnlyList<EntityRef?>`) |
| CW-002 | code | M | **done** | CW-001 | `AgSceneFormat.cs`: `Version = 4` (v1/v2/v3 all rejected, re-cook messages); `[[system]]` reader/writer extended for `kind=2` (`controlledEntityIndex:i32 \| moveSpeed:f32`); `ReadCount` floor lowered 45→25 (DriveControl is now the shortest record) |
| CW-003 | test | S | **done** | CW-002 | `AgSceneFormatTests.cs`: `RoundTrip_DriveControlSystem_HasNoProbeModel` (probe stays `AssetKey.None`), `Read_RejectsV3WithARecookMessage`. 802 tests total (+5 incl. Wave 3's), 0 warning |

## Wave 2 — cook-side authoring

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| CW-004 | code | M | **done** | — | `Agapanthe.Assets.Pipeline/Procedural/GroundQuadGenerator.cs` (new): `ModelContent.BuildGroundModel`/`BuildGrassImage` moved verbatim, parameterized by TOML `size`; registered in `ProceduralGenerators.ByName["ground_quad"]` |
| CW-005 | test | S | **done** | CW-004 | `tests/Agapanthe.Tests/GroundQuadGeneratorTests.cs` (new, 10 tests): geometry/UV/bounds/material/image invariants pinned directly (can't diff against `ModelContent` — it's deleted in this same phase — so the formula itself is pinned, mirroring `UvSphereGeneratorTests`' posture) |
| CW-006 | code | M | **done** | CW-001 | `SceneAuthoring.cs`/`SceneTomlReader.cs`: `AuthoredSystem`'s probe fields relaxed to optional; `[[system]] kind = "drive_control"` reader (`move_speed`/`controlled_entity_index`). **Deviation (design gap found during implementation)**: `[[entity]]` had NO body support at all before this — only `[[cluster]]` built a `SceneBody`. Added `body`/`velocity` keys to the shared `[[entity]]`/`[[grid]]`/`[[cluster]]` reader (`ItemKeys`) and `AuthoredItem.HasBody`/`Velocity`, reusing the existing `InverseMass`/`Restitution`/`Radius` fields — needed for `drive`'s one steerable body, in scope for this wave (cook-side authoring) |
| CW-007 | code | S | **done** | CW-006 | `SceneCompiler.cs`: `Compile`'s `AuthoredItemKind.Entity` branch builds a `SceneBody` when `item.HasBody`; `ToSystem` resolves `drive_control` before the shared probe-model load (routed on `s.Kind` first), validates `controlled_entity_index` in range of the flat `entities` list and names an entity with a non-null `Body`, validates `move_speed > 0` |
| CW-008 | test | S | **done** | CW-007 | `SceneTomlReaderTests.cs` (+3), `SceneCompilerTests.cs` (+7: entity-with-body, entity-without-body, drive_control resolve/out-of-range/non-body-target/invalid-move_speed). 821 tests total (+19 this wave), 0 warning (1 unrelated flaky GC-alloc test confirmed pre-existing, passes in isolation) |

## Wave 3 — materializer + guard

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| CW-009 | code | M | **done** | CW-001 | `SceneMaterializer.Materialize`: captures `SpawnBody`'s returned `EntityRef` into a new parallel list → `MaterializeResult.SpawnedEntities` (`null` at an index where `spawnEntities:false` skipped it, or for a drawable-only entity); system-model-loading loop skips a `None` `ProbeModel` |
| CW-010 | test | S | **done** | CW-009 | `SceneMaterializerTests.cs`: `SpawnedEntities` populated correctly (body→ref, drawable→null, spawnEntities:false→all-null), `None` `ProbeModel` doesn't reach `loadModel` |
| CW-011 | code | S | **done** | — | `SceneRecipe.cs`: widened the single-slot guard to snapshot/compare `InputMap`/`SampleInput` alongside `ApplyCommand` (closes 3c-2 F4) |

## Wave 4 — client factory

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| CW-012 | code | M | **done** | CW-009, CW-011 | `samples/Sandbox/Systems/DriveControlSystemFactory.cs` (new): `Kind => DriveControl`, `Stage => Stage.Input`; resolves `result.SpawnedEntities[spec.ControlledEntityIndex]` (throws if null/OOR); wires `InputMap`/`SampleInput`/`ApplyCommand` exactly as `DriveSceneRecipe` today, scaled by `spec.MoveSpeed`; returns a private no-op `ISystem` (drive never needed a per-tick system, only the input callbacks — same as before this migration) |
| CW-013 | code | S | **done** | CW-012 | `SandboxGame.SceneSystems` gains `new DriveControlSystemFactory()`. 821 tests, 0 warning |

## Wave 5 — content authoring + old-code deletion

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| CW-014 | config | M | **done** | CW-003, CW-008, CW-005 | Baked numbers by temporarily instrumenting `DriveSceneRecipe`/`SandboxCameras.FrameCamera`/`SetupLights` with `Console.Error.WriteLine`, running once against `models/DamagedHelmet.glb`'s actual cooked bounds, then deleting the file (the logging was never committed). Authored `content/procedural/ground.toml` + `content/scenes/drive.toml` (fixed model per D6, `procedural_sky`, zero-G physics, `[[system]] kind="drive_control"`) |
| CW-015 | code | M | **done** | CW-013, CW-014 | Deleted `DriveSceneRecipe.cs`, `ModelContent.cs` (fully dead), `SandboxCameras.cs` (fully dead), `BenchSpinSystem.cs`/`ChurnSystem.cs` (pre-existing 0-caller) — all grep-verified. Trimmed `RecipeInput.WireFreeFly` (pre-existing 0-caller since Contenu-3b, kept `WireProbeKey`). `SandboxGame.Scenes`: `drive` → `SceneRecipe("drive")`, now last in the list (all 8 scenes cooked). 821 tests still green, 0 warning |

## Wave 6 — verify + mandatory tail

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| CW-016 | test | S | **done** | CW-015 | 821 tests green; `drive` capture pinned (`9030f6a64e9587b05d5abb99b487b1b9`) + human visual verdict PASS (helmet + ground + procedural sky render, correctly framed); all 7 other scenes re-verified byte-identical |
| CW-017 | infra | S | **done** | CW-016 | AOT publish Sandbox + HeadlessSim. **JIT == NativeAOT** on `drive` capture and default snapshot. `HeadlessSim --scene drive` → exit 1 confirmed on both JIT and the AOT binary (D9, message names `DriveControl`) |
| CW-018 | test | — | **done** | CW-017 | Self code review + Requirements validation — see below. **Found and reverted a stray artifact**: an accidental "go" had been appended into an unrelated doc comment in `src/Agapanthe.Engine/SystemScheduler.cs` (not part of this phase's actual work) — reverted via `git checkout`, rebuilt/retested clean |
| CW-019 | infra | — | **done** | CW-018 | Double audit `csharp-lowlevel` + `engine-architect`, findings applied — see below |
| CW-020 | docs | — | todo | CW-019 | Converge: `CLAUDE.md`, `AVANCEMENT.md`, `BACKLOG.md`, spec §12 outcome, archive board — **closes Contenu-3c (3/3)** |

## Double audit findings (CW-019)

`csharp-lowlevel` + `engine-architect` dispatched in parallel against the full 3c-3 diff (the closing phase of
Contenu-3c — both audits also reviewed the domain's cumulative 🟡 deferred debt for anything that should block
close). Both confirmed the `SystemScheduler.cs` stray-artifact revert held (`git diff` empty for that assembly).

**🟠 Found independently by both audits, fixed:**
- **`drive_control` + a pending restore was a hard startup crash**, contradicting the design doc's own claim that
  it "correctly no-ops during a resume." `SceneRecipe` computes `spawnEntities = false` for `AGAPANTHE_LOAD` (or
  a `[restore]` block), which leaves `MaterializeResult.SpawnedEntities` entirely null — but
  `DriveControlSystemFactory` resolved its target eagerly in `Create`, throwing `InvalidOperationException` with
  a message blaming a "materializer ordering bug" for what is actually an ordinary, reachable invocation. Unlike
  `LandingChallengeSystem`'s lazy first-tick seed (which only needs a *count*, re-derivable from world state
  post-restore), `DriveControl` genuinely cannot resolve "the body at cook-time index N" after a restore — the
  restored world's entities are not that spawn-ordered list. Fixed by rejecting the combination explicitly and
  loudly at both points where it can be known: `SceneCompiler.ToSystem` rejects `drive_control` + a scene
  `[restore]` block at cook time; `SceneRecipe.Build` separately rejects `drive_control` + the runtime
  `AGAPANTHE_LOAD` override (which cook time can't see) before dispatch, with a message naming the actual
  incompatibility. Live-verified: `AGAPANTHE_SCENE=drive AGAPANTHE_LOAD=...` now fails fast with the new named
  error (still 0 leak on the abort path); `planet-challenge`'s own resume re-verified unaffected.
- **`[[entity]] body = true` on a multi-mesh model or multi-member prefab silently spawned N co-located,
  mutually-penetrating rigid bodies** — `Place` stamps the same `SceneBody` onto every (member × mesh)
  `SceneEntity` it emits, and the cook-time validation added for `drive_control` only checked that the
  *targeted* entity had a body, not that exactly one was produced. Fixed: `SceneCompiler.Compile`'s `Entity`
  branch now counts entities emitted by `Place` and throws if `body = true` produced anything other than exactly
  one. 2 new tests (multi-mesh model, multi-member prefab).

**🟡 Applied:**
- `body`/`velocity` were added to the *shared* `ItemKeys` array (used by `[[entity]]`/`[[grid]]`/`[[cluster]]`),
  so `[[grid]] body = true` or `[[cluster]] velocity = [...]` parsed successfully and were then silently
  discarded (`EmitGrid` always passes `body: null`; `EmitCluster` hardcodes `Velocity = Zero`) — a direct
  erosion of `RejectUnknownKeys`' own guarantee that no accepted authoring key is ever ignored. Fixed:
  `ReadItems` now rejects `body`/`velocity` outright when `kind != AuthoredItemKind.Entity`.
- The `ProbeModel.IsNone` skip in `SceneMaterializer`'s model-loading loop was unconditional — for any kind
  *other* than `DriveControl`, a `None` probe model is only reachable via a forged/corrupt v4 blob (the field
  was `required` through 3c-2's authoring surface), and silently skipping its load would defer the failure to a
  much later, less legible lookup failure inside a client factory. Fixed: throws immediately naming the kind if
  `ProbeModel.IsNone` and `Kind != DriveControl`.
- The mutable `_pendingBrake` latch and the `KeyPressed` subscription lived on `DriveControlSystemFactory`
  itself — a registry singleton (one instance for the process lifetime in `SandboxGame.SceneSystems`), unlike
  its two stateless siblings. Latent today (`Create` runs once per process), but a future in-process scene
  reload would accumulate subscriptions and leak stale latched state across calls. Fixed: `PendingBrake` moved
  onto the returned `DriveControlSystem` instance, so each `Create` call gets its own latch tied 1:1 to the
  `KeyPressed` subscription that same call adds.
- (Endorsed by the architect as worth doing opportunistically, not a live bug): `SceneRecipe`'s factory lookup
  used `FirstOrDefault`, silently picking the first of several factories registered for the same `Kind`. Fixed:
  `SingleOrDefault`-shaped dispatch now throws a named "N factories registered" error on ambiguity.
- (Endorsed by both audits as the one accumulated 🟡 across all 3 phases worth closing before the domain closes,
  not left as backlog): `world_origin` was applied to entity positions but consumed raw by `ScenePhysics.
  AttractorCenter`/`SceneSystem.Centre`/`ZoneCenter` — inert today (every scene uses a zero origin) but a
  silent-wrong-answer landmine for the first scene that wants both. Fixed: `SceneCompiler.Compile` now rejects
  a non-zero `world_origin` combined with a physics attractor or a `probe_drop`/`landing_challenge` system, at
  cook time, with a message naming the tracked debt. 3 new tests (attractor+origin, probe+origin, origin-alone
  still succeeds).
- `CookRunner.CookerVersion` bumped to `"contenu3c-3"` for the `.agscene` v3→v4 change (the incremental-recook
  safety net must track every format bump — this was itself a finding in 3c-2's audit, applied consistently here).

**Not re-flagged** (both audits confirmed clean on request): `entityIndex` bookkeeping in `SceneMaterializer`'s
spawn loop stays correctly parallel to `def.Entities` in every branch; the key-table builder correctly filters
`AssetKey.None` before the ordinal sort, so `DriveControl`'s sentinel never perturbs it; `ReadCount(minRecordBytes:
25)` is still a true, correctly-computed floor across all 3 system kinds; the `stackalloc` clump buffer in
`GroundQuadGenerator` is safe and verbatim; relaxing `required` on the shared probe head does not weaken
`probe_drop`/`landing_challenge`'s own TOML readers (both keep explicit `?? throw`); the widened `SceneRecipe`
guard doesn't false-positive on the first factory to claim a slot; module topology holds end-to-end across all
3 phases (`MaterializeResult.SpawnedEntities` names only `EntityRef`, a `World` type — `Agapanthe.Scene` stays
`{Core, World, Assets}`-clean).

Post-fix re-verification: **831 tests green** (+10 this pass, 2 unrelated pre-existing flaky GC-alloc tests
confirmed — pass in isolation), all 8 scenes re-cooked (`CookerVersion` bump forced it) and byte-identical to
their prior pins, `drive` + `AGAPANTHE_LOAD` now fails with the intended named error (0 leak on the abort path),
`planet-challenge`'s resume re-verified unaffected, HeadlessSim D9 guard + default snapshot re-confirmed,
Sandbox + HeadlessSim **JIT == NativeAOT** re-confirmed on every capture/snapshot after every fix.

## Requirements validation (CW-018)

- **D6** `drive` drops its CLI arg. ✅ **Closed fully** — `content/scenes/drive.toml` fixes `models/DamagedHelmet.glb`; no scene anywhere in the codebase retains a CLI model argument or `ModelContent.ResolveModelKey` (deleted).
- **D9** `HeadlessSim` hard-error on scene systems. ✅ Re-verified on the 3rd kind (`DriveControl`), JIT + AOT.
- **3c-2 🟡 F2** (required probe head forces a dummy model for a non-spawning kind). ✅ Closed — `SceneSystem.ProbeModel`/`ProbeLocalMesh`/`ProbeLocalMat`/`ProbeRadius` relaxed to optional/defaulted; `DriveControl` authors none of them.
- **3c-2 🟡 F4** (`ApplyCommand`-only guard doesn't cover `InputMap`/`SampleInput`). ✅ Closed — `SceneRecipe`'s guard now snapshots/compares all three slots.
- Self code review (`git diff --stat`): 22 files changed, 511 insertions / 793 deletions — net negative, consistent with a final cleanup phase (2 hand-coded recipes' worth of code deleted, replaced by declarative data + 2 new generator/factory files). No stray `.csproj`/project-reference changes. A stray unrelated edit was found and reverted (see CW-018 above) — worth flagging to the double audit as something to specifically check wasn't missed elsewhere.

## DAG (wave depth)

```
W1: CW-001 ── CW-002 ── CW-003
W2: CW-004 ── CW-005 (∥ with CW-006/007/008 — disjoint files)
    (needs CW-001) CW-006 ── CW-007 ── CW-008
W3: (needs CW-001) CW-009 ── CW-010
    CW-011 (∥ — disjoint file, SceneRecipe.cs)
W4: (needs CW-009,CW-011) CW-012 ── CW-013
W5: (needs CW-003,CW-008,CW-005) CW-014 ── (needs CW-013,CW-014) CW-015
W6: CW-016 ── CW-017 ── CW-018 ── CW-019 ── CW-020
```

**Parallel-safe**: W2 CW-004/005 (generator) ∥ CW-006/007/008 (drive_control TOML) — genuinely disjoint files,
no shared symbols. W3 CW-009/010 (materializer) ∥ CW-011 (SceneRecipe guard) — disjoint files. Everything else
serialized, matching 3c-1/3c-2's bias toward sequential safety on a small board.

## Requirements validation (to fill at CW-018)

- D6 `drive` drops its CLI arg. Closes fully this phase — no scene anywhere retains a CLI model argument.
- D9 `HeadlessSim` hard-error: re-verify on the 3rd system kind (`DriveControl`).
- 3c-2's deferred 🟡 F2 (required probe head) and F4 (guard scope) — both closed this phase, not deferred again.

## Deferred Work (carried, unchanged)

Procedural environment → real `.agenv`/`AssetKind.Environment` (Contenu-2b) · entity name→`GlobalId` (D8, no
consumer) · `world_origin` not applied to `AttractorCenter`/`Centre`/`ZoneCenter` (inert while every scene uses
zero origin) · `ProbeRadius`/`Every` validation (3c-1, `DriveControl` doesn't touch these) · `MoveSpeed`/
`ShadowDistance` sentinel semantics not formally documented (3c-1) · duplicate-`Kind` factory picks first match
silently (3c-1) · `ApplyEnvironment` missing explicit `default` arm (3c-1) · derivation scripts (`bake_planet.cs`,
`bake_challenge.cs`, and this phase's) not committed for traceability.

Once this phase closes, **Contenu-3c is CLOS (3/3)** and the domain-level "cap vrai engine" backlog item moves
to whatever's next (2ᵉ slice dissemblable, UI-3, or another item from §4quater).
