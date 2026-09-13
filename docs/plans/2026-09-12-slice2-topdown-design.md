# Slice-2 — the 2nd dissimilar slice: top-down orthographic app

## 1. Summary

The domain Contenu is now entirely closed (Contenu-1/2/3a/3b/3c-1/3c-2/3c-3, all committed).
Every scene the engine has ever rendered — VS-1/2/3, the whole Contenu-3c arc — was built
around the same planetary/perspective shape: a free-fly or fixed 3rd-person camera looking at
a sphere from a modest distance. The backlog (§4quater) names this explicitly as the cheapest
generality test that exists: build one genuinely dissimilar slice and see what breaks. "The
engine is what's common between the two."

This milestone is that slice: a second sample app, `samples/TopDown`, with a true orthographic
top-down camera over a small flat diorama, reusing as much of the existing declarative-scene/
`SceneRecipe`/`ISceneSystemFactory` machinery as possible. Two pieces of debt the backlog ties
explicitly to "the moment app #2 arrives" get paid here: `EngineWindowAdapter` (pure `IWindow`↔
`EngineWindow` forwarding, currently `internal` to Sandbox) and `DriveControlSystemFactory`
(built in Contenu-3c-3, also Sandbox-`internal`, also zero Sandbox-specific) both move into a
new shared `Agapanthe.Platform.App` project both apps reference.

## 2. Decision Log

| # | Decision |
|---|---|
| D1 | Scope = a genuinely separate 2nd sample app (`samples/TopDown`), not a new scene bolted onto the existing Sandbox — proves `AppHost`/`IGame`/`SceneRecipe` are reusable outside Sandbox, which a same-app scene cannot test. |
| D2 | `Camera` gains a **real** `Orthographic` projection mode (`CameraProjection` enum: `Perspective`/`Orthographic`), not a narrow-FOV-viewed-from-far fake — the actual generality test the backlog names. |
| D3 | CSM shadows out of scope. `ShadowFit.FitSliceSphere` computes frustum-slice corners via `tan(FovY/2)` — correct for a perspective cone, wrong for an orthographic box. The topdown scene ships `casts_shadow = false` everywhere; the orthographic CSM case is documented, deferred debt. |
| D4 | Camera behavior: fixed overhead orthographic camera over a static diorama, no camera-follow. Reuses `SceneSystemKind.DriveControl`/`DriveControlSystemFactory` verbatim (constructed a second time, in the second app) instead of building a new "camera follows the steered body" system. |
| D5 | `DriveControlSystemFactory` and `EngineWindowAdapter` move from Sandbox-`internal` into a new shared `src/Agapanthe.Platform.App` project (references `Agapanthe.Platform` + `Agapanthe.App`), both become `public`. `Sandbox` and `TopDown` reference the same classes — zero duplication. |
| D6 | MSBuild: `TopDown.csproj` imports the already-shared `build/Agapanthe.Cook.targets` (assets). Shader precompile / font cook targets are **duplicated** into `TopDown.csproj` from `Sandbox.csproj`, not further extracted into shared `.targets` — that extraction is its own MSBuild-infrastructure task with its own risk, out of scope here. |
| D7 | Content lives in the existing shared `content/` root. New: `content/scenes/topdown.toml`; reuses the existing `uv_sphere` (puck + static props) and `ground_quad` (floor) generators — no new generator needed. |

## 3. Architecture

### 3.1 `Camera` / `MathHelpers` — the real generality test

`src/Agapanthe.Rendering/Camera.cs` gains a new `public enum CameraProjection { Perspective,
Orthographic }`. `Camera` gains `Projection` (default `Perspective` — byte-identical default
behavior for every existing scene) and `OrthoWidth`/`OrthoHeight` (world units, meaningless
when `Projection == Perspective`). `ProjectionMatrix` switches:

```csharp
public Matrix4x4 ProjectionMatrix => Projection switch
{
    CameraProjection.Perspective => MathHelpers.PerspectiveVulkanReversed(FovY, AspectRatio, Near, Far),
    CameraProjection.Orthographic => MathHelpers.OrthographicVulkanReversed(OrthoWidth, OrthoHeight, Near, Far),
    _ => throw new InvalidOperationException($"unknown projection {Projection}"),
};
```

`src/Agapanthe.Core/MathHelpers.cs` gains `OrthographicVulkanReversed(width, height, near,
far)` — mirrors `PerspectiveVulkanReversed`'s derivation (the clip-space transform `z → w − z`
applied to the existing `OrthographicVulkan`), matching the engine's pipeline-wide reversed-Z
convention (`ClearDepth = 0f` + `GreaterOrEqual` compare, hardcoded in `Renderer.cs`). A
*standard*-depth orthographic matrix under that fixed compare op would silently invert
near/far depth ordering — a real correctness bug, not one a validation layer would ever
surface. **The exact matrix-element formula is proved by test, not hand-derived here** —
`OrthographicReversedZProjectionTests.cs` mirrors `ReversedZProjectionTests.cs`'s three cases
(near→1, far→0, monotonic, XY preserved) using the existing `MathHelpers.ProjectPoint` helper;
TDD writes the test first, then the implementation until green.

**Frustum culling needs no change.** `Frustum.FromViewProjection` (Gribb-Hartmann plane
extraction from any view·projection matrix) is already projection-agnostic — it already builds
the shadow LIGHT frustum from an orthographic matrix today.

**`RenderView`/`CameraUniforms` need no change.** `RenderView.FovY` becomes a meaningless but
harmless passenger under orthographic: `SceneCameraApplier.cs:27-29` treats `scene.FovY <= 0f`
as a "don't override" sentinel (the same convention `MoveSpeed`/`ShadowDistance` already use),
so `Camera.FovY` keeps whatever value it already had — its own default (60°) if the TOML omits
`fov_y`, or whatever `fov_y` the scene authors regardless of projection. **Correction from spec
review**: an earlier draft of this doc claimed `FovY` would be forced to `0f` for an
orthographic camera; that code path does not exist, and forcing it would require touching
`SceneCameraApplier` for no benefit. The corrected picture: `ShadowFit.FitSliceSphere` (out of
scope, D3) runs every frame regardless of projection, consuming whatever real `FovY` the
topdown scene's `[camera]` block authors (a harmless, ordinary — not degenerate — shadow-fit
sphere, computed but never used to draw anything since `casts_shadow = false` everywhere).
Verified live (0 validation/0 leak) rather than assumed either way.

**Documented, deferred nuance — not fixed here**: the PBR shader's specular view vector is
`V = normalize(eyePos − worldPos)`, correct for perspective, technically wrong for true
orthographic (rays are parallel; `V` should be a constant `-forward`). Specular highlights are
subtly mispositioned under orthographic; geometry, depth, and culling are all correct. Called
out explicitly, not silently accepted.

### 3.2 `.agscene` v4 → v5 — `SceneCamera` gains a projection

`src/Agapanthe.Assets/Scene/SceneDefinition.cs`: `SceneCamera` gains its own `CameraProjection`
(GPU-free assembly, own copy mirroring `Agapanthe.Rendering.CameraProjection` — same pattern
every other closed scene enum follows) plus `OrthoWidth`/`OrthoHeight` (`Fixed`-only, like
`MoveSpeed`/`ShadowDistance` — always serialized for the `Fixed` variant, `0` when unused).

`src/Agapanthe.Assets/Scene/AgSceneFormat.cs`: `Version = 5` (v1–v4 all rejected with dedicated
re-cook messages, following the established per-version-message pattern). `[fixed]` camera
variant gains `projection:u8 | orthoWidth:f32 | orthoHeight:f32` after the existing
`moveSpeed`/`shadowDistance` fields.

`src/Agapanthe.Assets.Pipeline/Scene/{SceneAuthoring,SceneTomlReader,SceneCompiler}.cs`:
`AuthoredCamera` gains `Projection`/`OrthoWidth`/`OrthoHeight`; `[camera]` TOML reads
`projection = "perspective" | "orthographic"` (default `"perspective"` — byte-identical for
every existing scene) + `ortho_width`/`ortho_height`; `SceneCompiler.ToCamera`'s `fixed` arm
carries the 3 new fields through.

`src/Agapanthe.App/Scene/SceneCameraApplier.cs`: the `Fixed` branch sets `camera.Projection`/
`OrthoWidth`/`OrthoHeight` from the scene, mirroring how `MoveSpeed`/`ShadowDistance` are
conditionally copied today.

`src/Agapanthe.Assets.Pipeline/CookRunner.cs`: `CookerVersion` bumped for the v4→v5 change (the
incremental-recook safety net must track every format bump — a 3c-2 audit finding, applied
consistently).

### 3.3 `Agapanthe.Platform.App` — the shared client-app glue

New project `src/Agapanthe.Platform.App` (`ProjectReference`: `Agapanthe.Platform`,
`Agapanthe.App`). Two files moved verbatim and made `public`:
- `EngineWindowAdapter.cs` — pure `IWindow`↔`EngineWindow` forwarding, no behavior change.
- `DriveControlSystemFactory.cs` — the `ISceneSystemFactory` for `SceneSystemKind.DriveControl`
  (built in Contenu-3c-3). No behavior change; only visibility (`internal`→`public`) and
  namespace move.

`Sandbox.csproj` gains a `ProjectReference` to `Agapanthe.Platform.App`; its own copies of both
files are deleted; `SandboxGame.SceneSystems`/`Program.cs` update `using`s only — zero
functional change. All 8 Sandbox captures must be byte-identical after this move (the
strongest possible regression gate on a pure refactor).

### 3.4 `samples/TopDown` — the new app

Mirrors `samples/Sandbox/Program.cs`'s shape:
```csharp
return AppHost.RunClient(
    new TopDownGame(),
    new EngineWindowAdapter(new EngineWindow("Agapanthe TopDown", 1280, 720)),
    args);
```
`TopDownGame : IGame` — `Title`, `DefaultScene => "topdown"`, `Scenes => [new
SceneRecipe("topdown")]`, `SceneSystems => [new DriveControlSystemFactory()]` (from
`Agapanthe.Platform.App`, same class Sandbox uses). `Universe => UniverseId.None` (dev-host
convention matching `SandboxGame`).

`TopDown.csproj`: `ProjectReference`s mirroring Sandbox's list (App, Engine, Engine.Render,
Rendering, World, Platform, Graphics, Assets, Core, + `Agapanthe.Platform.App`), `PublishAot =
true`, imports `build/Agapanthe.Cook.targets`, duplicates the `PrecompileShaders`/`CookFonts`
MSBuild targets and `StripShadercFromRelease` from `Sandbox.csproj` (D6).

### 3.5 Content — `content/scenes/topdown.toml`

Reuses `content/procedural/ground.toml` (floor) and the existing `uv_sphere` generator (a small
steerable "puck" — either the existing `probe.toml` shape or a new tiny
`content/procedural/puck.toml`, decided at authoring time, not an architectural fork). Shape:
one `[[entity]] body = true` puck (reuses the Contenu-3c-3 entity-body authoring capability),
one ground quad, 2 static non-body entities (small spheres, distinct emissive colors) so the
diorama visibly holds more than one drawable. `[camera] mode = "fixed" projection =
"orthographic"` with baked `ortho_width`/`ortho_height` framing the whole diorama from directly
overhead (`pitch = -π/2`). `[[system]] kind = "drive_control"` targeting the puck's entity
index. `[environment]` reuses `procedural_sky` or `black` (whichever frames better once
rendered — not architectural). No `[physics]` attractor — flat zero-gravity ground, like
`drive`.

### 3.6 `HeadlessSim` — automatic coverage, no new code

`content/scenes/topdown.toml` sits in the same shared `content/` root every `AssetCatalog`
reads from — `HeadlessSim --scene topdown` automatically hits the existing D9 guard
(`DriveControl` in `Systems`, kind-based, not app-based) and refuses with exit 1. No
HeadlessSim code changes. Verified live in the tail wave, same as every prior
`SceneSystemKind` addition.

## 4. Files (critical set)

| File | Nature |
|---|---|
| `src/Agapanthe.Rendering/Camera.cs` | `CameraProjection` enum, `Projection`/`OrthoWidth`/`OrthoHeight`, `ProjectionMatrix` switch |
| `src/Agapanthe.Core/MathHelpers.cs` | `OrthographicVulkanReversed` |
| `tests/Agapanthe.Tests/OrthographicReversedZProjectionTests.cs` (new) | TDD-first proof of the new matrix |
| `src/Agapanthe.Assets/Scene/SceneDefinition.cs`, `AgSceneFormat.cs` | v4→v5, `SceneCamera` projection fields |
| `src/Agapanthe.Assets.Pipeline/Scene/{SceneAuthoring,SceneTomlReader,SceneCompiler}.cs` | TOML surface for the new fields |
| `src/Agapanthe.Assets.Pipeline/CookRunner.cs` | `CookerVersion` bump |
| `src/Agapanthe.App/Scene/SceneCameraApplier.cs` | applies the 3 new fields |
| `src/Agapanthe.Platform.App/` (new project) | `EngineWindowAdapter.cs`, `DriveControlSystemFactory.cs` (moved, made public) |
| `samples/Sandbox/*` | deletes its 2 moved files, references the new project, re-verify 8 captures unchanged |
| `samples/TopDown/` (new) | `TopDown.csproj`, `Program.cs`, `TopDownGame.cs` |
| `content/scenes/topdown.toml`, maybe `content/procedural/puck.toml` | authored data |

## 5. Error handling

- `SceneCompiler`/`SceneTomlReader` reject an unknown `projection` string with a named
  `AssetException` (mirrors every other closed-enum TOML field in this codebase).
- `AgSceneFormat.Read` rejects `CameraProjection` values outside the closed enum the same way
  every other cooked enum is rejected (`AgSceneException` naming the unknown byte).
- `Camera.ProjectionMatrix`'s `switch` has a `_ => throw` arm — an unreachable-in-practice
  invariant (the cooked format only ever produces the two known values), matching this
  codebase's convention of never silently defaulting an exhaustive switch.
- No new failure mode for `HeadlessSim` — the existing D9 guard covers `topdown` automatically.

## 6. Testing strategy

- `OrthographicReversedZProjectionTests.cs`: near→1, far→0, monotonic depth, XY preserved —
  written first (TDD), against the still-unimplemented `OrthographicVulkanReversed`.
- `AgSceneFormatTests.cs`: round-trip a `SceneCamera` with `Projection = Orthographic` +
  `OrthoWidth`/`OrthoHeight`; v4-rejection test (mirrors the existing v1/v2/v3 rejection tests).
- `SceneTomlReaderTests.cs`/`SceneCompilerTests.cs`: parse + compile `projection =
  "orthographic"` with `ortho_width`/`ortho_height`; unknown-projection-string rejection.
- Live capture/AOT verification (not unit-testable): all 8 Sandbox scenes byte-identical after
  the `Agapanthe.Platform.App` move; new `topdown` capture pinned + human visual verdict;
  `HeadlessSim --scene topdown` exit 1 on JIT and AOT; JIT == AOT on every capture/snapshot
  across all three binaries (Sandbox, TopDown, HeadlessSim).

## 7. Migration path

Single-phase milestone (unlike Contenu-3c's 3 gated sub-phases — this is one cohesive slice,
sized similarly to one of those sub-phases). Waves, in dependency order: (1) `Camera`/
`MathHelpers` orthographic support + tests, (2) `.agscene` v5 format + cook-side authoring, (3)
`Agapanthe.Platform.App` extraction (Sandbox re-verify), (4) `samples/TopDown` app + content
authoring, (5) verify + mandatory tail + double audit + converge.

## 8. Out of scope (deferred, not silently dropped)

CSM/`ShadowFit` orthographic correctness (D3) · PBR specular view-vector orthographic
correctness (§3.1) · extracting shader/font cook MSBuild into shared `.targets` (D6) ·
camera-follow behavior (D4) · a new procedural generator beyond `uv_sphere`/`ground_quad`
unless authoring genuinely needs one.

## 9. Outcome (2026-09-12, session 38)

**Shipped as designed**, D1-D7 all held, with 2 real generality bugs found live (not by
re-reading code) and fixed, plus 5 findings from the mandatory double audit applied before
closure.

**Delivered**: `samples/TopDown` (standalone `Exe`, own `TopDownGame : IGame`) proves
`AppHost`/`IGame`/`SceneRecipe` reusable outside Sandbox. `Camera` gained a real
`CameraProjection` (`MathHelpers.OrthographicVulkanReversed`, TDD-derived — the first attempt
copied the perspective reversed-Z formula and a test caught it immediately: orthographic's `w`
is a constant `1` vs. perspective's `w = z_view`, so `M33 = -M33; M43 = 1 - M43` replaces
`M33 = -1 - M33; M43 = -M43`). `.agscene` v4→v5 (`SceneCamera.Fixed` gains
`Projection`/`OrthoWidth`/`OrthoHeight`). `EngineWindowAdapter` + `DriveControlSystemFactory`
extracted from Sandbox-`internal` into the new shared `src/Agapanthe.Platform.App` — verified
by the strongest possible regression gate (a pure move: all 8 Sandbox captures stayed
byte-identical). CSM shadows out of scope (D3) — `topdown.toml` ships `casts_shadow = false` on
every entity.

**2 generality bugs found live**: `SceneRecipe.cs` (shared `Agapanthe.App` code) had
`"Sandbox: "` hardcoded into 2 log lines — fixed to `"AppHost: "`. The puck entity in
`topdown.toml` had no explicit `casts_shadow = false` (default `true` — silently violated D3) —
fixed.

**Gates**: 854 tests, 0 warning, `topdown` capture pinned + human visual verdict PASS
(`31ea748dfbe34192ed80512ea54288fa` — three identical-radius spheres render as identical-size
circles regardless of x/z depth, the defining proof of a real orthographic projection), all 8
Sandbox captures re-verified byte-identical, `HeadlessSim --scene topdown` → exit 1 confirmed
(D9, names `DriveControl`), all 3 binaries JIT == NativeAOT on every capture/snapshot
(re-verified after the audit fixes below, since those touched runtime code), 0 leak / 0
validation everywhere.

**Double audit**: `csharp-lowlevel` 4.3/5 + `engine-architect` 4.2/5, both PASS-with-concerns,
no 🔴. Both independently verified the reversed-Z orthographic derivation symbolically and
confirmed the `Agapanthe.Platform.App` move was byte-identical (diff against `HEAD` shows only
namespace/visibility changes). **4 findings duplicated by both, applied**:
- `projection`/`ortho_width`/`ortho_height` were silently dropped on a `frame-bounds` camera
  (the same silently-ignored-key class of bug Contenu-3c-3 closed for `body`/`velocity` leaking
  onto `[[grid]]`/`[[cluster]]`) — `SceneCompiler.BuildFrameBoundsCamera` now rejects any
  non-default value.
- `Agapanthe.Platform.App` had no `EngineIsHeadlessTests` allowlist entry, despite being the one
  project whose entire purpose is being the sole Platform+App meeting point — added, matching
  the pattern every other structural project got at its own milestone.
- 2 stale comments (`IWindow.cs:10`, `EngineIsHeadlessTests.cs:59`) still named the deleted
  `samples/Sandbox/EngineWindowAdapter` — fixed.
- Orthographic has no automatic aspect-ratio correction (perspective gets it for free via
  `FovY`+`AspectRatio`) — `Camera.OrthoHeight = 0` is now a "derive from `OrthoWidth`/
  `AspectRatio`" sentinel (same convention as `MoveSpeed`/`ShadowDistance`); `topdown.toml`'s
  hand-computed `ortho_height = 22.5` replaced by the sentinel `0.0` (re-capture confirmed
  byte-identical — the derivation reproduces the same value exactly).

**1 additional csharp-lowlevel-only finding, applied**: `OrthoWidth`/`OrthoHeight` were
validated at cook time only, not at `.agscene` read time nor in `Camera` itself — a forged or
corrupted blob, or a bare `new Camera { Projection = Orthographic }`, could reach
`CreateOrthographic(0, …)` and upload a NaN/Inf matrix with zero validation-layer message.
Symmetric guards added in `AgSceneFormat`'s reader and `Camera.ResolveOrthoHeight()`.

**Deferred to backlog §4quater** (assumed, not an oversight): D3's only safety net is a TOML
convention with no code-level enforcement (`engine-architect`) · `SceneSystemKind.DriveControl`
is now 2 scenes/2 apps refused by `HeadlessSim` — a design-limit signal for netcode, not a bug: a
steerable body is the canonical use case for an authoritative server, and the blanket
`Systems.Count > 0` refusal does not distinguish *the system* (client-side input sampling) from
*the scene declaration* (sim-side, "entity N is steerable") — `engine-architect` · HDR exposure
hardcoded for the Sandbox's studio HDRI, no `.agscene` field lets a scene author its own ·
`skybox.vert`/`FreeCameraController` orthographic correctness (minor, unexercised today) ·
build cost triples per app (each app cooks all of `content/` into its own `obj/assetcache`) ·
`Frustum.Normalize`'s `1e-8` guard degrades earlier under planetary-scale orthographic views
(requalifies the pre-existing `CLAUDE.md` debt item, doesn't add a new one).

**How to test it**:
```
dotnet build Agapanthe.slnx -c Release
dotnet test
AGAPANTHE_SCENE=topdown AGAPANTHE_CAPTURE=/tmp/topdown.ppm AGAPANTHE_MAX_FRAMES=2 \
  dotnet run --project samples/TopDown -c Release --no-build
# MD5 should be 31ea748dfbe34192ed80512ea54288fa
dotnet run --project samples/TopDown -c Release   # interactive: WASD/Space/C move the puck, X brakes
dotnet run --project samples/HeadlessSim -c Release --no-build -- --scene topdown  # exit 1, names DriveControl
```
