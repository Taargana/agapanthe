# Absolute Work Board — Slice-2 : 2nd dissimilar slice (top-down orthographic app)

**Status**: `completed` (2026-09-12) — all 6 waves done, 21/21 tasks closed
**Spec**: `docs/plans/2026-09-12-slice2-topdown-design.md` (APPROVED 4.3/5, scored review by a separate
subagent — 8/8 factual claims verified true against the codebase; 1 real Consistency gap found and fixed
in the spec before decomposition)
**Session**: 38
**Rollback point**: commit `f3406ae` (Contenu-3c-3, closed — last commit before this milestone)

## Scope

Backlog §4quater's "2ᵉ slice dissemblable" — a genuinely separate 2nd sample app (`samples/TopDown`) with
a true orthographic top-down camera over a small flat diorama, proving `AppHost`/`IGame`/`SceneRecipe` are
reusable outside Sandbox. `Camera` gains a real `CameraProjection` (Perspective/Orthographic). `.agscene`
v4→v5 (`SceneCamera` projection fields). `EngineWindowAdapter` + `DriveControlSystemFactory` extracted from
Sandbox-`internal` into a new shared `Agapanthe.Platform.App` project. CSM shadows and PBR specular
orthographic correctness explicitly deferred (documented debt, not silently dropped).

## Project conventions (unchanged from Contenu-3c)

.NET 10 solution, xUnit tests in `tests/Agapanthe.Tests`, `dotnet build`/`dotnet test` via `Agapanthe.slnx`,
TDD expected, PowerShell primary shell. Commits on explicit request only.

## Wave 1 — `Camera`/`MathHelpers` orthographic projection

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| SL-001 | test | S | **done** | — | `tests/Agapanthe.Tests/OrthographicReversedZProjectionTests.cs` (new, TDD-first — confirmed red before SL-002: `CS0117` compile error, method didn't exist): mirrors `ReversedZProjectionTests.cs`'s 3 cases |
| SL-002 | code | S | **done** | SL-001 | `src/Agapanthe.Core/MathHelpers.cs`: `OrthographicVulkanReversed`. **First attempt failed the test** — copy-pasted the perspective formula (`M33=-1-M33; M43=-M43`), which assumes `w=view_z` (perspective's `M44=0`); orthographic's `w` is a constant `1` (`M44=1`, no divide), so the correct transform is `M33=-M33; M43=1-M43`. TDD caught the wrong formula immediately (near mapped to 0.1, not 1) — exactly the "proved by test, not hand-derived" the spec called for |
| SL-003 | code | S | **done** | SL-002 | `src/Agapanthe.Rendering/Camera.cs`: `CameraProjection` enum; `Camera.Projection`/`OrthoWidth`/`OrthoHeight`; `ProjectionMatrix` switches |
| SL-004 | test | S | **done** | SL-003 | `CameraTests.cs`: default-Perspective regression + Orthographic-uses-new-matrix. 836 tests total (+5), 0 warning |

## Wave 2 — `.agscene` v4→v5 + cook-side authoring

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| SL-005 | code | S | **done** | — | `src/Agapanthe.Assets/Scene/SceneDefinition.cs`: own `CameraProjection` enum (GPU-free copy, mirrors Rendering's); `SceneCamera` gains `Projection`/`OrthoWidth`/`OrthoHeight` (`Fixed`-only, always-serialized like `MoveSpeed`/`ShadowDistance`) |
| SL-006 | code | M | **done** | SL-005 | `src/Agapanthe.Assets/Scene/AgSceneFormat.cs`: `Version = 5` (v1-v4 all rejected, dedicated re-cook messages); `[fixed]` camera variant reader/writer gains `projection:u8 \| orthoWidth:f32 \| orthoHeight:f32`; unknown-byte projection value rejected via a validating local function, not a blind cast |
| SL-007 | test | S | **done** | SL-006 | `AgSceneFormatTests.cs`: round-trip orthographic + perspective-default Fixed camera; v4-rejection; unknown-projection-byte rejection (built via the existing raw-payload `Container()` helper, not a fragile byte-flip on compressed data) |
| SL-008 | code | M | **done** | SL-005 | `SceneAuthoring.cs`/`SceneTomlReader.cs`/`SceneCompiler.cs`: `AuthoredCamera` gains the 3 fields; `[camera] projection = "perspective"\|"orthographic"` (default perspective) + `ortho_width`/`ortho_height`; `ToCamera`'s `fixed` arm carries them + validates `ortho_width`/`ortho_height` are positive finite when orthographic (cook-time reject, not a runtime degenerate matrix) |
| SL-009 | test | S | **done** | SL-008 | `SceneTomlReaderTests.cs`/`SceneCompilerTests.cs`: parse+compile orthographic camera; unknown-projection-string + invalid-ortho-dimension rejections. 848 tests total (+12 this wave), 0 warning |
| SL-010 | code | S | **done** | — | `CookRunner.cs`: `CookerVersion` bumped to `"slice2"` for the v4→v5 change |

## Wave 3 — bridge: `SceneCameraApplier`

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| SL-011 | code | S | **done** | SL-003, SL-006, SL-008 | `src/Agapanthe.App/Scene/SceneCameraApplier.cs`: `Fixed` branch maps `Agapanthe.Assets.Scene.CameraProjection` → `Agapanthe.Rendering.CameraProjection` (two deliberately distinct enums — Assets can't reference Rendering — explicit switch, not a cast) and copies `OrthoWidth`/`OrthoHeight`. No unit test: `SceneCameraApplier` has zero existing test coverage anywhere (needs GPU-coupled `Renderer`/`FreeCameraController` to construct) — verified live at Wave 6, matching every prior camera-field addition's precedent |

## Wave 4 — `Agapanthe.Platform.App` extraction

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| SL-012 | code | M | **done** | — | New project `src/Agapanthe.Platform.App` (added to `Agapanthe.slnx`; refs Core/World/Assets/Scene/Engine/App/Platform); moved `EngineWindowAdapter.cs` (namespace `Agapanthe.Platform.App`) + `DriveControlSystemFactory.cs` (namespace `Agapanthe.Platform.App.Systems`) verbatim, made `public` |
| SL-013 | code | S | **done** | SL-012 | `Sandbox.csproj` references the new project; deleted Sandbox's own copies of both files; updated `using`s in `SandboxGame.cs`/`Program.cs` — zero functional change |
| SL-014 | test | S | **done** | SL-013 | Full `dotnet test` green (848). **All 8** Sandbox scene captures re-verified byte-identical to their pinned MD5s (`model`/`grid`/`drop`/`metalrough`/`planet`/`planet-drop`/`planet-challenge`/`drive`), 0 leak / 0 validation on every one — the pure move regression gate holds |

## Wave 5 — `samples/TopDown` app + content

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| SL-015 | code | M | **done** | SL-011, SL-014 | `TopDown.csproj` (mirrors `Sandbox.csproj`'s ProjectReferences + `Agapanthe.Platform.App`, `PublishAot=true`, imports `Agapanthe.Cook.targets`, duplicates shader-precompile/font-cook targets) + `Program.cs` + `TopDownGame.cs` (`Scenes => [SceneRecipe("topdown")]`, `SceneSystems => [DriveControlSystemFactory]`). **Found + fixed a real generality bug** while smoke-testing: `SceneRecipe.cs` (shared `Agapanthe.App` code, used by every app) had `"Sandbox: "` hardcoded into 2 `Log.Info` lines — factually wrong once a 2nd app used the same shared class. Renamed to the established generic-log convention `"AppHost: "` (matches `AppHost.cs`'s own lines) |
| SL-016 | config | M | **done** | SL-011 | `content/scenes/topdown.toml` + `content/procedural/topdown-prop-{blue,green}.toml` (2 new small colored `uv_sphere` instances — reuses the existing generator, no new one per D7): `procedural/ground` floor + steerable `procedural/probe` puck (`body=true`) + 2 static props; `[camera] mode="fixed" projection="orthographic" ortho_width=40 ortho_height=22.5` overhead (`pitch=-π/2`, height corrected to match the 1280×720 16:9 viewport — an orthographic camera has no automatic aspect-correction the way perspective's `FovY`+`AspectRatio` gives for free); `[[system]] kind="drive_control"`; zero-gravity `[physics]` (flat diorama, no attractor). **Self-review (SL-019) caught a real D3 gap**: the puck entity had no explicit `casts_shadow = false`, defaulting to `true` (`SceneAuthoring.CastsShadow` default) — contradicting the spec's "ships with `casts_shadow = false` on every entity". Fixed; re-capture confirmed byte-identical MD5 (the flag made no visible pixel difference in this framing, but the TOML metadata now correctly satisfies D3) |

## Wave 6 — verify + mandatory tail

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| SL-017 | test | S | **done** | SL-015, SL-016 | Full `dotnet test` green (848 in Debug, the project's normal config — 847 pass + 1 known pre-existing flaky `CopySyncStateTests.PlanFrame_IsZeroAlloc_InSteadyState`, confirmed pass in isolated re-run; the 2 Release-mode "failures" are `[Conditional("DEBUG")]` owner-thread guards correctly no-op'ing in Release, not a regression). `topdown` capture pinned **`31ea748dfbe34192ed80512ea54288fa`** (log lines now read `AppHost:` correctly, 4 entities, 0 leak). First capture attempt (`1e09fa5c…`) revealed a content-authoring bug, not an engine bug: `ortho_width=40`/`ortho_height=30` (aspect 1.33) mismatched the 1280×720 viewport (aspect 1.78) — an orthographic camera has no automatic aspect-correction the way perspective's `FovY`+`AspectRatio` gives for free, so width/height must be authored to already match the viewport. Fixed `ortho_height` to `22.5` (`40/(16/9)`) in `content/scenes/topdown.toml`, documented inline; re-cooked + re-captured → pinned above. All 8 Sandbox captures re-verified byte-identical (`model` `9a010fc3…` / `grid` `c314e6a1…` / `drop` `2fbf87fa…` / `metalrough` `c4cf4605…` / `planet` `99e2f4a3…` / `planet-drop` `81ddf074…` / `planet-challenge` `ea6ba910…` / `drive` `9030f6a6…`), 0 leak / 0 validation on every one. **Human visual verdict: PASS** (2026-09-12) — 3 spheres of identical radius render as true circles of identical apparent size regardless of x/z depth (D2's real orthographic projection confirmed), floor tiling uniform depth-to-depth, overhead framing correct |
| SL-018 | infra | S | **done** | SL-017 | `HeadlessSim --scene topdown` → exit 1 (D9, names `DriveControl`) confirmed on **both** JIT and AOT (`vswhere.exe`/VC toolchain needed adding to `PATH` for `dotnet publish -r win-x64` to succeed in this shell — environment quirk, not a code issue). AOT-published all 3 binaries (Sandbox 4.3 MB / TopDown 4.3 MB / HeadlessSim 2.0 MB, all in the expected NativeAOT size range). **JIT == NativeAOT confirmed on every artifact**: `topdown` capture (`31ea748d…`), `HeadlessSim` default snapshot (`6a13dd54…`) + `--drive` (`f6053226…`), all 8 Sandbox scene captures (`model`/`grid`/`drop`/`metalrough`/`planet`/`planet-drop`/`planet-challenge`/`drive`) — every hash byte-identical to its JIT counterpart. 0 leak / 0 validation on every AOT run. **Re-verified after the SL-020 audit fixes** (which touched `Camera.cs`/`SceneCompiler.cs`/`AgSceneFormat.cs` — runtime code, not just docs/tests): re-published all 3 binaries, `topdown` capture still `31ea748d…` on AOT, D9 guard still exit 1 on AOT, all 8 Sandbox captures still byte-identical on AOT |
| SL-019 | test | — | **done** | SL-018 | Self code review + Requirements validation vs D1-D7 — **1 real gap found and fixed** (D3 shadow flag on the puck, see SL-016). Validated: **D1** `samples/TopDown` is a standalone `Exe` with its own `TopDownGame : IGame`, not a Sandbox scene ✓ · **D2** `Camera.ProjectionMatrix` genuinely branches on `Projection` (`OrthographicVulkanReversed`, not a narrow-FOV fake), confirmed visually (identical-radius spheres render as identical-size circles regardless of x/z depth) ✓ · **D3** all 4 `topdown.toml` entities now carry `casts_shadow = false`, no `ShadowFit` orthographic fix attempted ✓ · **D4** no camera-follow system built — `TopDownGame.SceneSystems` is exactly `[DriveControlSystemFactory]`, the same class `drive` uses ✓ · **D5** `Agapanthe.Platform.App` holds both `EngineWindowAdapter`/`DriveControlSystemFactory`; Sandbox's own copies deleted, both apps reference the shared project (grep-verified 0 duplicate copies) ✓ · **D6** `TopDown.csproj` duplicates `PrecompileShaders`/`CookFonts`/`StripShadercFromRelease` from `Sandbox.csproj` (no new shared `.targets` beyond the pre-existing `Agapanthe.Cook.targets` import) ✓ · **D7** content in the shared `content/` root; only 2 new *instances* of the existing `uv_sphere` generator authored (`topdown-prop-{blue,green}.toml`), no new generator ✓ |
| SL-020 | infra | — | **done** | SL-019 | Double audit `csharp-lowlevel` (**4.3/5**) + `engine-architect` (**4.2/5**), both PASS-with-concerns, **no 🔴**. Both independently verified the reversed-Z ortho derivation symbolically correct (`M33=-M33; M43=1-M43`, distinct from perspective's `M44=0` case) and the `Agapanthe.Platform.App` move byte-identical (diff against `HEAD` shows only namespace/visibility changes, zero method-body differences). **4 findings duplicated by both — applied**: (1) `projection`/`ortho_width`/`ortho_height` silently dropped on a `frame-bounds` camera — `SceneCompiler.ToCamera`'s `frame-bounds` arm now rejects any non-default value via new `BuildFrameBoundsCamera`; (2) `Agapanthe.Platform.App` had no `EngineIsHeadlessTests` allowlist entry — added, matching the pattern every other structural project got at its own milestone; (3) 2 stale comments (`IWindow.cs:10`, `EngineIsHeadlessTests.cs:59`) still named the deleted `samples/Sandbox/EngineWindowAdapter` — fixed to name `Agapanthe.Platform.App.EngineWindowAdapter`; (4) orthographic camera has no aspect-ratio auto-correction (perspective gets it for free via `FovY`+`AspectRatio`) — `Camera.OrthoHeight=0` is now a "derive from `OrthoWidth`/`AspectRatio`" sentinel (same convention as `MoveSpeed`/`ShadowDistance`), `topdown.toml`'s hand-computed `22.5` replaced by the sentinel `0.0` (re-capture confirmed byte-identical MD5 — the derivation reproduces the same value exactly). **1 additional csharp-lowlevel-only finding applied**: `OrthoWidth`/`OrthoHeight` were validated at cook time only, not at `.agscene` read time nor in `Camera` itself — a forged/corrupted blob or a bare `new Camera { Projection = Orthographic }` could reach `CreateOrthographic(0,…)` and upload a NaN/Inf matrix with 0 validation-layer message; added symmetric guards in `AgSceneFormat`'s reader and `Camera.ResolveOrthoHeight()`. 854 tests green (no flaky failure this run), `topdown` capture re-verified byte-identical (`31ea748d…`), all 8 Sandbox captures re-verified byte-identical, `--scene topdown` → exit 1 (D9) re-confirmed, 0 leak/0 validation. **Deferred to backlog** (both audits agree these are real but out of this jalon's scope): D3's only safety net is a TOML convention with no code-level enforcement (engine-architect F2) · `DriveControl` scenes are now 2-for-2 unloadable in HeadlessSim — a design-limit signal for netcode, not a bug (engine-architect F5) · HDR exposure hardcoded for the Sandbox's studio HDRI, no `.agscene` field (engine-architect F6) · `skybox.vert`/`FreeCameraController` orthographic correctness (both, minor) · build cost triples per app via 3 independent `obj/assetcache` cook caches (both, minor) · `Agapanthe.Platform.App` naming collision risk with bare `App.*` (csharp-lowlevel F9, minor) · `far == near` still unvalidated at cook (csharp-lowlevel F7, pre-existing, minor) · `Frustum.Normalize`'s `1e-8` guard degrades earlier under planetary-scale orthographic views (csharp-lowlevel F5 — requalifies the pre-existing `CLAUDE.md` debt item, doesn't add a new one) |
| SL-021 | docs | — | **done** | SL-020 | Converge: `CLAUDE.md` (top summary + prose milestone entry + dette persistante), `docs/AVANCEMENT.md` (§Reprise entry + pointer to next), `docs/BACKLOG.md` (§4quater item marked LIVRÉ + 2 debt items closed/reframed), spec §9 outcome section added, board archived |

## DAG (dependency depth)

```
W1: SL-001 ── SL-002 ── SL-003 ── SL-004
W2: SL-005 ─┬─ SL-006 ── SL-007
            └─ SL-008 ── SL-009 (∥ with SL-006/007 — disjoint files)
    SL-010 (∥ — trivial, no dependents block on it)
W3: (needs SL-003,SL-006,SL-008) SL-011
W4: SL-012 ── SL-013 ── SL-014                    (independent of W1-W3 — could run concurrently,
                                                    kept sequential here for gate simplicity)
W5: (needs SL-011,SL-014) SL-015 ─┬
    (needs SL-011)        SL-016 ─┘ (∥ — disjoint files: csproj/Program.cs vs content/*.toml)
W6: SL-017 ── SL-018 ── SL-019 ── SL-020 ── SL-021
```

**Parallel-safe**: W2 SL-006/007 (format) ∥ SL-008/009 (TOML authoring) — disjoint files, both depend only
on SL-005. W5 SL-015 (app) ∥ SL-016 (content) — disjoint files, both depend only on SL-011. W4 is fully
independent of W1-W3 (zero file overlap) — sequenced here for gate simplicity, not because of a real
dependency; could be pulled earlier if useful.

## Requirements validation (to fill at SL-019)

- D1 separate app, not a Sandbox scene — verify `samples/TopDown` is a standalone `Exe` with its own `IGame`.
- D2 real orthographic projection — verify `Camera.ProjectionMatrix` genuinely branches, not a perspective fake.
- D3 shadows deferred — verify `topdown.toml` has `casts_shadow = false` everywhere, no `ShadowFit` fix attempted.
- D4 fixed camera, `DriveControl` reused — verify no new camera-follow system was built.
- D5 `Agapanthe.Platform.App` shared — verify both apps reference the same classes, zero duplication.
- D6 MSBuild duplicated, not further extracted — verify no new shared `.targets` files beyond what's planned.
- D7 content in shared root, no new generator beyond `uv_sphere`/`ground_quad` unless justified.

## Deferred Work (carried from spec §8)

CSM/`ShadowFit` orthographic correctness · PBR specular view-vector orthographic correctness · extracting
shader/font cook MSBuild into shared `.targets` · camera-follow behavior · a new procedural generator beyond
`uv_sphere`/`ground_quad` unless Wave 5 authoring genuinely needs one.
