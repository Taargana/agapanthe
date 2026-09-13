# Absolute Work Board — Physics queries: raycast + layer mask

**Status**: `completed` — 2026-09-13 (session 40)
**Spec**: `docs/plans/2026-09-14-physics-queries-raycast-design.md` (APPROVED 4.2/5, scored review
by a separate subagent, 3 iterations — round 1 found a fabricated precedent + 5 real gaps
(missing serialization touchpoint, no authoring path, aspect-ratio ambiguity, unbounded scale
note, sort-allocation ambiguity), all fixed; round 2 found the fix for "no authoring path" itself
cited a nonexistent `ImportedEntitySpec.CastsShadow` field, corrected; round 3 approved)
**Session**: 40
**Rollback point**: commit `974384a` (docs: text-and-ui, closed — last commit before this milestone)

## Scope

Backlog §4quater's next item after Texte & UI (closed S39). A general-purpose raycast —
`GameWorld.TryRaycast`/`RaycastAll` — against every drawable (via the existing `Bounds` sphere
component, not just physics bodies), with an optional layer-mask filter (new `QueryLayer`
component), plus `Camera.ScreenPointToRay` so "what's under the cursor" is demonstrable
end-to-end in the Sandbox. Shape-overlap queries are explicitly deferred — this is raycast only.

## Project conventions (unchanged)

.NET 10 solution, xUnit tests in `tests/Agapanthe.Tests`, `dotnet build`/`dotnet test` via
`Agapanthe.slnx`, TDD expected, PowerShell primary shell (Bash lacks `vswhere.exe` on PATH for
AOT publish — add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH first).
Commits on explicit request only. Double audit for this milestone is the **project-standard**
pairing — `csharp-lowlevel` + `engine-architect` (this is ECS/World/math work, no new Vulkan
surface, so no deviation to `graphics-3d` as UI-3 needed).

## Wave 1 — `Agapanthe.Core` primitive + `Agapanthe.World` component (parallel tracks)

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| PQ-001 | test | S | done | — | `tests/Agapanthe.Tests/RaySphereIntersectTests.cs` (TDD-first, confirm red): direct hit, miss, sphere entirely behind origin, origin inside sphere, tangent/grazing, `maxDistance` clipping a would-be hit |
| PQ-002 | code | S | done | PQ-001 | `src/Agapanthe.Core/Ray.cs` (`readonly record struct Ray(Double3 Origin, Vector3 Direction)`) + `src/Agapanthe.Core/RaySphereIntersect.cs` (`TryIntersectSphere`, solved in `double` throughout, spec §3.1) |
| PQ-003 | test | S | done | — | Extend `tests/Agapanthe.Tests/ComponentRegistryTests.cs`'s order-guard test for the new `QueryLayer` component; new serialization round-trip test (tagged entity `Save`/`Load` round-trips its `QueryLayer.Mask`) — confirm red |
| PQ-004 | code | M | done | PQ-003 | `src/Agapanthe.World/Components.cs`: `QueryLayer { uint Mask }` (`[Component]`, registered in `ComponentRegistry`); `WorldSerialization.cs`: 3 new switch cases (`EntityHasComponent`/`WriteComponent`/`ReadAndAddComponent`); `src/Agapanthe.Core/ImportedEntitySpec.cs`: new trailing-optional ctor param `uint? layer = null` (spec §3.2 — `readonly struct`, positional ctor, no `with`-expression); materialization (`GameWorld.cs`) adds `QueryLayer` only when `spec.Layer is { } mask`, mirroring `NoShadowCast`'s `entity.Add(...)`-only-when-needed shape — **also required an unplanned snapshot format bump (v4→v5)**: adding a 14th component tripped an unconditional `Debug.Assert(V4ComponentCount == ComponentRegistry.All.Count)` in `WorldSerialization.cs`, cascading 43 unrelated-looking test failures; fixed as a proper additive version bump (mirrors the v3→v4 precedent), then re-pinned the 3 `HeadlessSimSnapshotFormatTests` MD5 hashes (byte lengths unchanged — the new component is additive/optional, no entity in those scenes carries it) |

**Parallel-safe**: PQ-001→002 (`Agapanthe.Core`) ∥ PQ-003→004 (`Agapanthe.World`/`Agapanthe.Core`
`ImportedEntitySpec.cs`) — disjoint files.

## Wave 2 — the query itself + the screen→world helper + TOML authoring (parallel tracks)

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| PQ-005 | test | M | done | PQ-002, PQ-004 | `tests/Agapanthe.Tests/GameWorldRaycastTests.cs` (TDD-first, confirm red): nearest-hit correctness (incl. two spheres on one ray, nearer wins), `RaycastAll` sorted-by-distance + buffer-truncation, layer-mask include/exclude (tagged vs. untagged via `ImportedEntitySpec.Layer`), miss cases, `maxDistance<=0` throws, 0-alloc-after-warmup (both entry points, `SurfaceContactsTests.cs`-style) |
| PQ-006 | code | M | done | PQ-005 | `src/Agapanthe.World/RaycastHit.cs` (`readonly record struct`) + `src/Agapanthe.World/GameWorld.Queries.cs` (new partial, sibling to `.Physics.cs`): `AllLayers` const, `TryRaycast`/`RaycastAll` — single query + inline `Has<QueryLayer>()` (spec §3.2 step 1, NOT a two-query split), reused scratch grid (separate from the physics grid's `_cellHead`/`_cellNext`), `results[..count].Sort(CompareByDistance)` via a cached `private static Comparison<RaycastHit>` method group. Deviation: query is `WithAll<GlobalId, WorldPosition, WorldTransform, Bounds>()` (added `WorldTransform`, reusing the existing `WorldSphere` helper to rotate `Bounds.Center` correctly — same drawable-only filtering). No shared callback-based traversal (would allocate a closure, breaking the 0-alloc gate) — `TryRaycast`/`RaycastAll` each inline their own DDA walk over shared static helpers. |
| PQ-007 | test | S | done | PQ-004 | `SceneTomlReaderTests.cs`/`SceneCompilerTests.cs`: parse + compile an optional `layer` key on `[[entity]]` — confirm red |
| PQ-008 | code | S | done | PQ-007 | `SceneAuthoring.cs`/`SceneTomlReader.cs`/`SceneCompiler.cs`: optional `layer` key, cook-time validated, threaded into `ImportedEntitySpec.Layer` at materialization. `.agscene` bumped v5→v6. Entity-only scope (grid/cluster explicitly reject `layer`, mirroring the `body`/`velocity` Contenu-3c-3 precedent) — deliberate, not a limitation. |
| PQ-009 | test | S | done | PQ-002 | `tests/Agapanthe.Tests/CameraScreenPointToRayTests.cs` (TDD-first, confirm red): viewport-center ray points along `Camera.Forward`; a corner ray diverges by roughly the expected FOV angle; re-narrowing the produced `Ray.Origin` against `RenderView.Origin` recovers the camera's own world position |
| PQ-010 | code | S | done | PQ-009 | `src/Agapanthe.Rendering/Camera.cs`: `ScreenPointToRay(Vector2, uint, uint)` — direction built from camera basis (Right/Up/Forward) scaled by NDC from `FovY`/`AspectRatio` (algebraically equivalent to inverting `ProjectionMatrix`, avoids a matrix inverse — documented deviation), origin widened via `RenderView.Origin` |

**Parallel-safe**: PQ-005→006 (`Agapanthe.World`) ∥ PQ-007→008 (`Agapanthe.Assets.Pipeline`) ∥
PQ-009→010 (`Agapanthe.Rendering`) — disjoint files, each depends only on Wave 1 outputs.

## Wave 3 — Sandbox demo

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| PQ-011 | code | S | done | PQ-006, PQ-010, PQ-008 | Sandbox click-to-highlight demo (spec §3.3): on mouse click, `camera.ScreenPointToRay(...)` → `world.TryRaycast(...)`, log/highlight the hit entity. Demo only — no new engine surface, no new gameplay system. **Deviation**: `IWindow` exposes no mouse-click event or raw cursor position (only capture-on-click + delta-while-captured), and once captured the cursor has no on-screen position anyway — used a screen-**center** crosshair raycast triggered by `KeyPressed(Key.F)` instead, wired once at `AppHost` level (applies to Sandbox + TopDown for free, zero new `.agscene`/`IWindow` surface). Log-line only, no visual highlight. |

## Wave 4 — verify + mandatory tail

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| PQ-012 | test | M | **partially done** | PQ-011 | Full `dotnet test` green (890/890, 0 warnings) — **done**. Isolation confirmed by grep: `TryRaycast`/`RaycastAll` called only from `AppHost.cs`'s `Key.F` demo hook, never from any render/tick path. `model`-scene capture: git-stash A/B proof (pre-milestone commit `974384a` vs current tree, same env vars) → **byte-identical** `9a010fc311dd51b74f755d306d4a819f` — this milestone is a render no-op, confirmed rather than assumed. JIT-Debug == JIT-Release == NativeAOT-publish, all three produce the same hash on `model`, 0 leak each — **AOT gate done** for Sandbox; HeadlessSim AOT already confirmed by the audit fork (snapshot hash `dbe9ed91…` JIT==AOT). **All 8 remaining pinned scenes now also verified** via the same git-stash A/B technique (batched: all 8 "after" captures, one stash, all 8 "before" captures, one pop) — every one byte-identical (`grid`/`drop`/`metalrough`/`planet`/`planet-drop`/`planet-challenge`/`drive`/`topdown`), and 5 of them (`planet`, `planet-drop`, `planet-challenge`, `drive`, `topdown`) exactly match the hashes already pinned in `CLAUDE.md` (confirming the repro params used were correct, not just self-consistent). Full suite re-confirmed 890/890 green after each stash round-trip. **Live human visual verdict: PASS** (2026-09-13, `AGAPANTHE_SCENE=grid`) — `F` on a visible entity logged `[raycast] hit entity EntityRef(94) at distance 9.19/9.21/9.58 m` (stable across near-repeats, plausible distances), `F` toward empty space logged `[raycast] no hit`. **PQ-012 fully done.** **Found + fixed along the way**: `CookRunner.CookerVersion` wasn't bumped for the `.agscene` v5→v6 bump (PQ-007/008) — same bug class as a prior audited Contenu-3c-2 finding; bumped to `"physics-queries"`. |
| PQ-013 | test | — | **done** | PQ-012 | Self code review + Requirements validation vs D1-D6 — all 6 verified directly against code (grep/diff, not assumed): D1 no shape-overlap query exists · D2 query is `WithAll<GlobalId, WorldPosition, WorldTransform, Bounds>` (not `BodyDesc`) · D3 single query + inline `Has<QueryLayer>()`, no `WithNone<T>()` split anywhere · D4 `GameWorld.Physics.cs` diff is only the 6-line `QueryLayer` tagging addition — its `_cellHead`/`_cellNext` untouched and unreferenced by `GameWorld.Queries.cs`'s own separate scratch · D5 `TryRaycast`/`RaycastAll` present, sort via a cached `static readonly Comparison<RaycastHit>` (not allocated per call), `maxDistance` validated · D6 `GameWorld.Queries.cs` has zero reference to `Agapanthe.Rendering`/`Camera` (headless-safe confirmed), `Camera.ScreenPointToRay` lives in `Agapanthe.Rendering`. |
| PQ-014 | infra | — | **done** | PQ-013 | Double audit `csharp-lowlevel` (3,6/5, **1 🔴** + 6 🟠 + 6 🟡) + `engine-architect` (4,1/5, 0 🔴, 3 🟠 + 5 🟡), dispatched in parallel — PASS-with-concerns both, strong convergence (3 findings independently hit by both: orthographic `ScreenPointToRay`, unbounded DDA walk cost, `RaycastAll` truncation semantics). **The 🔴** (`maxDistance = +Infinity` hangs the raycast — real, reachable from the public API) and every 🟠, plus most 🟡, fixed via two parallel fix-forks (disjoint files: World/Core math vs Rendering/Scene/Sandbox) + one fix applied directly by the coordinator (F8, missed in the fork dispatch). **Fixes applied**: infinite-loop guard on `maxDistance` · DDA walk now bounded to the occupied-cell AABB tracked during grid build (closes the O(maxDistance) cost hazard) + `TryRaycast` shrinks its bound to `bestDistance` once a hit is found + `RaycastAll` breaks out once its buffer is full · `RaycastHit.Point` now computed in exact `Double3` arithmetic (was narrowing to `float` before multiplying, contradicting the "solved in double" design premise) · `ArgumentException` guard on non-finite/zero `Ray.Direction` (closes a NaN-poisoning + undocumented-`ArithmeticException` hazard) · deterministic strict-`<` tie-break for equal-distance hits (was archetype-order-dependent) · `RaySphereIntersect.TryIntersectSphere` rewritten with the numerically-stable closest-point formulation (was catastrophically cancelling at ≳1e8 m — verified fixed with a new test at 7.48e10 m, this engine's own `AGAPANTHE_SUN` scale) · `EnsureStampCapacity` now grows by doubling (was reallocating every call once a world exceeds its initial size — exactly the `planet-drop`/`planet-challenge` probe-spawning shape) · `Camera.ScreenPointToRay` **properly implements** the orthographic branch (parallel rays, per-pixel origin offset) instead of silently producing a perspective-style cone — the spec's §7 false "projection-agnostic" claim corrected · `Layer` added to the 2 remaining `ImportedEntitySpec` field-by-field copy sites (`ProbeDropSystem`/`LandingChallengeSystem`, currently a no-op since no template populates `Layer` yet, but closes the seam) · `layer = 0` now rejected at cook time (was silently unreachable by every query including `AllLayers`). Test coverage substantially widened (diagonal rays, negative-direction hits, off-axis spheres, origin outside the occupied grid, infinity/zero/NaN-direction rejections, planetary-magnitude precision). **904/904 tests green, 0 warnings, 0 regressions** (890 → 904, +14 net). **Deferred as accepted 🟡** (documented, not fixed): F9 (no dedicated v4-snapshot-format fixture — coverage gap, not a bug), F13 (the `Debug.Assert` component-count tripwire lives in `Load` not just `Save` — arguable, low-value to move), F15 (`Camera.Right` NaN at pitch ±90° — pre-existing `Camera` degeneracy, out of this milestone's scope), F16 (a test's tolerance is looser than ideal but the underlying convention is correct and unit-tested elsewhere), F18 (a micro-optimization hoisting a per-entity check to per-chunk — real but negligible), F8's own sub-note now closed. |
| PQ-015 | docs | — | **done** | PQ-014 | Converge: `CLAUDE.md` (top summary line + full milestone entry), `docs/AVANCEMENT.md` (Reprise section entry + pointer update), `docs/BACKLOG.md` (item marked closed, remaining items re-scoped), spec §8 outcome section written, board archived to `.absolute-work/archive/board-session40-physicsqueries.md` |

## DAG (dependency depth)

```
W1: PQ-001 ── PQ-002 ─┐                                  (Core)
    PQ-003 ── PQ-004 ─┼─► W2: PQ-005 ── PQ-006 ─┐        (World, needs 002+004)
                       │       PQ-007 ── PQ-008 ─┼─► W3: PQ-011 ─► W4: PQ-012 ── PQ-013 ── PQ-014 ── PQ-015
                       │       (needs 004 only)   │
                       └────►  PQ-009 ── PQ-010 ──┘        (Rendering, needs 002 only)
```

## Requirements validation (to fill at PQ-013)

- D1 raycast only — verify no shape-overlap query was accidentally added.
- D2 hits every drawable via `Bounds`, not just `RigidBody` — verify the query in `PQ-006` is
  `WithAll<GlobalId, WorldPosition, Bounds>()`, not `BodyDesc`.
- D3 `QueryLayer` single-query + inline `Has<QueryLayer>()`, authoring via `ImportedEntitySpec.Layer`
  + TOML `layer` key, `WorldSerialization.cs`'s 3 cases present — verify no two-query split exists.
- D4 broadphase rebuilt per-call, separate scratch from the physics grid — verify `_cellHead`/
  `_cellNext` (physics) are untouched by `git diff`.
- D5 `TryRaycast` + `RaycastAll`, caller-provided buffer, sorted by distance, 0-alloc.
- D6 `Camera.ScreenPointToRay` — `GameWorld`'s raycast itself takes a plain `Ray`, no `Camera`/
  window dependency (headless-safety check — grep `GameWorld.Queries.cs` for any `Agapanthe.Rendering`
  reference, should be none).

## Deferred Work (carried from spec §7)

Shape-overlap queries (sphere/box "what's within this region") · mesh-accurate raycasting (today's
hit test is against the `Bounds` sphere proxy) · a persisted/incrementally-dirty-tracked broadphase
(D4) · any new gameplay system consuming the raycast beyond the Sandbox demo · `TopDown`'s
orthographic camera getting its own `ScreenPointToRay` verification (the math is projection-agnostic
but untested there this milestone).
