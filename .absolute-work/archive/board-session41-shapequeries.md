# Absolute Work Board — Shape queries: sphere overlap

**Status**: `completed` — 2026-09-14 (session 41)
**Spec**: `docs/plans/2026-09-14-shape-queries-overlap-design.md` (APPROVED 4.4/5, scored review
by a separate subagent, 2 iterations — round 1 (3.4/5 NEEDS WORK) found a real internal-
consistency defect: the walk's stated dedup rationale mischaracterized `BuildGrid`'s actual
one-bucket-per-candidate behavior; the real fix — a one-cell margin expansion of the iterated
range — was identified and applied; round 2 independently re-derived the margin math and
confirmed it correct, APPROVED)
**Session**: 41
**Rollback point**: commit `54747c898ed3e1a87bdad1b1fed5d06aa4e70499` (feat(physics-queries):
raycast + layer mask — last commit before this milestone)

## Scope

Backlog §4quater's next item after physics queries — raycast + layer mask (closed S40). A single
new query — `GameWorld.OverlapSphere` — reusing the raycast milestone's `GatherCandidates`/
`BuildGrid` broadphase infrastructure unmodified, plus a `Key.G` Sandbox/TopDown demo. Box
overlap is explicitly deferred (spec D1) — this is sphere-only.

## Project conventions (unchanged)

.NET 10 solution, xUnit tests in `tests/Agapanthe.Tests`, `dotnet build`/`dotnet test` via
`Agapanthe.slnx`, TDD expected, PowerShell primary shell (Bash lacks `vswhere.exe` on PATH for
AOT publish — add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH first).
Commits on explicit request only. Double audit for this milestone is the **project-standard**
pairing — `csharp-lowlevel` + `engine-architect` (same reasoning as S40: ECS/World/math work, no
new Vulkan surface).

## Wave 1 — the query itself (TDD pair, single owner — tightly coupled files)

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| SQ-001 | test | M | done | — | `tests/Agapanthe.Tests/GameWorldOverlapTests.cs` (TDD-first, confirm red): simple overlap, multi-candidate sorted-by-distance, buffer truncation, layer-mask include/exclude, miss, `radius<=0`/NaN/`+Infinity` throw, tangent inclusive `<=`, boundary-margin case (candidate bucketed one cell outside the naive AABB but still geometrically overlapping — spec §5), 0-alloc-after-warmup. 13 tests, all confirmed red (missing `OverlapSphere`) then green on first implementation pass, including the boundary-margin case that exercises exactly the bug round-1 spec review caught. |
| SQ-002 | code | M | done | SQ-001 | `src/Agapanthe.World/OverlapHit.cs` (`readonly record struct OverlapHit(EntityRef Entity, double Distance)`, doc comment states center-to-center distance explicitly — spec §3.1) + `GameWorld.Queries.cs`: `ValidateRadius` (mirrors `ValidateMaxDistance`'s exact guard shape) + `OverlapSphere(Double3, float, Span<OverlapHit>, uint layerMask = AllLayers)` — reuses `GatherCandidates`/`BuildGrid` unmodified, one-cell-margin-expanded AABB cell range (spec §3.1, NOT the naive unexpanded range), no stamp/dedup (single non-marching sweep visits each cell once by construction), truncation check placed after each cell bucket finishes (mirrors `RaycastAll`'s exact post-bucket placement, not mid-candidate), sort via a new cached `static readonly Comparison<OverlapHit>` (mirrors `CompareByDistance`'s pattern, cannot share the delegate itself — different struct). Build 0 warnings, full suite 917/917 (904→917, +13). |

## Wave 2 — Sandbox/TopDown demo

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| SQ-003 | code | S | done | SQ-002 | `src/Agapanthe.App/AppHost.cs`: `case Key.G:` in the existing `KeyPressed` switch (spec §3.2) — `world.OverlapSphere(camera.Position, 10f, stackalloc OverlapHit[64], GameWorld.AllLayers)`, logs count + each hit entity/distance. Demo only, no new `IWindow` surface, no new gameplay system. Build 0 warnings. |

## Wave 3 — verify + mandatory tail

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| SQ-004 | test | M | **partially done** | SQ-003 | Full `dotnet test` green (917/917, 0 warnings) — **done**. All 9 pinned captures re-verified byte-identical via git-stash A/B against the `54747c8` rollback point — **all 9 land on exactly the same hashes as S40** (`model` `9a010fc3…`, `grid` `c314e6a1…`, `drop` `611fbdfa…`, `metalrough` `c4cf4605…`, `planet` `99e2f4a3…`, `planet-drop` `81ddf074…`, `planet-challenge` `ea6ba910…`, `drive` `9030f6a6…`, `topdown` `31ea748d…`) — **done**. AOT publish (Sandbox) + JIT==NativeAOT confirmed on `model` (`9a010fc3…` both, 0 leak) — **done**. One transient GLFW/Silk.NET shutdown crash hit during the `grid` capture run (pre-existing documented ~1-in-5 issue, occurs *after* the capture is saved and the leak report is clean — not a regression; verified the `.ppm` was written correctly and its hash matches). **Live human visual verdict: PASS** (2026-09-14). **SQ-004 fully done.** |
| SQ-005 | test | — | **done** | SQ-004 | Self code review + Requirements validation vs D1-D5 — all 5 verified directly against code (grep/diff, not assumed): D1 no `OverlapBox`/box-overlap surface anywhere · D2 `OverlapHit(EntityRef, double Distance)`, no `Point` field, doc comment states center-to-center explicitly · D3 caller-provided `Span<OverlapHit>`, `goto Done` truncation, sorted via `CompareOverlapsByDistance` · D4 `ValidateRadius`'s guard (`!(radius > 0f) \|\| float.IsInfinity(radius)`) mirrors `ValidateMaxDistance`'s exact shape · D5 `case Key.G:` present in `AppHost.cs`, zero diff against rollback point on `IWindow.cs`/`AgSceneFormat.cs`/`SceneDefinition.cs` (no new `IWindow` surface, no new `.agscene` field). |
| SQ-006 | infra | — | **done** | SQ-005 | Double audit `csharp-lowlevel` (3,8/5, **1 🔴** + 3 🟠 + 4 🟡) + `engine-architect` (4,0/5, 0 🔴, 5 🟠 + 3 🟡), dispatched in parallel — PASS-with-concerns both, very strong convergence (2 findings independently hit by both: the unbounded volume sweep, and a pre-existing `RaycastAll` buffer-overrun bug the two reviewers found by comparing it to `OverlapSphere`'s correct guard). **The 🔴** (a natural-looking large-radius `OverlapSphere` call sweeps a cell-box volume unbounded by the query itself — cellSize is dictated by the world's largest object, not the query radius, so a "what's within 1 km" call over a dense-small-object world can sweep 10⁹+ empty cells) and every 🟠 fixed. **Fixes applied**: `OverlapSphere` now estimates the swept cell volume in `double` *before* touching `long` arithmetic and falls back to a direct linear scan over the already-gathered candidate array whenever the grid would visit at least as many cells as there are candidates — this closes the 🔴 sweep-cost hazard AND a separate 🟡 (`long` overflow degrading to a silent empty result) as one fix, since the fallback pre-empts the overflow-prone arithmetic entirely · restored `_qVisitedStamp`/`_qStamp` dedup in `OverlapSphere`'s grid path (the "tested at most once by construction" comment was true of cell *coordinates* but not of `QueryCellHash`'s *hash*, which can alias two distinct coordinates onto one bucket — a real, if low-probability, duplicate-hit risk) · a genuine **pre-existing bug in the already-shipped `RaycastAll`** (committed as part of S40, `54747c8`) — its capacity check sat only after each cell's candidate chain finished, not in the loop condition, so a single cell holding more candidates than the remaining buffer could write past the end and throw `IndexOutOfRangeException` from an API documented as never throwing for an undersized buffer; found by both reviewers independently via comparison against `OverlapSphere`'s correctly-guarded sibling, fixed by mirroring the correct pattern back onto `RaycastAll` · added `ValidateCenter` (mirrors `ValidateDirection`'s posture) rejecting a non-finite query center, which previously produced a silent, indistinguishable-from-a-miss `0` result · widened the overlap threshold (`radius + candidateRadius`) to `double` before comparing, closing the same float-narrowing defect class S40 already fixed once on `RaycastHit.Point` · added a `GlobalId` secondary sort key to both `CompareHitsByDistance` and `CompareOverlapHitsByDistance` — `Span<T>.Sort` is an unstable introsort, so equal-distance results were ordered by non-deterministic grid-walk/Arch-archetype order, the same bug *class* S40 had already fixed once for `TryRaycast`'s tie-break but never carried over to the `*All` sort-based siblings. **6 new regression tests added** (huge-radius-over-sparse-world completes quickly, center NaN/Infinity throws, deterministic equal-distance ordering, `RaycastAll` many-candidates-one-cell no-throw). **922/922 tests green, 0 warnings, 0 regressions** (917 → 922, +5 net — one test covers two related fixes). AOT re-confirmed after fixes: `model` scene hash `9a010fc3…` unchanged, 0 leak. **Deferred as accepted 🟡/🟠 debt** (documented, not fixed — no current consumer needs them and fixing now would be speculative API churn): `OverlapHit` carries no `Double3 Center`/`float Radius`, so a caller cannot derive a direction or a surface-adjusted distance — flagged by `engine-architect` as the most likely thing to force API churn on the first real consumer, but D2 was an explicit, already-approved spec decision and no consumer exists yet · `Double3.Distance`'s sqrt is paid on every rejected candidate where a squared-distance pre-filter would be cheaper (micro-optimization, `QuerySurfaceContacts` already does this for its own different candidate set) · the engine now has two independently-evolving region-query code paths (`OverlapSphere` vs. `GameWorld.Physics.cs`'s `QuerySurfaceContacts`) with different candidate sets and distance conventions — noted, not merged (genuinely different requirements, not obviously reconcilable). |
| SQ-007 | docs | — | **done** | SQ-006 | Converge: `CLAUDE.md` (top summary line + full milestone entry), `docs/AVANCEMENT.md` (Reprise section entry + pointer update), `docs/BACKLOG.md` (item marked closed, box overlap re-scoped as the remaining item), spec §8 outcome section written, board archived to `.absolute-work/archive/board-session41-shapequeries.md` |

## DAG (dependency depth)

```
W1: SQ-001 ── SQ-002 ──► W2: SQ-003 ──► W3: SQ-004 ── SQ-005 ── SQ-006 ── SQ-007
```

No parallel-safe pairs this milestone — `OverlapHit.cs`/`GameWorld.Queries.cs` are a tightly
coupled TDD pair (one query, one result type), and every subsequent task strictly depends on the
one before it (demo needs the query; verify needs the demo wired; audit needs verify green;
converge needs audit applied). Smaller and more linear than S40's 4-wave/15-task raycast board —
this milestone is explicitly scoped to reuse already-audited infrastructure (spec §1).

## Requirements validation (to fill at SQ-005)

- D1 sphere-only — verify no box-overlap surface was accidentally added.
- D2 `OverlapHit(EntityRef, double Distance)`, no `Point` field — verify against `RaycastHit`'s
  shape to confirm the deliberate divergence held.
- D3 caller-provided `Span<OverlapHit>` buffer, sorted by distance, same truncation semantics as
  `RaycastAll` — verify `goto Done`-style early exit exists, not a silent post-hoc discard.
- D4 `radius <= 0` or non-finite throws `ArgumentOutOfRangeException` — verify `ValidateRadius`
  mirrors `ValidateMaxDistance`'s exact double-guard shape (non-positive OR infinite, not just one).
- D5 `Key.G` demo, `camera.Position` + 10m, `AppHost.cs` only — verify no new `IWindow` surface,
  no new `SceneSystemKind`/factory, no `.agscene` field added.

## Deferred Work (carried from spec §7)

Box overlap ("what's within this box?") — separate future backlog item · mesh-accurate overlap
testing (inherits the `Bounds` sphere-proxy approximation) · a persisted/incrementally-dirty-
tracked broadphase · any new gameplay system consuming `OverlapSphere` beyond the `Key.G` demo ·
a distinct "N nearest" query mode with a true nearest-first guarantee under truncation (D3
deliberately inherits `RaycastAll`'s existing truncation semantics instead).
