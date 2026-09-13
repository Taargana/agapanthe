# Physics queries — raycast + layer mask

## 1. Summary

Backlog §4quater's next item after Texte & UI (closed session 39). Motivating example stated
in the backlog: "there is no way to ask what's under the cursor" — no general spatial query API
exists outside physics stepping itself. `GameWorld.QuerySurfaceContacts` is the only existing
spatial query, and it is a bespoke aggregation built for exactly one gameplay rule (the landing
challenge), not a reusable primitive.

This design adds a raycast query — `GameWorld.TryRaycast`/`RaycastAll` — against every drawable
(not just physics bodies), with an optional layer mask, plus a `Camera.ScreenPointToRay` helper
so the motivating "what's under the cursor" scenario is demonstrable end-to-end in the Sandbox.
Shape-overlap queries ("what's within this sphere/box") are explicitly out of scope — a separate
future item, not invented here just because the backlog title also says "shapes".

## 2. Decision log (interview)

| # | Decision |
|---|---|
| D1 | Scope = **raycast only**. Shape-overlap queries are deferred to a later backlog item. |
| D2 | The raycast hits **every drawable**, via the existing `Bounds { Center, Radius }` component every drawable already carries (not just `RigidBody` physics bodies) — a static, non-physics decoration must still be clickable. `Bounds` is a sphere over-approximation used today for frustum culling ("over-covers slightly, never under-covers"); the raycast inherits that same imprecision (a ray can register a hit slightly outside the actual rendered mesh) — documented, not fixed here. |
| D3 | Layers are in scope now: a new optional component `QueryLayer { uint Mask }` (absent ⇒ treated as `AllLayers = uint.MaxValue`, so an untagged entity is hit by every query by default). The raycast takes a `layerMask` parameter (default `AllLayers`); a candidate is included when `(candidateMask & layerMask) != 0` (Unity/Godot convention). The query-side resolution is authored exactly like the existing optional tag `NoShadowCast` (a single query + an inline per-entity `Has<QueryLayer>()` check — see §3.2; a genuinely-precedented `WithNone<T>()` two-query split does **not** exist anywhere in this codebase, corrected during spec review). The authoring side is new, not a mirror of an existing field: `NoShadowCast` is set via a `bool castsShadow = true` *parameter* on `GameWorld.SpawnImported` — a parameter `SpawnDeferred` does not even have, so today's mechanism can't tag a deferred/runtime spawn at all. `Layer` instead becomes a nullable field directly on `ImportedEntitySpec` (reachable from both `SpawnImported` and `SpawnDeferred` alike, a strictly broader mechanism than the one it's inspired by, not a copy of it), plus an optional `[[entity]] layer = N` authored TOML key. `QueryLayer` also needs its 3 `WorldSerialization.cs` switch-statement cases (§3.2) — every `[Component]` struct is a save-format entry, not just a `ComponentRegistry` registration. |
| D4 | Broadphase: a **new** uniform grid indexed over every drawable (not the existing physics grid, which only indexes `RigidBody` and is private to `ResolveBodyContacts`), **rebuilt fresh on every raycast call** — same pattern as the existing physics grid (reused/never-reallocated scratch arrays, 0-alloc after warmup), not a persisted/incrementally-dirty-tracked structure. A shared private build routine is reusable, unmodified, by a future shape-query milestone. |
| D5 | Two entry points: `TryRaycast(...)` — nearest hit, `bool` + `out RaycastHit` — and `RaycastAll(...)` — every hit sorted by distance into a **caller-provided** `Span<RaycastHit>` buffer (0-alloc, matches `FrameSeries.CopyChronological`'s established caller-owned-buffer convention). |
| D6 | A screen→world helper ships alongside the World-side raycast: `Camera.ScreenPointToRay(Vector2 screenPoint, uint viewportWidth, uint viewportHeight) -> Ray` (camera-relative unprojection, then `+ RenderView.Origin` back to a true `Double3` world position). `GameWorld`'s raycast itself stays headless-safe — it takes a plain `Ray`, no `Camera`/window dependency. `viewportWidth`/`viewportHeight` are used ONLY for the pixel→NDC mapping — the projection matrix itself still comes from `Camera.ProjectionMatrix` (built from the camera's own stored `AspectRatio`), so the caller contract is: keep `Camera.AspectRatio` in sync with the actual viewport (already required today, e.g. `AppHost`'s resize hook), and pass the SAME width/height that produced that aspect ratio. A mismatched pair silently produces a wrong ray, not a thrown error — documented in §4, not solved by re-deriving the projection from the passed-in dimensions (that would let two different aspect ratios disagree about what the camera is actually rendering). |

## 3. Architecture

### 3.1 `Agapanthe.Core` — the shared, GPU-free primitive

```csharp
namespace Agapanthe.Core;

/// A half-line in world space: an origin (double precision, matching WorldPosition) and a unit
/// direction (float precision — a direction has no large-magnitude precision problem the way a
/// position does). Callers must supply an already-normalized Direction; this type does not
/// silently normalize on construction (matches the project's explicit-input convention — no
/// hidden work on a hot-path math type).
public readonly record struct Ray(Double3 Origin, Vector3 Direction);
```

`RaySphereIntersect` (static, pure, GPU-free — mirrors the `MathHelpers`/`ShadowFit`/
`GpuTimestampMath` precedent of extracting the pure math a feature depends on):

```csharp
public static bool TryIntersectSphere(
    in Ray ray, Double3 sphereCenter, float sphereRadius, double maxDistance, out double distance);
```

Returns the nearest intersection `t >= 0` within `[0, maxDistance]`, or `false` (leaving
`distance = 0`) for a miss, a sphere entirely behind the ray origin, or a tangent/grazing case
resolved to "no distinct entry point" (a single-root quadratic, treated as a hit at that root —
documented, not a special case requiring extra branches). Standard ray-sphere quadratic, solved
in `double` throughout (the origin is already `double`; casting to `float` early would reintroduce
the precision problem `Double3`/camera-relative-origin exists to avoid).

### 3.2 `Agapanthe.World` — the query

New component (registered in `ComponentRegistry`, like every other `[Component]` struct — and,
like every other component, a new entry in `WorldSerialization.cs`'s 3 switch statements,
`EntityHasComponent`/`WriteComponent`/`ReadAndAddComponent`, plus the registry-order guard test
those switches are checked against; a tagged entity's `Save()` throws `WorldSerializationException`
until this is done):

```csharp
[Component]
[StructLayout(LayoutKind.Sequential)]
internal struct QueryLayer
{
    public uint Mask;
}
```

Query-side resolution is authored exactly like the existing optional tag `NoShadowCast`
(`entity.Add(new NoShadowCast{...})` only when `!castsShadow`, `GameWorld.cs:237-242`). The
authoring mechanism itself is new, not a mirror of `NoShadowCast`'s: `castsShadow` is a `bool`
*parameter* on `GameWorld.SpawnImported` (default `true`), a parameter `SpawnDeferred` does not
even have — today's mechanism has no way to tag a deferred/runtime spawn at all. `Layer` instead
becomes a field directly on `ImportedEntitySpec` itself — `uint? Layer` (`null` ⇒ no `QueryLayer`
component ⇒ `AllLayers` at query time) — reachable from both `SpawnImported` and `SpawnDeferred`
alike; materialization adds the component only when `spec.Layer is { } mask`. `SceneAuthoring`/
`SceneTomlReader`/`SceneCompiler` gain a mirrored optional `layer` key on `[[entity]]` (cook-time
validated like every other authored numeric field), so a real scene can tag an entity, and a unit
test can construct a tagged fixture directly via `ImportedEntitySpec` (the existing test
convention across this suite — no TOML round-trip needed to exercise `GameWorld`-level behavior).

`ImportedEntitySpec` is a plain `readonly struct` with a positional constructor (`Mesh, Material,
Position, RotationScale, BoundsCenter, BoundsRadius, Order, Identity`), not a `record` with
`init` properties — there is no `with`-expression syntax available. `Layer` is added as one more
optional constructor parameter (after the existing trailing-optional `identity`, so every
existing call site keeps compiling unchanged: `..., MeshRefKey identity = default, uint? layer =
null`).

New public result type (plain, no Arch type, GPU-free — matches `LandingCounts`'s shape):

```csharp
public readonly record struct RaycastHit(EntityRef Entity, double Distance, Double3 Point);
```

`GameWorld.Queries.cs` (new partial, sibling to `.Physics.cs`, same file-per-concern convention):

```csharp
public const uint AllLayers = uint.MaxValue;

public bool TryRaycast(in Ray ray, double maxDistance, out RaycastHit hit, uint layerMask = AllLayers);
public int RaycastAll(in Ray ray, double maxDistance, Span<RaycastHit> results, uint layerMask = AllLayers);
```

Both: `ObjectDisposedException.ThrowIf(_disposed, this)` + `AssertOwnerThread()` (the standing
gate on every other `GameWorld` mutator/query — `SetBodyVelocity`, `QuerySurfaceContacts`,
`StepPhysics`). `maxDistance` is a **required** parameter (no "infinite ray" sentinel) — a finite
broadphase grid needs a bounded region to size its cells against, and the project favors explicit
inputs over hidden magic defaults (`ShadowDistance`, `MoveSpeed` are the sentinel-style exception
that prove the rule: those are *scene-authored*, cook-time-validated fields, not a runtime API
default).

Implementation shape:
1. **One** Arch query over every drawable — `WithAll<GlobalId, WorldPosition, Bounds>()` — with
   an inline per-entity `entities[i].Has<QueryLayer>()` check to resolve that entity's effective
   mask (`Has` ⇒ `Get<QueryLayer>().Mask`, else `AllLayers`). This mirrors the ACTUAL existing
   pattern for an optional per-entity tag in this codebase — `NoShadowCast`, read via
   `entities[i].Has<NoShadowCast>()` at `GameWorld.cs:850` — not a two-query `WithNone<T>()` split
   (no such split exists anywhere in this codebase today; an earlier draft of this spec cited one
   that turned out not to exist, corrected during spec review).
2. Gather matching entities (that also pass the `layerMask` test) into reused scratch arrays
   (`Double3[] position`, `float[] radius`, `EntityRef[] entity` — same "grown on demand, never
   re-allocated" convention as `GameWorld.Physics.cs`'s `_pEntity`/`_pPos`/etc.).
3. Build a uniform grid over that scratch set, cell size derived from the max radius present
   (same reasoning as `ResolveBodyContacts`'s `cellSize = 2 * maxRadius`) — a **new**, separate
   `Dictionary<long, int>` + linked-list-in-array scratch (not `GameWorld.Physics.cs`'s existing
   `_cellHead`/`_cellNext`, which is mid-use during `StepPhysics` and indexes a different set of
   entities entirely; reusing it would alias two unrelated concerns onto one buffer).
4. Walk the ray through the grid cells it crosses (DDA / 3D grid traversal, bounded by
   `maxDistance`), testing each candidate once via `RaySphereIntersect.TryIntersectSphere`.
5. `TryRaycast`: track only the minimum distance seen; `RaycastAll`: collect every hit into the
   caller's span (capped at `results.Length` — excess hits are simply not written, matching the
   project's truncate-don't-grow convention for caller buffers, e.g. `TextLayout`'s 256-glyph
   cap), then sort the written prefix via `results[..count].Sort(CompareByDistance)` where
   `CompareByDistance` is a `private static` `Comparison<RaycastHit>` method — a method group, not
   a lambda, so no per-call closure allocation (the same "allocated once, cached static delegate"
   shape as `QueryPool`'s/`GraphicsPipeline`'s `DestroyDelegate`).

**Scale note** (§7): this rebuild-per-call design is sized for occasional calls (a mouse click),
not continuous per-frame use (e.g. a hover highlight running every tick) — scenes in this
engine reach 10k+ instances, and a persisted broadphase is exactly what that usage pattern would
need instead (deferred, D4).

### 3.3 `Agapanthe.Rendering` — the screen→world helper

```csharp
public Ray ScreenPointToRay(Vector2 screenPoint, uint viewportWidth, uint viewportHeight);
```

On `Camera`. Builds the standard unprojection: screen pixel → NDC → clip → view space (via the
inverse of `ProjectionMatrix`) → world space (via the inverse of `View`), evaluated at both the
near and far plane to get a direction, exactly like `RenderView.CreateView()`'s existing
camera-relative-origin convention — the unprojection itself happens in camera-relative float
space (cheap, no precision loss at that scale), and only the final `Ray.Origin` is widened back
to true double precision by adding `RenderView.Origin`. This mirrors the "narrow, then widen"
pattern `RenderView` already establishes for every other camera-relative computation in the
engine (never invert that order — computing in raw world-space doubles and only then narrowing
would reintroduce the exact precision problem camera-relative-origin exists to solve).

Sandbox wiring (demo only, no new engine surface, no new gameplay system): on a mouse click,
build a `Ray` via `camera.ScreenPointToRay(...)` and call `world.TryRaycast(...)`, logging the
hit entity (or drawing a debug marker via the existing `DebugOverlaySystem`/`UiDrawList`
machinery — implementation detail decided during EXECUTE, not a new design decision).

## 4. Error handling

- **Disposed world / wrong thread**: same standing gates as every other `GameWorld` method —
  `ObjectDisposedException`/the owner-thread assert, not a new error path.
- **`maxDistance <= 0` or non-finite**: rejected at the API boundary (`ArgumentOutOfRangeException`/
  `ArgumentException`) rather than silently producing an empty grid or a NaN-poisoned traversal —
  matches the project's "reject a degenerate input loudly, at the boundary" convention (e.g.
  `SceneCompiler`'s numeric field validation).
- **`Ray.Direction` not unit-length**: NOT validated at the API boundary (a `Debug.Assert` only,
  `[Conditional("DEBUG")]`, matching the existing owner-thread-assert pattern) — validating a
  `Vector3.Length` on every call would cost a hot-path `sqrt` for a caller error that is easy to
  get right once (`Vector3.Normalize` before calling) and easy to unit-test for. A non-unit
  direction does not crash — it silently scales the reported `Distance`/`Point`, a correctness
  bug for the caller to notice, not a memory-safety one.
- **`RaycastAll` with a too-small buffer**: never throws — truncates like every other
  caller-buffer API in this codebase, and the truncation is silent by design (the caller chose
  the buffer size; a return-count already tells them how many were written).
- **`viewportWidth`/`viewportHeight` inconsistent with `Camera.AspectRatio`** (D6): NOT validated
  — a mismatch silently produces a wrong ray, not a thrown error. This is a caller-contract
  documentation issue (keep the viewport dimensions passed to `ScreenPointToRay` consistent with
  whatever last set `Camera.AspectRatio`), not a new runtime check, since the camera's aspect
  ratio is already trusted uniformly everywhere else in the renderer.

## 5. Testing strategy

- **GPU-free, unit-tested**: `RaySphereIntersect.TryIntersectSphere` — direct hit, miss, sphere
  entirely behind the ray origin, ray originating inside the sphere, tangent/grazing case,
  `maxDistance` clipping a would-be hit. Mirrors the `GpuTimestampMath`/`ShadowFit` precedent.
- **`GameWorld` raycast tests** (new `GameWorldRaycastTests.cs`, precedent:
  `SurfaceContactsTests.cs`'s 0-alloc-after-warmup pattern): nearest-hit correctness against a
  handful of spheres at known positions (including two along the same ray, verifying `TryRaycast`
  picks the nearer one); `RaycastAll` returns every hit sorted by distance and truncates
  correctly into an undersized buffer; layer-mask filtering excludes/includes tagged vs. untagged
  entities correctly; a miss (empty world, or a ray pointing away from everything) returns
  `false`/`0`; `maxDistance <= 0` throws; 0-alloc-after-warmup for both entry points.
- **`Camera.ScreenPointToRay` tests**: a ray through the viewport center points along
  `Camera.Forward`; a ray through a viewport corner diverges by roughly the expected FOV-derived
  angle; the produced `Ray.Origin`, once re-narrowed against `RenderView.Origin`, matches the
  camera's own world position within floating-point tolerance.
- **Live verification**: a Sandbox click-to-highlight interaction, screenshotted for a human
  visual sanity check — not a new deterministic capture gate (clicking is inherently
  interactive; no prior interactive-only feature in this project has one either).
- **Regression**: every existing capture (8 Sandbox scenes + `topdown`) re-verified byte-
  identical — this touches `GameWorld`/`Camera`, both shared by every scene, and the raycast is
  a pure query never invoked from the render/tick path itself, so no capture should move.
- **Double audit**: `csharp-lowlevel` + `engine-architect` — the project-standard pairing (ECS/
  World/math work, no new Vulkan surface, so no deviation to `graphics-3d` is warranted here).

## 6. Migration path

Single-phase milestone (comparable in size to UI-3, not to Contenu-3c's gated sub-phases).
Waves, in dependency order: (1) `Ray` + `RaySphereIntersect` (`Agapanthe.Core`) + tests,
(2) `QueryLayer` component (+ its 3 `WorldSerialization.cs` cases + registry-order guard test) +
`ImportedEntitySpec.Layer` + `RaycastHit` + `GameWorld.Queries.cs` (`TryRaycast`/`RaycastAll`) +
tests, (3) `[[entity]] layer` TOML authoring (`SceneAuthoring`/`SceneTomlReader`/`SceneCompiler`)
+ tests, (4) `Camera.ScreenPointToRay` + tests, (5) Sandbox click-to-highlight demo wiring,
(6) verify + mandatory tail + double audit + converge.

## 7. Out of scope (deferred, not silently dropped)

- Shape-overlap queries (sphere/box "what's within this region") — a separate future backlog
  item, not invented here just because the backlog title also names "shapes".
- Mesh-accurate raycasting — today's hit test is against the `Bounds` sphere proxy, the same
  approximation frustum culling already accepts; a precise mesh-level raycast would need actual
  triangle data at query time, a materially larger feature.
- A persisted/incrementally-dirty-tracked broadphase (D4) — the per-call rebuild is deliberately
  the simpler, sufficient-for-now choice; revisit only if a real workload calls the raycast often
  enough per frame for the rebuild cost to matter.
- Any new gameplay system consuming the raycast beyond the Sandbox click-to-highlight demo.
- ~~`TopDown`'s orthographic camera getting its own `ScreenPointToRay` verification — the
  unprojection math is projection-agnostic~~ **Correction (post-audit, csharp-lowlevel +
  engine-architect independently found this): that claim was false of the shipped implementation.**
  The originally-specified approach (inverting `ProjectionMatrix`) would have been
  projection-agnostic; the implementation deviated to a basis-vector construction
  (`Right`/`Up`/`Forward` scaled by FovY-derived NDC offsets) that is NOT equivalent for an
  orthographic camera — it silently produced a perspective-style converging cone instead of
  parallel rays. `Camera.ScreenPointToRay` now branches explicitly on `Projection`:
  `Orthographic` yields a constant `Forward` direction with a per-pixel origin offset (along
  `Right`/`Up`, scaled by half `OrthoWidth`/`OrthoHeight`); `Perspective` is unchanged. Both
  paths are now covered by dedicated tests (`CameraScreenPointToRayTests`).

## 8. Outcome (closed 2026-09-13, session 40)

Shipped as designed, D1-D6 all intact per PQ-013's self-review (verified against the actual code,
not assumed). 15-task/4-wave board, 3 unplanned discoveries along the way, double audit found and
fixed one real 🔴, all corrected before close.

**Unplanned mid-flight discoveries** (none anticipated by this spec, all resolved same-session):
- Adding `QueryLayer` (component #14) tripped an unconditional `Debug.Assert` in
  `WorldSerialization.cs` comparing the live component count against a version-4-hardcoded
  constant — cascaded to 43 unrelated-looking test failures. Fixed as a proper additive
  `WorldSerialization` v4→v5 bump (mirrors this project's established version-bump precedent);
  the 3 `HeadlessSimSnapshotFormatTests` MD5s were re-pinned (byte lengths unchanged — the new
  component is optional/additive, no scene in that test carries it).
- `CookRunner.CookerVersion` wasn't bumped for the `.agscene` v5→v6 format change (the `layer`
  TOML key) — same bug class the Contenu-3c-2 audit had already caught once for a v2→v3 bump.
  Found during PQ-012 verification (a stale incremental asset cache threw `AgSceneException`),
  fixed immediately.
- `IWindow` has no mouse-click event or raw cursor position (only capture-on-click + delta-while-
  captured) — D6's "click to highlight" demo was reinterpreted as a `KeyPressed(Key.F)`
  screen-center crosshair raycast instead, avoiding new `IWindow` surface entirely. Wired once at
  `AppHost` level, so it applies to both `Sandbox` and `TopDown` for free.

**Verification**: 904 tests (890 → 904, +14 net across both audit fix-forks), 0 warnings, 0
regressions. All 9 pinned captures (8 Sandbox scenes + `topdown`) re-verified byte-identical via
git-stash A/B against the `974384a` rollback point — 5 of them landed on exactly the hashes
already pinned in `CLAUDE.md`, confirming the repro parameters were correct, not just
self-consistent. `HeadlessSim`/Sandbox **JIT == NativeAOT** confirmed both before and after the
audit fix pass (`dbe9ed9103f10f4a34708e174753721b`, unchanged). Live human visual verdict
**PASS** (2026-09-13, `grid` scene): `F` on a visible entity → `[raycast] hit entity
EntityRef(94) at distance 9.19/9.21/9.58 m` (stable across near-repeats), `F` toward empty space
→ `[raycast] no hit`.

**Double audit** — `csharp-lowlevel` (3,6/5, PASS-with-concerns) + `engine-architect` (4,1/5,
PASS-with-concerns), dispatched in parallel per this project's standard pairing (no Vulkan
surface here, so no deviation to `graphics-3d` was needed). Strong convergence: 3 findings were
independently hit by both reviewers (the orthographic `ScreenPointToRay` bug, the unbounded DDA
walk cost, and `RaycastAll`'s misleading truncation-semantics doc).

**1 🔴 found and fixed** — `maxDistance = double.PositiveInfinity` passed the existing NaN/`<=0`
guard and made the DDA walk loop forever (`while (t <= maxDistance)` never terminates against
`+Infinity`); a genuinely reachable public-API hazard (a caller writing "cast as far as
possible" is a natural call), not a contrived edge case. Fixed by rejecting `IsInfinity` at the
validation boundary alongside the existing NaN/non-positive checks.

**Findings fixed** (🟠 unless noted): unbounded DDA walk cost — now bounded to the occupied-cell
AABB tracked during grid build, plus `TryRaycast` shrinking its search radius to `bestDistance`
once a hit is found and `RaycastAll` breaking out once its buffer fills · `RaycastHit.Point`
was narrowing the exact `double` hit distance to `float` before multiplying, contradicting the
"solved in double throughout" design premise — now exact `Double3` arithmetic · a degenerate
(NaN/zero-length) `Ray.Direction` could poison `bestDistance` with `NaN` or crash via an
undocumented raw `ArithmeticException` — now rejected with `ArgumentException` at both entry
points (a stronger fix than this spec's originally-planned Debug-only assert, given the audit
found real crash/poisoning consequences) · the equal-distance tie-break was archetype-order-
dependent (non-deterministic which of two co-located candidates wins) — now a strict
first-encountered-wins rule · `RaySphereIntersect`'s quadratic solution catastrophically
cancelled at ≳1e8 m, undermining this project's stated planetary/interstellar-scale ambitions —
rewritten with the numerically-stable closest-point formulation, verified with a new test at
7.48e10 m (this engine's own `AGAPANTHE_SUN` distance) · `Camera.ScreenPointToRay`'s
orthographic-projection gap (above, §7) · `EnsureStampCapacity` reallocated on every single call
once a world grew past its initial entity count (exactly the shape of `planet-drop`/
`planet-challenge`'s probe-spawning) — now grows by doubling like its sibling · the two remaining
`ImportedEntitySpec` field-by-field copy sites (`ProbeDropSystem`/`LandingChallengeSystem`) were
silently dropping `Layer` — fixed (currently a no-op, since no runtime template populates `Layer`
yet, but closes the exact seam D3 was written to avoid) · an authored `layer = 0` was silently
unreachable by every query including `AllLayers` — now rejected at cook time, matching this
project's established convention for degenerate authored values.

**Deferred as accepted 🟡** (documented in `.absolute-work/archive/`, not silently dropped): no
dedicated v4-snapshot-format test fixture (coverage gap only, not a bug) · the component-count
`Debug.Assert` tripwire lives in `Load` rather than only `Save` (arguable, low value to relocate)
· `Camera.Right`'s pre-existing NaN-at-pitch-±90° degeneracy (out of this milestone's scope) · one
test's angle tolerance is looser than ideal (the underlying convention is correct and pinned by a
sibling test) · a per-entity `Has<QueryLayer>()` check that could be hoisted to per-chunk (real
but negligible micro-optimization).
