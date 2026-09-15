# Shape queries — box overlap, AABB (backlog §4quater)

## 1. Summary

Backlog §4quater's next item after shape queries — sphere overlap (closed session 41,
`GameWorld.OverlapSphere`). That milestone's D1 explicitly deferred box overlap ("what's within
this box?") as a separate future item. Research confirmed there is no box/AABB/OBB shape anywhere
in the engine today — no component, no physics collider — every candidate carries only a sphere
`Bounds`. So `GameWorld.OverlapBox` is necessarily a box-query-region-vs-sphere-candidate test,
never box-vs-box.

Scope is deliberately narrowed to an **axis-aligned bounding box (AABB)** — no rotation.
`Double3` has no dot product, cross product, or quaternion-rotation method today; an oriented box
(OBB) in double precision would need all of that added from scratch with no concrete driving use
case. An AABB needs none of it — `Math.Clamp` plus squared-distance comparison, the same
complexity class as `OverlapSphere`'s own math.

## 2. Decision log (interview)

| # | Decision |
|---|---|
| D1 | **AABB only**, no rotation. An OBB stays a separate future backlog item — it would require adding `Double3` dot/cross/quaternion-rotation math that doesn't exist anywhere in this engine today, with no concrete use case driving it. |
| D2 | Reuses `OverlapHit(EntityRef, double Distance)` — no new result type. `Distance` is the box's own center (`(min+max)/2`) to the candidate's center, the same center-to-center convention `OverlapSphere` already established (not a surface distance, not adjusted for the candidate's radius). |
| D3 | `min`/`max` non-finite (NaN/±Infinity, either vector) or `min > max` on any axis (an inverted/degenerate box) → `ArgumentException`. Mirrors `ValidateRadius`/`ValidateCenter`'s established posture from sessions 40/41: a degenerate box is essentially always a caller bug, not a legitimate query, and this project's convention is to reject loudly rather than silently normalize. |
| D4 | A small dedicated-key demo ships alongside the World-side query, mirroring `Key.F` (raycast, session 40) and `Key.G` (sphere overlap, session 41) — `Key.H`, confirmed unbound anywhere in the codebase, wired once in `AppHost.cs` at the same switch. A 10×10×10 box centered on the camera, logging the count + entities found. Demo only, no new gameplay system. |

## 3. Architecture

### 3.1 `Agapanthe.World` — the query

`GameWorld.Queries.cs` (existing partial, sibling to `.Physics.cs`) gains:
```csharp
public int OverlapBox(Double3 min, Double3 max, Span<OverlapHit> results, uint layerMask = AllLayers)
```
Standard guards: `ObjectDisposedException.ThrowIf`, `AssertOwnerThread()`, new `ValidateBox(min, max)`
(see §4).

**Reuses unmodified**: `GatherCandidates(layerMask)` and `BuildGrid(count)` — exactly as
`OverlapSphere` already does, confirming session 41's D4 payoff extends to a second, differently-
shaped query without any change to the shared broadphase build.

**The cell range**: `[floor(min.X * invCell) - 1, floor(max.X * invCell) + 1]` per axis (and
analogously for Y/Z) — the same one-cell margin `OverlapSphere` derived and had independently
re-verified by both spec review rounds and the double audit: a candidate's radius is at most
`cellSize/2`, so its center can be bucketed exactly one cell beyond the box's own naive range and
still geometrically overlap it. Clamped against the occupied-cell AABB from `BuildGrid`
(`_qMinCellX` etc.), same as `OverlapSphere`.

**Every session-41 audit lesson is applied from the first line of code, not retrofitted**:
- **Cost-bound check in `double`, before any `long` arithmetic** (closes the exact 🔴 the sphere
  milestone's audit found and fixed): `spanX = ((max.X - min.X) * invCell) + 3`, similarly for
  Y/Z (a box is not necessarily cubic, unlike a sphere's isotropic margin, so each axis is
  computed separately); `approxCells = spanX * spanY * spanZ`. If not finite, or `>= count`, fall
  back to a direct linear scan over the already-gathered `_qGid`/`_qCenter`/`_qRadius[0..count)`
  arrays (new `OverlapBoxScanCandidates`, mirrors `OverlapSphereScanCandidates`'s exact shape) —
  correct by construction, no grid/hashing/aliasing risk, never more expensive than the grid path
  it replaces.
- **`_qVisitedStamp`/`_qStamp` dedup reused** in the grid-walk path (closes the hash-aliasing
  finding: `QueryCellHash` is a hash, not an injective key, so two distinct cell coordinates
  within the swept range can alias to one dictionary bucket).
- **Buffer-capacity check lives in the loop condition** (`k != -1 && written < capacity`), not
  only after a bucket finishes — the exact fix the audit had to apply retroactively to
  `RaycastAll`'s pre-existing bug; here it is correct from the start.
- **Deterministic sort, no new comparer needed**: `OverlapBox` reuses `OverlapHit` and the
  already-fixed `CompareOverlapsByDistance` (with its `GlobalId` secondary key for equal-distance
  determinism) verbatim — nothing new to get right or wrong here.

**Per-candidate test** (a standard, exact AABB-vs-sphere overlap — not an approximation beyond the
inherited `Bounds` sphere-proxy every other query already accepts):
```csharp
var clampedX = Math.Clamp(_qCenter[k].X, min.X, max.X);
var clampedY = Math.Clamp(_qCenter[k].Y, min.Y, max.Y);
var clampedZ = Math.Clamp(_qCenter[k].Z, min.Z, max.Z);
var dx = _qCenter[k].X - clampedX;
var dy = _qCenter[k].Y - clampedY;
var dz = _qCenter[k].Z - clampedZ;
if (dx * dx + dy * dy + dz * dz <= (double)_qRadius[k] * _qRadius[k])
{
    var reportDistance = Double3.Distance(boxCenter, _qCenter[k]); // D2
    results[written] = new OverlapHit(new EntityRef(_qGid[k]), reportDistance);
    written++;
}
```
Two distinct distances are involved and must not be confused: the *overlap test* itself is a
squared-distance-to-the-clamped-point comparison (no square root paid per candidate tested — a
small, incidental improvement over `OverlapSphere`'s per-candidate `Double3.Distance` call, not a
requirement of this spec and not applied retroactively to `OverlapSphere`); the *reported*
`OverlapHit.Distance` is the box-center-to-candidate-center distance per D2, computed with a
single `Double3.Distance` call only for entities that actually overlap (`boxCenter = (min + max) *
0.5`, computed once, outside every loop).

`ValidateBox(Double3 min, Double3 max)` (new, mirrors `ValidateRadius`/`ValidateCenter`'s exact
posture): throws `ArgumentException` when any component of `min` or `max` is non-finite, or when
`min.X > max.X || min.Y > max.Y || min.Z > max.Z`.

### 3.2 `Agapanthe.App` — the demo hook

`AppHost.cs`'s existing `window.KeyPressed` switch (where `Key.F`'s raycast demo and `Key.G`'s
sphere-overlap demo already live, `camera`/`world`/`window` already in scope) gains a
`case Key.H:` calling `world.OverlapBox(camera.Position - new Double3(5, 5, 5), camera.Position +
new Double3(5, 5, 5), stackalloc OverlapHit[64], GameWorld.AllLayers)` — a 10×10×10 box centered
on the camera — then logs the count and each hit entity+distance, exactly mirroring `Key.G`'s
posture. No new `IWindow` surface.

## 4. Error handling

- `min`/`max` non-finite or inverted (`min > max` on any axis) → `ArgumentException` (D3).
- `results.Length == 0` → returns `0` immediately, same early-out `OverlapSphere`/`RaycastAll`
  already establish.
- No entities in the box → returns `0`, `results` untouched.
- Disposed `GameWorld` → `ObjectDisposedException` (standard gate).
- Called off the owning thread → the existing `AssertOwnerThread()` `[Conditional("DEBUG")]`
  guard, matching every other `GameWorld` query in this file.

## 5. Testing strategy

New `tests/Agapanthe.Tests/GameWorldBoxOverlapTests.cs`, mirroring
`GameWorldOverlapTests.cs`/`GameWorldRaycastTests.cs`'s structure and precedent:

- Simple overlap: a box containing exactly one candidate returns it with the correct
  box-center-to-candidate-center distance.
- Multiple overlapping candidates, sorted-by-distance output verified explicitly.
- Buffer truncation: more candidates than `results.Length` — count equals the buffer length,
  never throws.
- Layer-mask filtering: tagged candidate excluded/included exactly as `OverlapSphere`'s tests do.
- A miss: no candidate inside the box → returns `0`.
- `min`/`max` non-finite (NaN, `+Infinity`, either vector) — throws `ArgumentException`.
- `min > max` on any single axis — throws `ArgumentException`.
- Tangent edge case: a candidate whose sphere touches the box boundary exactly (distance-to-
  clamped-point equals the candidate's radius) — included (inclusive `<=`).
- Boundary-margin case (concrete numbers, reusing session 41's own derived setup so the naive cell
  range is identical): a sole candidate at `(2.2, 0, 0)` radius `1` — its own radius sets
  `cellSize = 2`, so `invCell = 0.5`. Box `min = (-1.5, -1.5, -1.5)`, `max = (1.5, 1.5, 1.5)` gives
  a naive X cell range of `floor(-1.5·0.5)=-1` to `floor(1.5·0.5)=0`. The candidate's own bucket is
  `floor(2.2·0.5)=1` — exactly one cell past the naive upper bound. The true overlap test still
  holds: clamping the candidate's center into the box gives `clampedX = 1.5`, distance
  `2.2-1.5=0.7 ≤ radius 1` — a real overlap. Without the one-cell margin expansion, cell 1 is
  never visited and this candidate is silently missed. Reported `Distance` (box-center `(0,0,0)`
  to candidate center) is `2.2`.
- Huge box over a sparse world completes quickly (mirrors session 41's audit-fix regression test
  for `OverlapSphere` — proves the cost-bound fallback works for `OverlapBox` too, from day one).
- 0-alloc-after-warmup.

**Live verification**: `Key.H` demo interaction in Sandbox + TopDown, human visual/log sanity
check (interactive-only feature, same posture as `Key.F`/`Key.G`).

**Regression gate**: every existing pinned capture (9 total: 8 Sandbox scenes + `topdown`)
re-verified byte-identical — `OverlapBox` is a pure query, never invoked from the render/tick
path. Full `dotnet test` green, `dotnet build` 0 warnings, AOT publish + JIT == NativeAOT
re-confirmed (mirrors sessions 40/41's exact verification recipe).

**Double audit**: `csharp-lowlevel` + `engine-architect` — the project-standard pairing (ECS/World/
math work, no new Vulkan surface).

## 6. Migration path

Single wave, no format/version bump anticipated (no new component, no new `.agscene` field — a
pure runtime query over data the engine already tracks).

1. `ValidateBox` + `OverlapBox` + `OverlapBoxScanCandidates` (`GameWorld.Queries.cs`) — TDD:
   write `GameWorldBoxOverlapTests.cs` red first.
2. `Key.H` demo wiring in `AppHost.cs`.
3. Full verification sweep (build, tests, 9 captures, AOT/JIT parity).
4. Double audit, apply findings.
5. Converge: docs updated, spec §7 outcome section, board archived.

## 7. Out of scope (deferred, not silently dropped)

- OBB (oriented box, arbitrary rotation) — would require adding dot product, cross product, and
  quaternion-based rotation to `Double3` (none exist today), with no concrete driving use case.
- Mesh-accurate overlap — inherits the `Bounds` sphere-proxy approximation every other query in
  this engine already documents and accepts.
- A persisted/incrementally-dirty-tracked broadphase — same reasoning as `OverlapSphere`'s D4-
  inherited stance: the per-call rebuild is deliberately the simpler, sufficient-for-now choice.
- Any new gameplay system consuming `OverlapBox` beyond the `Key.H` demo.

## 8. Outcome (closed 2026-09-15, session 42)

Shipped as designed, D1-D4 all intact per BQ-005's self-review (verified against the actual code,
not assumed). 7-task/3-wave board. Reuse fidelity is real and total: `git diff` shows `GatherCandidates`
and `BuildGrid` untouched by this milestone — the raycast milestone's D4 bet (session 40) has now
paid off for a second, differently-shaped consumer without anything bending.

**Verification**: 937 tests (936 → 937, +1 net — most audit fixes rewrote existing tests rather than
adding new ones), 0 warnings, 0 regressions. All 9 pinned captures re-verified byte-identical via
git-stash A/B against the `dea6ef88807eb9a761d1663ada17eaf18799c242` rollback point — every one
landed on exactly the same hash already pinned from the prior two milestones. Sandbox JIT ==
NativeAOT confirmed both before and after the audit fix pass (`9a010fc3…`, unchanged), 0 leak.
Live human visual verdict **PASS** (2026-09-15): `H` near an entity logs a plausible count +
distance, `H` far from everything logs `0`.

**Double audit** — `csharp-lowlevel` (3.7/5, PASS-with-concerns) + `engine-architect` (4.1/5,
PASS-with-concerns), the project-standard pairing. Extremely strong convergence: both reviewers
independently proved, **by mutation**, the same serious defect.

**1 🔴 found and fixed** — the entire grid-walk implementation (one-cell margin expansion, stamp
dedup, buffer-capacity-in-loop-condition guard) was exercised by **zero** of the 14 delivered
tests. The cost-bound fallback's minimum cell estimate is 3×3×3=27 (a box's span term is `extent ·
invCell + 3`, so even a degenerate point box estimates 27 cells) — every test used 1-3 candidates,
so `approxCells (≥27) >= count` was true everywhere, and every test silently took the
`OverlapBoxScanCandidates` linear-scan branch instead. The named "boundary-margin" test — the
milestone's own headline demonstration — proved nothing: both auditors confirmed by temporarily
deleting the margin expansion that all 14 tests stayed green. **The identical defect was found,
independently, to already exist silently in session 41's own `OverlapSphere` tests** — S41's own
audit-added cost-bound fallback had the same effect on its boundary-margin test, undetected for an
entire milestone until this session's cross-milestone comparison surfaced it. Fixed in both places
by padding the test world with enough far-away, non-overlapping decoys (same radius, so `cellSize`
is unchanged) to push `count` above the cost estimate and force the grid branch — and personally
re-verified by mutation (temporarily neutralizing the margin in both `OverlapBox` and
`OverlapSphere`'s source, confirming both fixed tests now correctly fail, then restoring). A new
`OverlapBox_GridPath_ManyCandidatesInOneCell_TruncatesWithoutThrowing` test was added to cover the
capacity-guard/stamp-dedup half of the grid path, likewise mutation-verified (temporarily removing
the loop-condition capacity check reproduces the exact `IndexOutOfRangeException` the guard exists
to prevent).

**Other findings fixed** (all 🟠/🟡): the "huge box/radius over sparse world" regression tests (both
`OverlapBox` and, retroactively, `OverlapSphere`) used a single candidate at the origin, so the
occupied-cell AABB was already a single cell and the clamp alone — unrelated to the cost-bound fix
being tested — made the query cheap regardless; rewritten with genuinely far-apart candidates so
the occupied AABB itself is huge and only the cost-bound fallback can keep the query fast · the
`min > max` inversion test exercised only the X axis, the exact copy-paste-across-axes defect class
its own name claimed to cover — Y and Z cases added · `ValidateBox`'s exceptions now carry
`paramName` (mirrors `ValidateCenter`) · `TryOverlapBox`'s `out` parameter moved to the end of its
signature (every other `Try*` in the file ends with its `out`) · `OverlapHit`'s doc comment,
written only for `OverlapSphere`, updated to describe both shapes now sharing the type.

**Deferred as accepted debt** (documented, versed to `docs/BACKLOG.md` §4quater, not fixed in this
milestone): `TryRaycast`/`RaycastAll`'s DDA walk has no analogous cost-bound fallback — its cost is
equally dictated by `cellSize` (the world's largest object), not the query's own `maxDistance`, and
the engine's own `Key.F` demo (`maxDistance: 1_000_000.0`) can already reach millions of dictionary
probes per press in a dense-small-object world; real, pre-existing, out of scope here · the cost
estimate can underestimate the true cell count by up to ~2.4× at small spans (inherited from
`OverlapSphere`, benign) · truncation returns a different subset depending on which internal path
ran, which depends on total world population rather than just the query · `BuildGrid`'s full
O(count) bucketing cost is paid even when about to be discarded by the scan-fallback branch (only
`cellSize` was actually needed) · `OverlapSphere` still pays a `sqrt` per rejected candidate where
`OverlapBox`'s squared-distance test doesn't — a now-undocumented divergence between the siblings ·
a possible `long` overflow at astronomically extreme finite coordinates degrades to a silent empty
result rather than a throw (shared with `OverlapSphere`, effectively unreachable in practice) · the
equal-distance tie-break test proves intra-process stability but not specifically `GlobalId`-order.
