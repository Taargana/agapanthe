# Absolute Work Board — Contenu-3c-2 : `LandingChallenge` system + migrate `planet-challenge`

**Status**: `completed` — 2026-09-12, all 5 waves done (incl. BW-015b resume fix), double audit findings applied, docs converged (§10 outcome added to spec)
**Spec**: `docs/plans/2026-09-11-content-3c-scene-systems-design.md` (§1-8 approved 4.30/5; §9 "3c-2 design"
added 2026-09-12, concrete decomposition of what 3c-1 deliberately deferred — not a fresh brainstorm/spec
review cycle, since D1-D10 and the registry architecture are already locked and unchanged)
**Session**: 36
**Rollback point**: commit `e97b72e` (Contenu-3c-1, closed)

## Scope

2ᵉ of 3 gated sub-phases of Contenu-3c. `.agscene` v2→v3: `SceneSystemKind.LandingChallenge` + its fields
on `SceneSystem`. New `LandingChallengeSystemFactory` (client). Migrate `planet-challenge` off its hand-coded
`PlanetChallengeSceneRecipe` onto `SceneRecipe("planet-challenge")` + authored TOML. Delete
`PlanetChallengeSceneRecipe`/`PlanetStage`/the now-dead `PlanetContent`/`SandboxCameras` members (verified
0-caller after migration). Closes D7 (`SceneSystem.QuicksavePath`).

**Out of scope** (3c-3): `drive` migration, `ground_quad` generator, `ModelContent.ResolveModelKey` + CLI arg.

## Project conventions (detected, same as 3c-1)

.NET 10 solution, xUnit tests in `tests/Agapanthe.Tests`, `dotnet build`/`dotnet test` via `Agapanthe.slnx`,
TDD expected, PowerShell primary shell on this machine. Cooked-format pattern: reader `public`/writer
`internal`, `Reader` ref struct bounds-checked via `ReadCount(minRecordBytes)`. Commits on explicit request only.

## Wave 1 — format v3

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| BW-001 | code | S | **done** | — | `SceneDefinition.cs`: `SceneSystemKind.LandingChallenge = 1`; `SceneSystem` gains `ZoneCenter`/`ZoneRadius`/`SurfaceBand`/`DropHeight`/`TargetCount`/`ShotBudget`/`QuicksavePath` (additive to existing `ProbeModel`/`ProbeLocalMesh`/`ProbeLocalMat`/`ProbeRadius`) |
| BW-002 | code | M | **done** | BW-001 | `AgSceneFormat.cs`: `Version = 3` (v1/v2 rejected, re-cook message); `[[system]]` reader/writer extended per §9's binary layout for `kind=1`; key-table builder unaffected (already folds `Systems[*].ProbeModel`) |
| BW-003 | test | S | **done** | BW-002 | `AgSceneFormatTests.cs`: round-trip a `LandingChallenge` system record (`RoundTrip_LandingChallengeSystem`); v2 rejection test (`Read_RejectsV2WithARecookMessage`). 782 tests total (+2), 0 warning |

## Wave 2 — cook-side authoring

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| BW-004 | code | M | **done** | BW-001 | `SceneAuthoring.cs`/`SceneTomlReader.cs`: `AuthoredSystem` gains the new fields; `[[system]] kind = "landing_challenge"` reader (required/rejected key split, mirrors `probe_drop`) |
| BW-005 | code | S | **done** | BW-004 | `SceneCompiler.cs`: `ToSystem` resolves `landing_challenge` — validates `physics.mu > 0` is present in the same scene (a `LandingChallenge` system with no attractor is a cook-time error, not a runtime throw) |
| BW-006 | test | S | **done** | BW-005 | `SceneTomlReaderTests.cs`/`SceneCompilerTests.cs`: new-kind parse + compile, missing-`zone_center`/unknown-key/missing-attractor/uniform-gravity rejections. 788 tests total (+6), 0 warning |

## Wave 3 — client factory

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| BW-007 | code | M | **done** | BW-002 | `samples/Sandbox/Systems/LandingChallengeSystemFactory.cs` (new): `Kind => LandingChallenge`, `Stage => PostSimulation`; resolves probe template + `result.Physics` attractor (throws if null); constructs existing `LandingChallengeSystem` unchanged; wires `ApplyCommand`→`TryShoot`, `RecipeInput.WireProbeKey`, F5 quicksave via `spec.QuicksavePath` (fallback `"challenge.save"`) |
| BW-008 | code | S | **done** | BW-007 | `SandboxGame.SceneSystems` gains `new LandingChallengeSystemFactory()` |
| BW-009 | test | S | **done (deviation)** | BW-007 | No direct unit test — `Sandbox` isn't referenced by `Agapanthe.Tests` (same constraint 3c-1's `ProbeDropSystemFactory` hit; not flagged by either 3c-1 audit). The null-`Physics` path is unreachable in practice (`SceneCompiler.ToSystem` already rejects `landing_challenge` without an attractor at cook time — BW-006's tests cover that). Verified instead at the integration level in Wave 5 (build + capture + HeadlessSim D9 guard) |

## Wave 4 — content authoring + old-code deletion

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| BW-010 | config | M | **done** | BW-003, BW-006 | Baked numbers via throwaway script (`bake_challenge.cs`, scratchpad) reproducing `PlanetContent.SetupPlanetChallenge`/`FramePlanetChallengeCamera` at env-var defaults; reused planet.toml/planet-drop.toml's exact shared sun/physics numbers for consistency. Authored `content/procedural/beacon.toml` + `content/scenes/planet-challenge.toml` (planet+sun+beacon entities, physics attractor, `[[system]] kind="landing_challenge"`, fixed camera aimed at beacon) |
| BW-011 | code | M | **done** | BW-008, BW-010 | Deleted `PlanetChallengeSceneRecipe.cs`, `PlanetStage.cs`, and the now-fully-dead `PlanetContent.cs` (`SetupPlanetScene`/`SetupPlanetChallenge`/`BuildSphereModel`/`BuildBlackEnvironment`/`BuildProbeSphere`/`ChallengeSetup` — 0 remaining caller, grep-verified). Deleted `SandboxCameras.FramePlanetChallengeCamera`. `SandboxGame.Scenes`: `planet-challenge` → `SceneRecipe("planet-challenge")`. 788 tests still green, 0 warning, clean build |

## Wave 5 — verify + mandatory tail

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| BW-012 | test | S | **done** | BW-011 | 788 tests green. `planet-challenge` capture pinned (`ea6ba9101b972940bf4ed00fb6d5e25c`) + human visual verdict PASS (planet+sun+beacon visible, camera aimed correctly). `model`/`grid`/`drop`/`metalrough`/`planet`/`planet-drop` all re-verified byte-identical to their pins; `drive` unaffected, 0 leak. **Deviation**: `AGAPANTHE_LOAD`/F5 resume round-trip NOT re-exercised live — `LandingChallengeSystem.cs` (the class holding the seed/resume logic Contenu-3a's 🔴 fixed) is `git diff`-confirmed byte-unmodified by this migration (last touched in commit `25d1126`), so the behavior is provably unchanged, not just assumed |
| BW-013 | infra | S | **done** | BW-012 | AOT publish Sandbox + HeadlessSim. **JIT == NativeAOT** on `planet-challenge` capture and the default HeadlessSim snapshot. `HeadlessSim --scene planet-challenge` → exit 1 confirmed on the AOT binary too (D9, message correctly names `LandingChallenge`) |
| BW-014 | test | — | **done** | BW-013 | Self code review + Requirements validation vs D1-D10 — see below |
| BW-015 | infra | — | **done** | BW-014 | Double audit + all findings applied, incl. the 🔴 (human chose option (a): implement the real fix) |
| BW-016 | docs | — | todo | BW-015 | Converge: `CLAUDE.md`, `AVANCEMENT.md`, `BACKLOG.md`, spec §10 outcome, archive board; present 3c-3 scope |

## Double audit findings (BW-015)

`csharp-lowlevel` + `engine-architect` dispatched in parallel against the full diff.

**🔴 BLOCKING — found independently by both audits, FIXED (human chose option (a), the general fix):**

`AGAPANTHE_LOAD`/F5-quicksave resume for `planet-challenge` is broken by this migration, and the declarative
infra currently has **no way to fix it without a cross-cutting design change**:
- The old hand-coded `PlanetStage.Build`/`SetupPlanetChallenge` passed `spawnEntities: !loadMode`, keeping the
  world empty in load mode so `GameWorld.Load` (which hard-throws on a non-empty world) could populate it.
- `SceneRecipe.Build`/`SceneMaterializer.Materialize` always spawn every `[[entity]]` unconditionally — there is
  no "assets-only, skip spawn" mode. `content/scenes/planet-challenge.toml` authors no `[restore]` block (nor
  could it meaningfully — the path would need to be `AGAPANTHE_LOAD`'s runtime value, not a baked constant), so
  `SceneRecipe` takes its `else if (sim.Options.LoadPath ...)` branch: warn and ignore.
- **This is not new to 3c-2** — the same mechanism failure affects `planet-drop` since 3c-1 (its old recipe used
  the identical `spawnEntities: !loadMode` pattern). It went unnoticed there because `planet-drop` never had a
  human-verified resume *feature* the way `planet-challenge`'s F5 quicksave did (a VS-3/Contenu-3a acceptance
  criterion: *"une reprise F5→AGAPANTHE_LOAD reprend correctement le budget de tirs"*).
**Fix (BW-015b, an additional sub-wave)**: `SceneMaterializer.Materialize` gained a `bool spawnEntities = true`
parameter (both overloads) — when `false`, every model the scene references is still decoded (a client must
upload them before `GameWorld.Load` can resolve a restored entity's `AssetRef`), `PhysicsSystem` is still
attached, but no entity is spawned, keeping the world empty for `GameWorld.Load`. `SceneLoader.LoadHeadless`
passes it through (default `true`, so `HeadlessSim`'s call site is unaffected — verified, MD5 unchanged).
`SceneRecipe.Build` now: computes `spawnEntities = sim.Options.LoadPath is not { Length: > 0 }` BEFORE
materializing, and — when `AGAPANTHE_LOAD` is set — requests the restore from that runtime path directly
(taking priority over any scene-authored `[restore]` block, which no scene uses anyway; a fixed cook-time path
could never match a runtime override). This generalizes what the deleted `PlanetStage.Build`'s
`spawnEntities: !loadMode` did, but for **every** `SceneRecipe`-driven scene, not just the 3 planet-family ones.
`LandingChallengeSystemFactory`'s log line restored to correctly advertise the resume path (it's true again);
the `"planet-challenge (resume save)"` launchSettings.json profile restored (env vars trimmed to just
`AGAPANTHE_SCENE`/`AGAPANTHE_LOAD`, the dead knobs from before were not restored).

**Live-verified end-to-end** (not just unit-tested): `AGAPANTHE_SAVE=x.save` on `planet-challenge` (3 entities)
→ relaunch with `AGAPANTHE_LOAD=x.save` → log shows `"0 entities from cooked data"` then
`"[Contenu-3a] world restored ... 3 entities"`, 0 leak. Same round-trip re-verified on `planet-drop` (proving
the SAME latent bug, present since 3c-1, is fixed too — it was never caught before because `planet-drop` had no
human-verified resume acceptance criterion the way `planet-challenge`'s F5 did). Re-verified again on the
NativeAOT publish, byte-identical behavior. All 7 pinned captures (`model`/`grid`/`drop`/`metalrough`/`planet`/
`planet-drop`/`planet-challenge`) re-confirmed unchanged — the fix only activates when `AGAPANTHE_LOAD` is set,
which none of the pinned captures do. 2 new `SceneMaterializerTests` cases
(`Materialize_SpawnEntitiesFalse_LeavesWorldEmpty_ButStillLoadsModelsAndPhysics`,
`Materialize_SpawnEntitiesTrue_IsTheDefault`).

**🟠 Applied:**
- `LandingChallengeSystemFactory`'s null-`Physics` guard checked the wrong invariant (`Physics is null` instead
  of the cook-time rule `Mu > 0`) — a `[physics] mu = 0` scene with a `landing_challenge` system would pass the
  guard and silently spawn every probe 120 m from the world origin instead of the planet (the same failure shape
  as 3c-1's 🔴, just not yet triggered because no such scene exists). Fixed: guard now checks `Mu: > 0.0`.
- `SceneCompiler.ToSystem`'s cook-time attractor check validated only `AttractorMu > 0`, not `AttractorSurfaceRadius > 0`
  — a scene with `mu > 0` and `surface_radius = 0` would cook clean and yield a challenge whose landing band sits
  at the planet centre. Fixed: both now required.
- No numeric validation on the new `landing_challenge` fields — negative `target_count`/`shot_budget` reached
  `AgSceneFormat`'s `checked((uint)...)` as a raw `OverflowException` instead of a named authoring error; NaN in
  any double field would make every `QuerySurfaceContacts` comparison silently false (unwinnable challenge, no
  gate to catch it). Fixed: `SceneCompiler.ValidateLandingChallengeFields` rejects `target_count < 1`,
  `shot_budget < 1`, `shot_budget < target_count`, and non-finite/non-positive `zone_radius`/`surface_band`/
  `drop_height`/`probe_radius`/`zone_center`. 5 new tests.
- `QuicksavePath` was an unvalidated filesystem path taken verbatim from a cooked blob (`File.Create` target) —
  inconsistent with Contenu-2's posture on blob-embedded paths. Fixed: cook-time rejection of an absolute path or
  a `..` traversal segment; factory's exception filter widened to also catch `ArgumentException`/`NotSupportedException`.
- `CookRunner.CookerVersion` was not bumped for the v2→v3 format change (the incremental-skip safety net was
  disarmed — it only produced correct output this run because an unrelated new procedural asset happened to
  force a full re-cook anyway). Fixed: bumped to `"contenu3c-2"`.
- Stale comment on `AgSceneFormat`'s `[[system]]` `ReadCount(minRecordBytes: 45)` asserted ProbeDrop was "the
  only kind" — now false. Fixed: comment states 45 is the floor across variants, not an exhaustive claim.
- `SandboxEnv.EnvDouble`/`EnvVector3` had zero remaining callers after `PlanetContent.cs`'s deletion. Deleted
  (with the now-unused `System.Numerics` import).

**🟡 Noted, deferred** (SceneAuthoring's shared `ProbeModel`/`ProbeRadius` head being `required` will force a
future non-spawning system kind to author a dummy probe model — revisit when a 3rd kind actually needs it;
`world_origin` applied to entities but not `AttractorCenter`/`Centre`/`ZoneCenter` — inert today since every
scene uses a zero origin, pre-existing from 3c-1, widened here): rolled into 3c-3 Deferred Work.

Post-fix re-verification: **797 tests green (+9 total this wave)**, all 7 pinned captures byte-identical
(re-cooked from the `CookerVersion` bump, unaffected by the resume fix), HeadlessSim D9 guard + default
snapshot re-confirmed, Sandbox + HeadlessSim **JIT == NativeAOT** re-confirmed after every fix in this section
(including the resume round-trip itself, live-tested on the AOT binary).

## DAG (wave depth)

```
W1: BW-001 ── BW-002 ── BW-003
W2: (needs BW-001) BW-004 ── BW-005 ── BW-006
W3: (needs BW-002) BW-007 ─┬─ BW-008
                            └─ BW-009
W4: (needs BW-003,BW-006) BW-010 ── (needs BW-008,BW-010) BW-011
W5: BW-012 ── BW-013 ── BW-014 ── BW-015 ── BW-016
```

**Parallel-safe**: W2 can run alongside W1 once BW-001 lands (disjoint files: format vs. cook-side authoring —
but W2's `SceneCompiler` reads the `SceneSystemKind` enum from BW-001, so BW-001 is a hard blocker, not W1 as
a whole). W3 BW-008 ∥ BW-009 (disjoint: `SandboxGame.cs` one-liner vs. new test file). Everything else
serialized — small board, low parallelism value, matches 3c-1's own bias toward sequential safety.

## Requirements validation (BW-014)

- **D3** two registries, not three. ✅ No 3rd registry added — `LandingChallengeSystemFactory` wires its own input inline, same as `ProbeDropSystemFactory`.
- **D4** version-bump-and-recook. ✅ `AgSceneFormat.Version = 3`, v1 AND v2 both rejected with dedicated re-cook messages.
- **D7** `AGAPANTHE_SAVE`/F5-quicksave name collision. ✅ **Closed this phase** — `SceneSystem.QuicksavePath` is authored scene data; `LandingChallengeSystemFactory` reads it (fallback `"challenge.save"`), no env-var read for the quicksave path anymore.
- **D9** `HeadlessSim` hard-errors on scene systems. ✅ Re-verified on the 2nd system kind: `--scene planet-challenge` → exit 1, message names `LandingChallenge`, confirmed on both JIT and the AOT binary.
- Self code review (`git diff --stat`): 14 files changed, 421 insertions / 398 deletions — net near-zero, consistent with a migration (delete hand-coded recipe, add declarative equivalent). No stray `.csproj`/project-reference changes. No leftover dead code beyond the deliberate deletions (`PlanetChallengeSceneRecipe.cs`, `PlanetStage.cs`, `PlanetContent.cs`, `SandboxCameras.FramePlanetChallengeCamera` — all grep-verified 0-caller before deletion). `LandingChallengeSystem.cs` itself is untouched (confirmed via `git diff`), so the Contenu-3a-fixed resume/seed behavior carries over unmodified, not re-implemented.

## Deferred Work (carried + new)

- 3c-1's leftovers not yet applied: `ProbeRadius`/`Every` validation, `MoveSpeed`/`ShadowDistance` sentinel
  documentation, duplicate-`Kind` factory picking first match, `ApplyEnvironment` missing `default` arm,
  `bake_planet.cs` not committed.
- 3c-3 scope (unchanged): `ground_quad` generator, `drive` migration (drops CLI arg per D6),
  `ModelContent.ResolveModelKey` deletion, remaining `SandboxCameras`/`RecipeInput` cleanup.
