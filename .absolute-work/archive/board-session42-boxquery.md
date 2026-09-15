# Absolute Work Board — Shape queries: box overlap (AABB)

**Status**: `completed` — 2026-09-15 (session 42)
**Spec**: `docs/plans/2026-09-14-shape-queries-box-overlap-design.md` (APPROVED 4.6/5, scored
review by a separate subagent, 1 iteration — every reused-machinery claim verified against
current source, no fabrications, only a non-blocking suggestion applied before approval: concrete
boundary-margin test numbers added to §5, reusing session 41's own derived setup)
**Session**: 42
**Rollback point**: commit `dea6ef88807eb9a761d1663ada17eaf18799c242` (feat(shape-queries): sphere
overlap — last commit before this milestone)

## Scope

Backlog §4quater's next item after shape queries — sphere overlap (closed S41). A single new
query — `GameWorld.OverlapBox` (AABB, no rotation) — reusing the sphere-overlap milestone's
broadphase infrastructure unmodified, with every S41 audit lesson (cost-bound fallback, stamp
dedup, loop-condition capacity check, deterministic tie-break) applied from the first line of
code rather than retrofitted. Plus a `Key.H` Sandbox/TopDown demo. OBB is explicitly deferred
(spec D1) — this is AABB-only.

## Project conventions (unchanged)

.NET 10 solution, xUnit tests in `tests/Agapanthe.Tests`, `dotnet build`/`dotnet test` via
`Agapanthe.slnx`, TDD expected, PowerShell primary shell (Bash lacks `vswhere.exe` on PATH for
AOT publish — add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH first).
Commits on explicit request only. Double audit for this milestone is the **project-standard**
pairing — `csharp-lowlevel` + `engine-architect` (ECS/World/math work, no new Vulkan surface).

## Wave 1 — the query itself (TDD pair, single owner — tightly coupled files)

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| BQ-001 | test | M | done | — | `tests/Agapanthe.Tests/GameWorldBoxOverlapTests.cs` (TDD-first, confirm red): simple overlap, multi-candidate sorted-by-distance, buffer truncation, layer-mask include/exclude, miss, `min`/`max` non-finite throws, `min > max` on any axis throws, tangent inclusive `<=`, boundary-margin case (concrete numbers from spec §5: box `(-1.5,-1.5,-1.5)`-`(1.5,1.5,1.5)`, candidate at `(2.2,0,0)` radius 1, expect found at distance 2.2), huge-box-over-sparse-world completes quickly, deterministic equal-distance tie-break, 0-alloc-after-warmup. 14 tests, confirmed red (missing `OverlapBox`) then all green on first implementation pass. |
| BQ-002 | code | M | done | BQ-001 | `GameWorld.Queries.cs`: `ValidateBox(Double3, Double3)` (mirrors `ValidateRadius`/`ValidateCenter`'s exact posture) + `OverlapBox(Double3 min, Double3 max, Span<OverlapHit>, uint layerMask = AllLayers)` — reuses `GatherCandidates`/`BuildGrid` unmodified, one-cell-margin-expanded per-axis cell range, cost-bound check in `double` before `long` arithmetic with fallback to new `OverlapBoxScanCandidates` (mirrors `OverlapSphereScanCandidates`), `_qVisitedStamp`/`_qStamp` dedup reused, buffer-capacity check in the loop condition from the start, sorts via the already-fixed `CompareOverlapsByDistance` (no new comparer). New `TryOverlapBox` helper: exact clamp-based AABB-vs-sphere test (squared-distance comparison, no sqrt on rejected candidates) separate from the reported box-center-to-candidate distance (D2). Build 0 warnings, full suite 936/936 (922→936, +14). |

**Sequential, not parallel-safe** (same reasoning as S41): `OverlapBox`'s test file and
implementation are a tightly-coupled TDD pair on directly-adjacent code in one existing file.

## Wave 2 — Sandbox/TopDown demo

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| BQ-003 | code | S | done | BQ-002 | `src/Agapanthe.App/AppHost.cs`: `case Key.H:` in the existing `KeyPressed` switch (spec §3.2) — `world.OverlapBox(camera.Position - new Double3(5,5,5), camera.Position + new Double3(5,5,5), stackalloc OverlapHit[64], GameWorld.AllLayers)`, logs count + each hit entity/distance. Demo only, no new `IWindow` surface, no new gameplay system. Build 0 warnings. |

## Wave 3 — verify + mandatory tail

| ID | Type | Size | Status | Deps | Title |
|----|------|------|--------|------|-------|
| BQ-004 | test | M | **partially done** | BQ-003 | Full `dotnet test` green (936/936, 0 warnings) — **done**. All 9 pinned captures re-verified byte-identical via git-stash A/B against the `dea6ef8` rollback point — all 9 land on exactly the same hashes as S40/S41 — **done**. AOT publish (Sandbox) + JIT==NativeAOT confirmed on `model` (`9a010fc3…` both, 0 leak) — **done**. **Live human visual verdict: PASS** (2026-09-15). **BQ-004 fully done.** |
| BQ-005 | test | — | **done** | BQ-004 | Self code review + Requirements validation vs D1-D4 — all 4 verified directly against code (grep/diff, not assumed): D1 no `Quaternion`/rotation/OBB surface anywhere in `OverlapBox`'s path · D2 `OverlapHit(EntityRef, double Distance)` reused verbatim (no new result type), `reportDistance` computed from `boxCenter` (`(min+max)*0.5`), not the clamp-test distance · D3 `ValidateBox`'s guard (non-finite OR `min.X > max.X \|\| min.Y > max.Y \|\| min.Z > max.Z`) mirrors `ValidateRadius`/`ValidateCenter`'s posture exactly · D4 `case Key.H:` present in `AppHost.cs`, zero diff against rollback point on `IWindow.cs`/`AgSceneFormat.cs`/`SceneDefinition.cs`. |
| BQ-006 | infra | — | **done** | BQ-005 | Double audit `csharp-lowlevel` (3,7/5, **1 🔴** + 2 🟠 + 6 🟡) + `engine-architect` (4,1/5, 0 🔴, 3 🟠 + 6 🟡), dispatched in parallel — PASS-with-concerns both, **very strong convergence on a single serious finding**: both independently proved by mutation that the entire grid-walk path (margin expansion, stamp dedup, capacity-in-loop-condition) was exercised by ZERO of the 14 delivered tests — every one took the cost-bound scan-fallback branch (which triggers for any box, since the minimum cell estimate is 3×3×3=27, always ≥ the tiny candidate counts every test used). The named "boundary-margin" test proved nothing: mutating away the margin, the dedup, or the capacity guard left all 14 tests green. **Same defect independently found to already exist, silently, in S41's own `OverlapSphere` tests** — its cost-bound fallback (added by S41's own audit) had the identical effect, undetected until this session's cross-milestone comparison. **Fixes applied**: rewrote the boundary-margin test (both `OverlapBox` and, retroactively, `OverlapSphere`) to pad the world with enough far-away decoys (same radius, so `cellSize` is unchanged) to push `count` above the cost estimate and force the grid branch — verified myself by mutation (temporarily neutralized the margin expansion in both `OverlapBox` and `OverlapSphere`, confirmed both tests now correctly fail, then restored) · rewrote the "huge box/radius over sparse world" tests (both queries) to use genuinely far-apart candidates so the occupied-cell AABB itself is huge — the original version's single-candidate-at-origin design meant the occupied-AABB clamp alone (unrelated to the cost-bound fix) already made the query cheap, so it never actually exercised what it claimed to guard · added a new `OverlapBox_GridPath_ManyCandidatesInOneCell_TruncatesWithoutThrowing` test that forces the grid path via decoys and exercises the capacity-in-loop-condition guard together with stamp dedup — verified by mutation (temporarily removed the capacity guard from the loop condition, confirmed the test now correctly throws `IndexOutOfRangeException`, then restored) · added Y-axis and Z-axis inversion cases to the `min > max` validation test (previously only X was exercised, the exact copy-paste-across-axes defect class the test's own name claimed to cover) · `ValidateBox`'s exceptions now carry `paramName` (mirrors `ValidateCenter`) · `TryOverlapBox`'s `out` parameter moved to the end of its parameter list (mirrors every other `Try*` method in the file) · `OverlapHit`'s doc comment rewritten to be shape-neutral (it was written only for `OverlapSphere` and never updated when `OverlapBox` started sharing the type). **937/937 tests green, 0 warnings, 0 regressions** (936→937, +1 net — most fixes rewrote existing tests rather than adding new ones). AOT re-confirmed after fixes: `model` scene hash `9a010fc3…` unchanged, 0 leak. **Deferred as accepted 🟠/🟡 debt** (documented, versed to backlog, not fixed in this milestone): `TryRaycast`/`RaycastAll`'s DDA walk has no analogous cost-bound fallback — its cost is likewise dictated by `cellSize` (the world's largest object), not the query's `maxDistance`, and the engine's own `Key.F` demo (`maxDistance: 1_000_000.0`) can already reach ~13.5M dictionary probes per press in a dense-small-object world; real but pre-existing, out of this milestone's scope, versed to `docs/BACKLOG.md` §4quater · the cost estimate can underestimate the true swept-cell count by up to ~2.4× at small spans (inherited from `OverlapSphere`, benign) · truncation returns a different subset depending on which internal path (grid vs. scan) ran, which depends on total world population, not just the query — documented more thinly on `OverlapBox` than `RaycastAll`'s own wording · `BuildGrid`'s full O(count) bucketing cost is paid even when the scan-fallback branch is about to discard it (only `cellSize` was actually needed) · `OverlapSphere`'s per-candidate rejection still pays a `sqrt` where `OverlapBox`'s squared-distance test doesn't — a divergence now undocumented between the two siblings · a possible `long` overflow at astronomically extreme finite coordinates degrades to a silent empty result rather than a throw (shared with `OverlapSphere`, effectively unreachable in practice) · the equal-distance tie-break test only proves intra-process stability, not that the order is specifically `GlobalId`-ascending. |
| BQ-007 | docs | — | **done** | BQ-006 | Converge: `CLAUDE.md` (top summary line + full milestone entry), `docs/AVANCEMENT.md` (Reprise section entry + pointer update), `docs/BACKLOG.md` (item marked closed, new debt entry for the raycast cost-bound gap found during audit, OBB re-scoped as the remaining item), spec §8 outcome section written, board archived to `.absolute-work/archive/board-session42-boxquery.md` |

## DAG (dependency depth)

```
W1: BQ-001 ── BQ-002 ──► W2: BQ-003 ──► W3: BQ-004 ── BQ-005 ── BQ-006 ── BQ-007
```

No parallel-safe pairs — same reasoning as S41: this milestone is deliberately scoped to reuse
already-audited infrastructure on one existing file, no independent workstreams to split.

## Requirements validation (to fill at BQ-005)

- D1 AABB only, no rotation — verify no rotation parameter/OBB surface anywhere.
- D2 reuses `OverlapHit(EntityRef, double Distance)`, `Distance` = box-center-to-candidate-center
  — verify no new result type was introduced, and the reported distance is computed from
  `boxCenter`, not the clamped-point overlap-test distance.
- D3 `ValidateBox` rejects non-finite or inverted (`min > max` on any axis) with
  `ArgumentException` — verify guard shape mirrors `ValidateRadius`/`ValidateCenter`.
- D4 `Key.H` demo, `AppHost.cs` only — verify no new `IWindow` surface, no new
  `SceneSystemKind`/factory, no `.agscene` field.
- All S41 audit lessons applied from the start (not retrofitted) — verify: cost-bound check in
  `double` before `long` arithmetic with a scan fallback exists; `_qVisitedStamp`/`_qStamp` dedup
  is used in the grid-walk path; the buffer-capacity check is in the loop condition, not only
  after a bucket; the sort uses the existing `CompareOverlapsByDistance` (no new, potentially
  non-deterministic comparer).

## Deferred Work (carried from spec §7)

OBB (oriented box, arbitrary rotation) — would need `Double3` dot/cross/quaternion-rotation math
that doesn't exist today, no concrete use case · mesh-accurate overlap (inherits the `Bounds`
sphere-proxy approximation) · a persisted/incrementally-dirty-tracked broadphase · any new
gameplay system consuming `OverlapBox` beyond the `Key.H` demo.
