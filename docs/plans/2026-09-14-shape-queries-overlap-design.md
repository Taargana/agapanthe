# Shape queries — sphere overlap (backlog §4quater)

## 1. Summary

Backlog §4quater's next item after physics queries — raycast + layer mask (closed session 40).
That milestone's D1 explicitly deferred shape-overlap queries ("what's within this region") as
a separate future item, not invented alongside the raycast just because the backlog title also
said "shapes". This design delivers the first of those: `GameWorld.OverlapSphere` — "what is
inside this sphere?" — the direct region-query counterpart to "what does this ray hit?".

It reuses, unmodified, the broadphase infrastructure the raycast milestone built and explicitly
anticipated being reused this way (`GatherCandidates`, `BuildGrid`, `QueryLayer`'s single-query +
inline `Has<QueryLayer>()` resolution) — this is a genuinely small, low-risk addition on top of
already-audited machinery, not a new subsystem.

Box overlap ("what's within this box?") is a real, separate feature — deferred to a later item,
not invented here just because the backlog title also names "formes" (shapes, plural).

## 2. Decision log (interview)

| # | Decision |
|---|---|
| D1 | Scope = **sphere overlap only**. Box overlap is a separate future backlog item — a different shape representation (rotation, AABB vs. OBB) with no concrete use case driving it yet; inventing it now would be speculative. |
| D2 | Result type = **`OverlapHit(EntityRef Entity, double Distance)`** — a region query almost always needs the distance immediately after the call (damage falloff, "select nearest"), and it costs nothing extra to report (already computed by the sphere-sphere overlap test itself). Mirrors `RaycastHit`'s shape, minus the approximate hit `Point` (a region query has no single hit point — a sphere fully containing a candidate has no meaningful surface intersection). |
| D3 | `int OverlapSphere(Double3 center, float radius, Span<OverlapHit> results, uint layerMask = AllLayers)` — caller-provided buffer (0-alloc, matches `RaycastAll`'s established convention), **sorted by distance** at the end, same truncation limitation `RaycastAll` already documents and accepts: if `results` is undersized, the first `results.Length` candidates encountered in grid-walk order are kept and *those* are sorted — not a guaranteed globally-nearest-N. Consistency with the already-audited `RaycastAll` contract was preferred over inventing a different (stricter, more expensive) truncation semantic for a sibling API. |
| D4 | `radius <= 0` or non-finite (NaN/`Infinity`) → `ArgumentOutOfRangeException`. Mirrors `TryRaycast`/`RaycastAll`'s `ValidateMaxDistance` exactly, including the audit-learned lesson from session 40 (finding csharp-lowlevel-F1: `+Infinity` is neither NaN nor `<= 0` and must be checked separately, or it silently passes a naive guard). A `radius <= 0` request is essentially always a caller bug, not a legitimate "zero-radius" query — the raycast's `maxDistance` guard set this precedent already. |
| D5 | A small dedicated-key demo ships alongside the World-side query, mirroring the raycast's `Key.F` (D6, session 40) — `Key.G`, wired once in `AppHost.cs` at the same switch as `Key.F`, calling `world.OverlapSphere(camera.Position, 10f, results, GameWorld.AllLayers)` and logging the count + entities found. Free key (verified: `G` is not bound anywhere in `AppHost.cs`/`Sandbox`/`Agapanthe.Platform.App`). Demo only, no new gameplay system — same posture as `Key.F`. |

## 3. Architecture

### 3.1 `Agapanthe.World` — the result type and the query

- **`OverlapHit`** (new, `src/Agapanthe.World/OverlapHit.cs`): `public readonly record struct
  OverlapHit(EntityRef Entity, double Distance)` — plain, no Arch type, mirrors `RaycastHit`'s
  shape and doc-comment conventions (minus `Point`, per D2).
- **`GameWorld.Queries.cs`** (existing partial, sibling to `.Physics.cs`) gains:
  ```csharp
  public int OverlapSphere(Double3 center, float radius, Span<OverlapHit> results, uint layerMask = AllLayers)
  ```
  Standard guards: `ObjectDisposedException.ThrowIf`, `AssertOwnerThread()`, `ValidateRadius`
  (new, mirrors `ValidateMaxDistance`'s shape exactly — see D4).

  **Reuses unmodified**: `GatherCandidates(layerMask)` (gathers every drawable's `GlobalId`/
  world-space `Bounds` sphere into `_qGid`/`_qCenter`/`_qRadius` scratch, honouring `QueryLayer`)
  and `BuildGrid(count)` (buckets candidates into the reused `_qCellHead`/`_qCellNext` scratch,
  cell size `2 * maxCandidateRadius`, tracks the occupied-cell AABB in `_qMinCell*`/`_qMaxCell*`).
  Both were written generically enough by the raycast milestone (session 40) to need zero changes
  here — the spec for that milestone explicitly called this out as the payoff of D4's "a shared
  private build routine is reusable, unmodified, by a future shape-query milestone."

  **New**: the walk itself. Unlike the raycast's DDA (Amanatides-Woo) march along a ray, a sphere
  overlap has no direction to march — it iterates every grid cell whose coordinate range falls
  within the query sphere's own AABB, **expanded by exactly one cell in every direction**:
  `floor((center - radius) / cellSize) - 1` through `floor((center + radius) / cellSize) + 1` per
  axis, INCLUSIVE. The expansion is required, not cosmetic: `BuildGrid` buckets each candidate
  into exactly one cell keyed by `floor(candidateCenter / cellSize)` (§3.1 — no candidate is ever
  registered in more than one bucket), and `cellSize = 2 * maxCandidateRadius` — so a candidate
  whose center falls just outside the naive `[center-radius, center+radius]` cell range can still
  geometrically overlap the query sphere via its own radius (exactly why the raycast's DDA walk
  tests a 3×3×3 neighbourhood around its current cell at every step, rather than the current cell
  alone). Additionally clamp the (already-expanded) range against the occupied-cell AABB from
  `BuildGrid` so an empty region outside all candidates is never walked (same audit-learned lesson
  as the raycast's `WalkHasLeftOccupiedBounds`, applied here as an upfront clamp on the iterated
  range instead of an early-exit mid-walk, since there is no "direction of travel" to bound
  against). **No `_qVisitedStamp`/`_qStamp` dedup is needed here** (unlike the raycast): this walk
  visits each distinct cell coordinate in the expanded range exactly once via straightforward
  nested loops — it is not a step-by-step march whose per-step 3×3×3 neighbourhood can overlap
  with a *previous* step's neighbourhood the way the raycast's DDA can. A candidate is therefore
  tested at most once by construction, with no dedup bookkeeping required.

  For each candidate in each visited cell, test sphere-sphere overlap:
  `Double3.Distance(center, candidateCenter) <= radius + candidateRadius` (inherits the exact
  same `Bounds`-proxy approximation the raycast's D2 already documented and accepted — "over-
  covers slightly, never under-covers", not mesh-accurate). **`OverlapHit.Distance` is the
  center-to-center distance** between the query center and the candidate's `Bounds` sphere center
  — not a surface-to-surface distance, and not adjusted for either sphere's radius (documented
  explicitly here and in `OverlapHit`'s own doc comment, so a caller doing falloff math knows
  which convention they're getting).

  Truncation (D3): once `results` is full, stop testing further candidates (mirrors the raycast
  session-40 audit fix — `RaycastAll`'s `goto Done` once `written == capacity`, avoiding paying
  for tests whose results would just be discarded). Sort the written prefix by distance using the
  same cached `private static readonly Comparison<OverlapHit>` pattern `CompareByDistance`
  already establishes for `RaycastHit` (a new, analogous `CompareOverlapsByDistance` — the two
  result types are unrelated structs, so the comparison delegate cannot be shared verbatim, but
  the *pattern* — a static field, never a per-call lambda — is identical).

- **`ValidateRadius(float radius)`** (new, private static, mirrors `ValidateMaxDistance`'s exact
  shape): throws `ArgumentOutOfRangeException` when `!(radius > 0f) || float.IsInfinity(radius)`.

### 3.2 `Agapanthe.App` — the demo hook

- `AppHost.cs`'s existing `window.KeyPressed` switch (where `Key.F`'s raycast demo already lives,
  `camera`/`world`/`window` already in scope) gains a `case Key.G:` calling
  `world.OverlapSphere(camera.Position, 10f, results, GameWorld.AllLayers)` against a
  stack-allocated `Span<OverlapHit> results = stackalloc OverlapHit[64]` (64 fixed — every
  existing pinned scene has well under 64 entities in total, so a real truncation here would only
  ever happen against a pathological scene; if it does truncate, that's visible in the logged
  count equalling exactly 64, not silently wrong), then logs the count and each hit
  entity+distance. No new `IWindow` surface needed (mirrors `Key.F`'s posture exactly).

## 4. Error handling

- `radius <= 0` or non-finite → `ArgumentOutOfRangeException` (D4).
- `results.Length == 0` → returns `0` immediately, same as `RaycastAll`'s existing early-out for
  an empty buffer (not an error — a caller legitimately probing "is anything here" with a
  zero-length buffer is a valid, if unusual, use).
- No entities in range → returns `0`, `results` untouched (matches `RaycastAll`'s miss behavior).
- Disposed `GameWorld` → `ObjectDisposedException` (standard gate, every `GameWorld` public
  method already has this).
- Called off the owning thread → the existing `AssertOwnerThread()` `[Conditional("DEBUG")]`
  guard (Debug-only, matches every other `GameWorld` mutator/query in this file — not escalated
  to a Release-mode throw, since this project's established convention for that guard is
  Debug-only detection of a threading bug during development, not a Release-mode safety net).

## 5. Testing strategy

New `tests/Agapanthe.Tests/GameWorldOverlapTests.cs`, mirroring `GameWorldRaycastTests.cs`'s
structure and precedent (`SurfaceContactsTests.cs`-style 0-alloc test):

- Simple overlap: a sphere containing exactly one candidate returns it with the correct distance.
- Multiple overlapping candidates, sorted-by-distance output verified explicitly (not just count).
- Buffer truncation: more candidates than `results.Length` — verify the returned count equals
  the buffer length and does not throw (mirrors `RaycastAll_TruncatesIntoAnUndersizedBuffer`).
- Layer-mask filtering: a tagged candidate (via `ImportedEntitySpec`'s `layer` ctor param) is
  excluded when the query's `layerMask` doesn't intersect it; an untagged sibling is always
  included (mirrors the raycast's `AllLayers`-default coverage).
- A miss: no candidate within radius → returns `0`.
- `radius <= 0`, `radius = NaN`, `radius = double.PositiveInfinity` (as a `float`, so
  `float.PositiveInfinity`) — each throws `ArgumentOutOfRangeException`.
- Tangent edge case: `distance == radius + candidateRadius` exactly — included (inclusive `<=`).
- **Boundary-margin case** (the correctness reason for the one-cell range expansion in §3.1): a
  candidate whose own center falls in a grid cell just *outside* the query sphere's naive
  (unexpanded) `[center-radius, center+radius]` cell range, but whose radius still geometrically
  reaches into the query sphere — must be found, not silently missed. Constructed by placing a
  candidate near a cell boundary with a radius large enough to overlap the query sphere from the
  adjacent cell.
- 0-alloc-after-warmup for `OverlapSphere` (same `GC.GetAllocatedBytesForCurrentThread()` delta
  pattern already used by `SurfaceContactsTests.cs`/`GameWorldRaycastTests.cs`).

**Live verification**: `Key.G` demo interaction in Sandbox, human visual/log sanity check (not a
new deterministic capture gate — same posture as `Key.F`, an interactive-only feature).

**Regression gate**: every existing pinned capture (9 total: 8 Sandbox scenes + `topdown`)
re-verified byte-identical — `OverlapSphere` is a pure query, never invoked from the render/tick
path, so it must not move a single pixel. Full `dotnet test` green, `dotnet build` 0 warnings,
AOT publish + JIT == NativeAOT re-confirmed (mirrors session 40's exact verification recipe).

**Double audit**: `csharp-lowlevel` + `engine-architect` — the project-standard pairing (this is
ECS/World/math work reusing already-audited infrastructure, not new Vulkan surface, so no
deviation to `graphics-3d` is needed — same reasoning session 40 used).

## 6. Migration path

Single wave, no format/version bump anticipated (no new component, no new `.agscene` field — this
is a pure runtime query over data the engine already tracks). If implementation surfaces an
unplanned need (mirroring session 40's own experience, where adding `QueryLayer` unexpectedly
forced a snapshot version bump), it will be handled the same way: discovered, fixed properly,
documented in the outcome section — not silently patched over.

1. `OverlapHit` + `ValidateRadius` + `OverlapSphere` (`GameWorld.Queries.cs`) — TDD: write
   `GameWorldOverlapTests.cs` red first.
2. `Key.G` demo wiring in `AppHost.cs`.
3. Full verification sweep (build, tests, 9 captures, AOT/JIT parity).
4. Double audit, apply findings.
5. Converge: docs updated, spec §7 outcome section, board archived.

## 7. Out of scope (deferred, not silently dropped)

- Box overlap ("what's within this box?") — a separate future backlog item (D1); a different
  shape representation (rotation, AABB vs. OBB) with no concrete driving use case yet.
- Mesh-accurate overlap testing — inherits the `Bounds` sphere-proxy approximation the raycast
  milestone (D2, session 40) already documented and accepted for exactly this reason.
- A persisted/incrementally-dirty-tracked broadphase — same reasoning as the raycast's D4: the
  per-call rebuild is deliberately the simpler, sufficient-for-now choice.
- Any new gameplay system consuming `OverlapSphere` beyond the `Key.G` demo.
- A distinct "N nearest" query mode with a true nearest-first guarantee under truncation — D3
  deliberately inherits `RaycastAll`'s existing, already-accepted truncation semantics instead.

## 8. Outcome (closed 2026-09-14, session 41)

Shipped as designed, D1-D5 all intact per SQ-005's self-review (verified against the actual code,
not assumed). 7-task/3-wave board, one genuine 🔴 found and fixed, plus a real pre-existing bug in
the *prior* milestone's already-shipped code, discovered only because this milestone's correctly-
guarded sibling gave both auditors something to compare it against.

**Reuse fidelity, confirmed literally**: `git diff` on `GameWorld.Queries.cs` shows `GatherCandidates`
and `BuildGrid` untouched — the raycast milestone's D4 ("a shared private build routine is reusable,
unmodified, by a future shape-query milestone") held exactly as designed.

**Verification**: 922 tests (917 → 922, +5 net), 0 warnings, 0 regressions. All 9 pinned captures
re-verified byte-identical via git-stash A/B against the `54747c898ed3e1a87bdad1b1fed5d06aa4e70499`
rollback point — every one landed on exactly the same hash already pinned from the immediately-prior
milestone, confirming both that this query is a true render no-op and that the repro parameters are
stable across two consecutive milestones. Sandbox JIT == NativeAOT confirmed both before and after
the audit fix pass (`9a010fc3…`, unchanged), 0 leak. Live human visual verdict **PASS** (2026-09-14):
`G` near an entity logs a plausible count + distance, `G` far from everything logs `0`.

**Double audit** — `csharp-lowlevel` (3.8/5, PASS-with-concerns) + `engine-architect` (4.0/5,
PASS-with-concerns), the project-standard pairing (no new Vulkan surface). Very strong convergence:
both reviewers independently found the same 🔴-class hazard and the same pre-existing bug in
already-shipped code.

**1 🔴 found and fixed** — `OverlapSphere`'s cell sweep had no cost bound relative to the *query*
itself: `cellSize = 2 × maxCandidateRadius` is dictated by the largest object in the whole world, not
by the query's own radius, so a perfectly natural "what's within 1 km" call over a world of small,
dense objects could sweep a cell-box volume in the billions — reachable from the public API with a
wholly legitimate finite `radius` (`ValidateRadius` never rejected it). Fixed by estimating the swept
cell-box volume in `double` *before* ever computing the `long` cell bounds, and falling back to a
direct linear scan over the already-gathered flat candidate array whenever the grid would visit at
least as many cells as there are candidates in the world — correct by construction (no grid, no
hashing, no aliasing) and never more expensive than the grid path it replaces. This single fix also
closed a separate `long`-overflow-degrades-to-silent-empty-result finding as a side effect, since the
fallback pre-empts the overflow-prone arithmetic entirely for any radius extreme enough to trigger it.

**A genuine pre-existing bug found in already-shipped code** — `RaycastAll` (committed as part of
session 40's `54747c8`, already double-audited and closed) checked its buffer capacity only *after*
each grid cell's candidate chain finished, not inside the chain's own loop condition. Since
`cellSize = 2 × maxRadius` makes several candidates sharing one cell routine rather than exceptional,
a single cell's chain longer than the caller's remaining buffer capacity could write past
`results[capacity - 1]` and throw `IndexOutOfRangeException` — directly contradicting that method's
own documented contract ("never throws for an undersized buffer"). Both auditors found this
independently, by the identical method: noticing `OverlapSphere` guarded its own chain loop with
`written < capacity` in the loop condition, and asking why `RaycastAll` didn't. Fixed by mirroring the
correct pattern back onto `RaycastAll`, with a new regression test (three candidates forced into one
cell, undersized buffer, must not throw).

**Other findings fixed** (all 🟠): `OverlapSphere`'s claim that "a candidate is tested at most once by
construction" was true of cell *coordinates* but not of `QueryCellHash`'s *hash* — two distinct
coordinates can alias to the same dictionary bucket, so a candidate reachable from both could be
tested and written twice; restored the `_qVisitedStamp`/`_qStamp` dedup mechanism the raycast already
established for exactly this reason · a non-finite query `center` (NaN/±Infinity) previously produced
a silent, indistinguishable-from-a-miss `0` result instead of a clear error — added `ValidateCenter`,
mirroring `ValidateDirection`'s already-established posture · the overlap threshold
(`radius + candidateRadius`) was summed in bare `float` before comparison against the exact `double`
distance, the same float-narrowing defect class session 40 had already fixed once on
`RaycastHit.Point` — now widened to `double` before the addition · equal-distance results from both
`RaycastAll` and `OverlapSphere` were ordered by `Span<T>.Sort`'s unstable introsort alone, meaning
ties fell back to non-deterministic grid-walk/Arch-archetype order — the exact bug *class* session 40
had already fixed once for `TryRaycast`'s single-hit tie-break, but never carried over to the
sort-based `*All` siblings; added a `GlobalId` secondary key to both comparers, since this project's
entire §4quater direction depends on an authoritative-server model where query output order must be
reproducible.

**Deferred as accepted debt** (documented, not fixed — no current consumer needs any of these, and
changing them now would be speculative API churn against an already-approved D2): `OverlapHit`
carries no `Double3 Center`/`float Radius`, so a caller cannot derive a direction to the hit or a
surface-adjusted distance from `Distance` alone — the most likely thing to force a shape change on
this API the first time a real gameplay consumer appears · `Double3.Distance`'s square root is paid
on every rejected candidate, where a squared-distance pre-filter (already used by the unrelated
`QuerySurfaceContacts`) would be cheaper — a micro-optimization, not a correctness issue · the engine
now has two independently-evolving region-query code paths (`OverlapSphere` here; `GameWorld.Physics.cs`'s
`QuerySurfaceContacts` for the landing-challenge gameplay rule) with different candidate sets and
distance conventions — noted as worth a line of awareness for whoever touches either next, not merged
(the two genuinely serve different needs today).
