using System.Numerics;
using Agapanthe.Core;
using Arch.Core;
using Arch.Core.Extensions;

namespace Agapanthe.World;

// Physics queries (spec docs/plans/2026-09-14-physics-queries-raycast-design.md): a general raycast against every
// drawable, not just physics bodies (D2), with an optional layer mask (D3). Lives in its own partial, sibling to
// GameWorld.Physics.cs, same file-per-concern convention. The broadphase here is a NEW, separate uniform grid —
// distinct scratch from GameWorld.Physics.cs's _cellHead/_cellNext, which StepPhysics owns for a different entity
// set (RigidBody only) and is mid-use during its own step; aliasing the two would corrupt whichever runs second.
//
// TryRaycast and RaycastAll duplicate the same grid-walk shape (gather -> build grid -> DDA -> test neighbourhood)
// rather than sharing it behind an Action<> callback: a captured lambda would allocate a closure on every call,
// breaking the 0-alloc-after-warmup gate every other GameWorld query in this file honours.
public sealed partial class GameWorld
{
    /// <summary>The default layer mask: every entity, tagged or not, is included.</summary>
    public const uint AllLayers = uint.MaxValue;

    // Every drawable carries these (MaterialiseDrawable/MaterialiseBody) — GlobalId is universal today (P3-M2 D2)
    // so it is Bounds, not GlobalId, that actually discriminates "is a drawable" here (mirrors DrawableDesc's own
    // comment above). WorldTransform is included (unlike the spec's literal query text) so the world-sphere centre
    // can honour the entity's rotation/scale via the same WorldSphere helper CollectRenderLists already uses —
    // omitting it would silently mis-place the hit test for any entity whose Bounds.Center is off-origin.
    private static readonly QueryDescription RaycastDesc =
        new QueryDescription().WithAll<GlobalId, WorldPosition, WorldTransform, Bounds>();

    // --- Reused raycast scratch (grown on demand, never re-allocated per call) -----------------------------------
    private ulong[] _qGid = Array.Empty<ulong>();
    private Double3[] _qCenter = Array.Empty<Double3>();
    private float[] _qRadius = Array.Empty<float>();
    private int[] _qCellNext = Array.Empty<int>();
    private int[] _qVisitedStamp = Array.Empty<int>();
    private int _qStamp;
    private readonly Dictionary<long, int> _qCellHead = new();

    // Audit fix (csharp-lowlevel F2/F3, engine-architect F2/F3/F12): the occupied-cell AABB, tracked while
    // BuildGrid buckets each candidate — bounds the DDA walk to where content actually is, instead of walking
    // every empty cell out to maxDistance regardless of what (if anything) the world contains there. This
    // replaces the old _qCellX/_qCellY/_qCellZ scratch arrays, which were written per-candidate and never read
    // back (dead weight) — the min/max here is the only thing that bookkeeping was ever good for.
    private long _qMinCellX, _qMaxCellX, _qMinCellY, _qMaxCellY, _qMinCellZ, _qMaxCellZ;

    private static readonly Comparison<RaycastHit> CompareByDistance = CompareHitsByDistance;

    private static int CompareHitsByDistance(RaycastHit a, RaycastHit b) => a.Distance.CompareTo(b.Distance);

    /// <summary>
    /// Casts <paramref name="ray"/> against every drawable (D2) and returns the nearest hit within
    /// <c>[0, maxDistance]</c> whose effective layer mask intersects <paramref name="layerMask"/> (D3).
    /// </summary>
    public bool TryRaycast(in Ray ray, double maxDistance, out RaycastHit hit, uint layerMask = AllLayers)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        ValidateMaxDistance(maxDistance);
        ValidateDirection(ray.Direction);

        hit = default;
        var count = GatherCandidates(layerMask);
        if (count == 0)
        {
            return false;
        }

        var cellSize = BuildGrid(count);
        EnsureStampCapacity(count);
        unchecked
        {
            _qStamp++;
        }

        var found = false;
        var bestDistance = maxDistance;

        InitWalk(in ray, cellSize, out var cx, out var cy, out var cz, out var stepX, out var stepY, out var stepZ,
            out var tMaxX, out var tMaxY, out var tMaxZ, out var tDeltaX, out var tDeltaY, out var tDeltaZ);

        var t = 0.0;
        // Audit fix (csharp-lowlevel F2): bound the loop by bestDistance, not the original maxDistance —
        // bestDistance only ever shrinks as a closer hit is found, so once the nearest-so-far is known, no
        // cell further along the ray can possibly contain anything closer. The common "found the nearest
        // thing, stop" case now terminates the walk immediately instead of continuing to maxDistance.
        while (t <= bestDistance)
        {
            if (WalkHasLeftOccupiedBounds(cx, cy, cz, stepX, stepY, stepZ))
            {
                break;
            }

            for (var dz = -1L; dz <= 1L; dz++)
            {
                for (var dy = -1L; dy <= 1L; dy++)
                {
                    for (var dxo = -1L; dxo <= 1L; dxo++)
                    {
                        var key = QueryCellHash(cx + dxo, cy + dy, cz + dz);
                        if (!_qCellHead.TryGetValue(key, out var k))
                        {
                            continue;
                        }

                        for (; k != -1; k = _qCellNext[k])
                        {
                            if (_qVisitedStamp[k] == _qStamp)
                            {
                                continue;
                            }

                            _qVisitedStamp[k] = _qStamp;
                            if (RaySphereIntersect.TryIntersectSphere(in ray, _qCenter[k], _qRadius[k], bestDistance, out var distance)
                                // Audit fix (csharp-lowlevel F5/F19): strict '<', not '<=' — first-encountered-
                                // at-a-given-distance wins (walk order is deterministic per grid layout), rather
                                // than the last one visited winning under the old '<=' (which depended on Arch's
                                // archetype/chunk iteration order — e.g. a ground plane whose Bounds sphere
                                // contains the camera in `drive`/`topdown` could report a different entity from
                                // run to run at distance 0). Using `!found` for the very first candidate avoids
                                // an edge case where a legitimate hit at exactly maxDistance would otherwise be
                                // rejected by a bare strict '<' against the initial bestDistance = maxDistance.
                                && (!found || distance < bestDistance))
                            {
                                found = true;
                                bestDistance = distance;
                                var point = ray.Origin + new Double3(ray.Direction) * distance;
                                hit = new RaycastHit(new EntityRef(_qGid[k]), distance, point);
                            }
                        }
                    }
                }
            }

            if (!AdvanceWalk(ref cx, ref cy, ref cz, stepX, stepY, stepZ,
                    ref tMaxX, ref tMaxY, ref tMaxZ, tDeltaX, tDeltaY, tDeltaZ, out t))
            {
                break;
            }
        }

        return found;
    }

    /// <summary>
    /// Casts <paramref name="ray"/> against every drawable and writes every hit within <c>[0, maxDistance]</c>
    /// whose effective layer mask intersects <paramref name="layerMask"/> into <paramref name="results"/>, sorted
    /// nearest-first, truncating silently if there are more hits than <paramref name="results"/> can hold (never
    /// throws for an undersized buffer) — note truncation keeps the first <c>results.Length</c> hits encountered
    /// in DDA walk order (cell-granularity near-first, not a guaranteed globally-nearest-N), then sorts only
    /// those. Returns the number of hits written.
    /// </summary>
    public int RaycastAll(in Ray ray, double maxDistance, Span<RaycastHit> results, uint layerMask = AllLayers)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        ValidateMaxDistance(maxDistance);
        ValidateDirection(ray.Direction);

        var count = GatherCandidates(layerMask);
        if (count == 0 || results.Length == 0)
        {
            return 0;
        }

        var cellSize = BuildGrid(count);
        EnsureStampCapacity(count);
        unchecked
        {
            _qStamp++;
        }

        var written = 0;
        var capacity = results.Length;

        InitWalk(in ray, cellSize, out var cx, out var cy, out var cz, out var stepX, out var stepY, out var stepZ,
            out var tMaxX, out var tMaxY, out var tMaxZ, out var tDeltaX, out var tDeltaY, out var tDeltaZ);

        var t = 0.0;
        while (t <= maxDistance)
        {
            if (WalkHasLeftOccupiedBounds(cx, cy, cz, stepX, stepY, stepZ))
            {
                break;
            }

            for (var dz = -1L; dz <= 1L; dz++)
            {
                for (var dy = -1L; dy <= 1L; dy++)
                {
                    for (var dxo = -1L; dxo <= 1L; dxo++)
                    {
                        var key = QueryCellHash(cx + dxo, cy + dy, cz + dz);
                        if (!_qCellHead.TryGetValue(key, out var k))
                        {
                            continue;
                        }

                        for (; k != -1; k = _qCellNext[k])
                        {
                            if (_qVisitedStamp[k] == _qStamp)
                            {
                                continue;
                            }

                            _qVisitedStamp[k] = _qStamp;
                            if (RaySphereIntersect.TryIntersectSphere(in ray, _qCenter[k], _qRadius[k], maxDistance, out var distance))
                            {
                                var point = ray.Origin + new Double3(ray.Direction) * distance;
                                results[written] = new RaycastHit(new EntityRef(_qGid[k]), distance, point);
                                written++;
                            }
                        }

                        // Audit fix (engine-architect F17): once the caller's buffer is full, stop testing —
                        // everything remaining would just be discarded by the old `written < capacity &&`
                        // short-circuit while still paying for the sphere test and chunk indirection.
                        if (written == capacity)
                        {
                            goto Done;
                        }
                    }
                }
            }

            if (!AdvanceWalk(ref cx, ref cy, ref cz, stepX, stepY, stepZ,
                    ref tMaxX, ref tMaxY, ref tMaxZ, tDeltaX, tDeltaY, tDeltaZ, out t))
            {
                break;
            }
        }

        Done:
        results[..written].Sort(CompareByDistance);
        return written;
    }

    private static void ValidateMaxDistance(double maxDistance)
    {
        // Audit fix (csharp-lowlevel F1, BLOCKING): +Infinity previously slipped through this guard (it is
        // neither NaN nor <= 0) and made the DDA walk's `while (t <= maxDistance)` loop unbounded — a genuine
        // hang reachable from the public API by any caller passing "as far as possible" literally.
        if (!(maxDistance > 0.0) || double.IsInfinity(maxDistance))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDistance), maxDistance, "maxDistance must be a positive, finite value.");
        }
    }

    // Audit fix (csharp-lowlevel F4): a zero-length or non-finite direction previously produced either a raw,
    // undocumented ArithmeticException from Math.Sign(NaN) inside InitWalk, or a `true` result carrying
    // distance = NaN out of RaySphereIntersect (which then poisoned bestDistance for the rest of the walk).
    // The spec's originally-planned Debug.Assert-only guard would not have caught this in Release; a real
    // audit-found hazard gets a real ArgumentException at the public boundary instead.
    private static void ValidateDirection(Vector3 direction)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z))
        {
            throw new ArgumentException("ray.Direction must be finite.", "ray");
        }

        if (direction == Vector3.Zero)
        {
            throw new ArgumentException("ray.Direction must be non-zero.", "ray");
        }
    }

    // Step 1 (spec §3.2): ONE query over every drawable, with an inline per-entity Has<QueryLayer>() check — the
    // real precedent (NoShadowCast, GameWorld.cs) for an optional per-entity tag in this codebase, not a two-query
    // WithNone<T>() split (no such split exists anywhere here). Gathers matching entities into reused scratch.
    private int GatherCandidates(uint layerMask)
    {
        var count = 0;
        foreach (ref var chunk in _world.Query(in RaycastDesc))
        {
            var ids = chunk.GetSpan<GlobalId>();
            var positions = chunk.GetSpan<WorldPosition>();
            var transforms = chunk.GetSpan<WorldTransform>();
            var bounds = chunk.GetSpan<Bounds>();
            var entities = chunk.Entities;
            var n = chunk.Count;
            for (var i = 0; i < n; i++)
            {
                var mask = entities[i].Has<QueryLayer>() ? entities[i].Get<QueryLayer>().Mask : AllLayers;
                if ((mask & layerMask) == 0)
                {
                    continue;
                }

                EnsureQueryCapacity(count + 1);
                WorldSphere(transforms[i].Value, positions[i].Value, bounds[i], out var center, out var radius);
                _qGid[count] = ids[i].Value;
                _qCenter[count] = center;
                _qRadius[count] = radius;
                count++;
            }
        }

        return count;
    }

    // Step 2 (spec §3.2, D4): a NEW uniform grid over the gathered candidates — cell size = 2 * max radius present,
    // same reasoning as GameWorld.Physics.cs's ResolveBodyContacts — rebuilt fresh every call, reused scratch.
    // Also tracks the occupied-cell AABB (_qMin/MaxCell*) so the DDA walk can stop once it has permanently left
    // the region that could possibly contain a candidate (audit fix, see WalkHasLeftOccupiedBounds).
    private double BuildGrid(int count)
    {
        var maxRadius = 0f;
        for (var k = 0; k < count; k++)
        {
            if (_qRadius[k] > maxRadius)
            {
                maxRadius = _qRadius[k];
            }
        }

        var cellSize = maxRadius > 0f ? 2.0 * maxRadius : 1.0;
        var invCell = 1.0 / cellSize;

        _qCellHead.EnsureCapacity(count);
        _qCellHead.Clear();

        _qMinCellX = _qMinCellY = _qMinCellZ = long.MaxValue;
        _qMaxCellX = _qMaxCellY = _qMaxCellZ = long.MinValue;

        for (var k = 0; k < count; k++)
        {
            var cx = (long)Math.Floor(_qCenter[k].X * invCell);
            var cy = (long)Math.Floor(_qCenter[k].Y * invCell);
            var cz = (long)Math.Floor(_qCenter[k].Z * invCell);

            if (cx < _qMinCellX) _qMinCellX = cx;
            if (cx > _qMaxCellX) _qMaxCellX = cx;
            if (cy < _qMinCellY) _qMinCellY = cy;
            if (cy > _qMaxCellY) _qMaxCellY = cy;
            if (cz < _qMinCellZ) _qMinCellZ = cz;
            if (cz > _qMaxCellZ) _qMaxCellZ = cz;

            var key = QueryCellHash(cx, cy, cz);
            _qCellNext[k] = _qCellHead.TryGetValue(key, out var head) ? head : -1;
            _qCellHead[key] = k;
        }

        return cellSize;
    }

    // Audit fix (csharp-lowlevel F2, engine-architect F2/F3): the walk visits a 3x3x3 neighbourhood around
    // (cx,cy,cz), so a cell is only unreachable once the *current* coordinate has moved one full cell past the
    // occupied AABB's edge, in the direction of travel, on every axis whose step is nonzero. An axis whose step
    // is zero never changes again — if it started outside the (expanded-by-one) AABB range on that axis, no
    // future cell can ever intersect the box regardless of the other two axes.
    private bool WalkHasLeftOccupiedBounds(long cx, long cy, long cz, int stepX, int stepY, int stepZ)
        => HasEscapedAxis(cx, stepX, _qMinCellX - 1, _qMaxCellX + 1)
           || HasEscapedAxis(cy, stepY, _qMinCellY - 1, _qMaxCellY + 1)
           || HasEscapedAxis(cz, stepZ, _qMinCellZ - 1, _qMaxCellZ + 1);

    private static bool HasEscapedAxis(long coordinate, int step, long lo, long hi)
    {
        if (step > 0)
        {
            return coordinate > hi;
        }

        if (step < 0)
        {
            return coordinate < lo;
        }

        // Never moves on this axis: either always in range, or permanently out of range.
        return coordinate < lo || coordinate > hi;
    }

    // 3D grid traversal setup (Amanatides-Woo), bounded by maxDistance. All t values are expressed in WORLD
    // distance along the ray — valid because Ray.Direction is contractually unit-length (Ray's own doc comment),
    // so distance-along-ray and the parametric t coincide. Split into Init/Advance (rather than an
    // IEnumerable-shaped walker) so neither TryRaycast nor RaycastAll needs a closure to consume it.
    private static void InitWalk(
        in Ray ray, double cellSize,
        out long cx, out long cy, out long cz,
        out int stepX, out int stepY, out int stepZ,
        out double tMaxX, out double tMaxY, out double tMaxZ,
        out double tDeltaX, out double tDeltaY, out double tDeltaZ)
    {
        double ox = ray.Origin.X, oy = ray.Origin.Y, oz = ray.Origin.Z;
        double dx = ray.Direction.X, dy = ray.Direction.Y, dz = ray.Direction.Z;

        cx = (long)Math.Floor(ox / cellSize);
        cy = (long)Math.Floor(oy / cellSize);
        cz = (long)Math.Floor(oz / cellSize);

        stepX = Math.Sign(dx);
        stepY = Math.Sign(dy);
        stepZ = Math.Sign(dz);

        tMaxX = NextBoundary(ox, dx, cx, stepX, cellSize);
        tMaxY = NextBoundary(oy, dy, cy, stepY, cellSize);
        tMaxZ = NextBoundary(oz, dz, cz, stepZ, cellSize);

        tDeltaX = dx != 0.0 ? cellSize / Math.Abs(dx) : double.PositiveInfinity;
        tDeltaY = dy != 0.0 ? cellSize / Math.Abs(dy) : double.PositiveInfinity;
        tDeltaZ = dz != 0.0 ? cellSize / Math.Abs(dz) : double.PositiveInfinity;
    }

    private static double NextBoundary(double origin, double direction, long cell, int step, double cellSize)
    {
        if (step > 0)
        {
            return ((cell + 1) * cellSize - origin) / direction;
        }

        if (step < 0)
        {
            return (cell * cellSize - origin) / direction;
        }

        return double.PositiveInfinity;
    }

    private static bool AdvanceWalk(
        ref long cx, ref long cy, ref long cz,
        int stepX, int stepY, int stepZ,
        ref double tMaxX, ref double tMaxY, ref double tMaxZ,
        double tDeltaX, double tDeltaY, double tDeltaZ,
        out double t)
    {
        if (stepX == 0 && stepY == 0 && stepZ == 0)
        {
            t = double.PositiveInfinity;
            return false;
        }

        if (tMaxX < tMaxY && tMaxX < tMaxZ)
        {
            cx += stepX;
            t = tMaxX;
            tMaxX += tDeltaX;
        }
        else if (tMaxY < tMaxZ)
        {
            cy += stepY;
            t = tMaxY;
            tMaxY += tDeltaY;
        }
        else
        {
            cz += stepZ;
            t = tMaxZ;
            tMaxZ += tDeltaZ;
        }

        return true;
    }

    private static long QueryCellHash(long cx, long cy, long cz)
        => (cx * 73856093L) ^ (cy * 19349663L) ^ (cz * 83492791L);

    private void EnsureQueryCapacity(int needed)
    {
        if (_qGid.Length >= needed)
        {
            return;
        }

        var cap = Math.Max(needed, Math.Max(16, _qGid.Length * 2));
        Array.Resize(ref _qGid, cap);
        Array.Resize(ref _qCenter, cap);
        Array.Resize(ref _qRadius, cap);
        Array.Resize(ref _qCellNext, cap);
    }

    private void EnsureStampCapacity(int count)
    {
        if (_qVisitedStamp.Length >= count)
        {
            return;
        }

        // Doubling growth (mirrors EnsureQueryCapacity above) — a world that grows by one entity at a time
        // (e.g. planet-drop/planet-challenge dropping probes one per keypress) must not reallocate this array on
        // every single raycast call; Array.Resize preserves prior stamps and zero-fills the new tail, which stays
        // correct since _qStamp never revisits 0 after the first call.
        var cap = Math.Max(count, Math.Max(16, _qVisitedStamp.Length * 2));
        Array.Resize(ref _qVisitedStamp, cap);
    }
}
