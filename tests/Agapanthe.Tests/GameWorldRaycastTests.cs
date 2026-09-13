using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// Physics queries — <see cref="GameWorld.TryRaycast"/>/<see cref="GameWorld.RaycastAll"/>: the general raycast
/// against every drawable (spec `docs/plans/2026-09-14-physics-queries-raycast-design.md` §5), mirroring
/// <see cref="SurfaceContactsTests"/>'s 0-alloc-after-warmup convention.
/// </summary>
[Collection("World")]
public sealed class GameWorldRaycastTests
{
    private static ImportedEntitySpec Sphere(Double3 position, float radius, uint? layer = null)
        => new(new MeshHandle(0, 1), new MaterialHandle(0, 1), position, Matrix4x4.Identity, Vector3.Zero, radius, 0,
            layer: layer);

    private static void Spawn(GameWorld world, Double3 position, float radius, uint? layer = null)
        => world.SpawnImported(Sphere(position, radius, layer));

    [Fact]
    public void TryRaycast_HitsNearestOfTwoSpheresOnTheSameRay()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(0, 0, 10), 1f); // far
        Spawn(world, new Double3(0, 0, 5), 1f);  // near
        var ray = new Ray(Double3.Zero, Vector3.UnitZ);

        Assert.True(world.TryRaycast(in ray, 100.0, out var hit));
        Assert.Equal(4.0, hit.Distance, 3); // 5 - radius 1
    }

    [Fact]
    public void TryRaycast_Miss_RayPointsAway()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(0, 0, 10), 1f);
        var ray = new Ray(Double3.Zero, -Vector3.UnitZ);

        Assert.False(world.TryRaycast(in ray, 100.0, out _));
    }

    [Fact]
    public void TryRaycast_EmptyWorld_Misses()
    {
        using var world = new GameWorld();
        var ray = new Ray(Double3.Zero, Vector3.UnitZ);

        Assert.False(world.TryRaycast(in ray, 100.0, out _));
    }

    [Fact]
    public void RaycastAll_ReturnsEveryHit_SortedByDistance()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(0, 0, 10), 1f);
        Spawn(world, new Double3(0, 0, 5), 1f);
        Spawn(world, new Double3(0, 0, 20), 1f);
        var ray = new Ray(Double3.Zero, Vector3.UnitZ);

        Span<RaycastHit> results = stackalloc RaycastHit[8];
        var count = world.RaycastAll(in ray, 100.0, results, GameWorld.AllLayers);

        Assert.Equal(3, count);
        Assert.True(results[0].Distance < results[1].Distance);
        Assert.True(results[1].Distance < results[2].Distance);
    }

    [Fact]
    public void RaycastAll_TruncatesIntoAnUndersizedBuffer()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(0, 0, 10), 1f);
        Spawn(world, new Double3(0, 0, 5), 1f);
        Spawn(world, new Double3(0, 0, 20), 1f);
        var ray = new Ray(Double3.Zero, Vector3.UnitZ);

        Span<RaycastHit> results = stackalloc RaycastHit[2];
        var count = world.RaycastAll(in ray, 100.0, results);

        Assert.Equal(2, count); // truncated, never throws
    }

    [Fact]
    public void TryRaycast_LayerMask_ExcludesATaggedEntityNotInTheMask()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(0, 0, 5), 1f, layer: 0b0010u); // tagged, layer bit 1 only
        var ray = new Ray(Double3.Zero, Vector3.UnitZ);

        Assert.False(world.TryRaycast(in ray, 100.0, out _, layerMask: 0b0001u));
        Assert.True(world.TryRaycast(in ray, 100.0, out _, layerMask: 0b0010u));
    }

    [Fact]
    public void TryRaycast_LayerMask_UntaggedEntityIsHitByEveryMask()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(0, 0, 5), 1f); // no layer → AllLayers
        var ray = new Ray(Double3.Zero, Vector3.UnitZ);

        Assert.True(world.TryRaycast(in ray, 100.0, out _, layerMask: 0b0001u));
        Assert.True(world.TryRaycast(in ray, 100.0, out _, layerMask: 0x8000_0000u));
    }

    [Fact]
    public void TryRaycast_MaxDistanceLessThanOrEqualToZero_Throws()
    {
        using var world = new GameWorld();
        var ray = new Ray(Double3.Zero, Vector3.UnitZ);

        Assert.Throws<ArgumentOutOfRangeException>(() => world.TryRaycast(in ray, 0.0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.TryRaycast(in ray, -1.0, out _));
    }

    [Fact]
    public void RaycastAll_MaxDistanceLessThanOrEqualToZero_Throws()
    {
        using var world = new GameWorld();
        var ray = new Ray(Double3.Zero, Vector3.UnitZ);
        Span<RaycastHit> results = stackalloc RaycastHit[4];

        // Assert.Throws takes a delegate, and a Span (ref struct) cannot be captured in a lambda closure — call
        // directly and assert on the thrown exception instead.
        var threw = false;
        try
        {
            world.RaycastAll(in ray, 0.0, results);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        Assert.True(threw);
    }

    [Fact]
    public void TryRaycast_AllocatesNothingAfterWarmup()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(0, 0, 10), 1f);
        Spawn(world, new Double3(0, 0, 5), 1f);
        var ray = new Ray(Double3.Zero, Vector3.UnitZ);
        _ = world.TryRaycast(in ray, 100.0, out _); // warmup

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = world.TryRaycast(in ray, 100.0, out _);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"TryRaycast should allocate nothing, observed {allocated} bytes.");
    }

    [Fact]
    public void RaycastAll_AllocatesNothingAfterWarmup()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(0, 0, 10), 1f);
        Spawn(world, new Double3(0, 0, 5), 1f);
        Spawn(world, new Double3(0, 0, 20), 1f);
        var ray = new Ray(Double3.Zero, Vector3.UnitZ);
        Span<RaycastHit> results = new RaycastHit[8];
        _ = world.RaycastAll(in ray, 100.0, results); // warmup

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = world.RaycastAll(in ray, 100.0, results);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"RaycastAll should allocate nothing, observed {allocated} bytes.");
    }

    // --- Audit fixes (csharp-lowlevel F1/F2/F4/F10) -----------------------------------------------------------

    [Fact]
    public void TryRaycast_MaxDistanceInfinity_Throws()
    {
        // F1 (BLOCKING): +Infinity previously slipped past the "> 0" guard and made the DDA walk unbounded — a
        // real hang, since "cast as far as possible" is exactly what a caller would naturally pass Infinity for.
        using var world = new GameWorld();
        var ray = new Ray(Double3.Zero, Vector3.UnitZ);

        Assert.Throws<ArgumentOutOfRangeException>(() => world.TryRaycast(in ray, double.PositiveInfinity, out _));
    }

    [Fact]
    public void RaycastAll_MaxDistanceInfinity_Throws()
    {
        using var world = new GameWorld();
        var ray = new Ray(Double3.Zero, Vector3.UnitZ);
        Span<RaycastHit> results = stackalloc RaycastHit[4];

        var threw = false;
        try
        {
            world.RaycastAll(in ray, double.PositiveInfinity, results);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        Assert.True(threw);
    }

    [Fact]
    public void TryRaycast_ZeroDirection_Throws()
    {
        // F4: a zero-length direction drove `a = 0` in RaySphereIntersect, poisoning bestDistance with NaN, and
        // a NaN direction crashed with a raw ArithmeticException from Math.Sign inside the DDA setup. Both are
        // now rejected up front with a clear ArgumentException.
        using var world = new GameWorld();
        var ray = new Ray(Double3.Zero, Vector3.Zero);

        Assert.Throws<ArgumentException>(() => world.TryRaycast(in ray, 100.0, out _));
    }

    [Fact]
    public void TryRaycast_NaNDirection_Throws()
    {
        using var world = new GameWorld();
        var ray = new Ray(Double3.Zero, new Vector3(float.NaN, 0f, 0f));

        Assert.Throws<ArgumentException>(() => world.TryRaycast(in ray, 100.0, out _));
    }

    [Fact]
    public void RaycastAll_ZeroDirection_Throws()
    {
        using var world = new GameWorld();
        var ray = new Ray(Double3.Zero, Vector3.Zero);
        Span<RaycastHit> results = stackalloc RaycastHit[4];

        var threw = false;
        try
        {
            world.RaycastAll(in ray, 100.0, results);
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Assert.True(threw);
    }

    [Fact]
    public void TryRaycast_SparseWorld_HugeMaxDistance_CompletesQuickly()
    {
        // F2/F3 (perf): the DDA walk used to be O(maxDistance / cellSize) regardless of world content — an
        // empty world beyond a couple of meters with maxDistance = 1e6 walked ~500k cells x 27 neighbour
        // lookups. The walk is now bounded to the occupied-cell AABB; this world has one small sphere near the
        // origin and nothing else, so the fixed walk should return almost instantly even with a huge bound.
        using var world = new GameWorld();
        Spawn(world, new Double3(0, 0, 5), 1f);
        var missRay = new Ray(Double3.Zero, -Vector3.UnitZ); // points away — walks the full occupied-AABB miss path

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hit = world.TryRaycast(in missRay, 1_000_000.0, out _);
        sw.Stop();

        Assert.False(hit);
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"TryRaycast with a huge maxDistance over a sparse world took {sw.ElapsedMilliseconds} ms — the DDA walk may no longer be bounded to occupied cells.");
    }

    [Fact]
    public void TryRaycast_DiagonalRay_HitsOffAxisSphere()
    {
        // F10: every prior test used a ray along +-UnitZ with spheres on that same axis — a sign error or
        // off-by-one in the DDA's X/Y handling specifically would have passed the whole suite undetected.
        using var world = new GameWorld();
        var direction = Vector3.Normalize(new Vector3(1f, 1f, 1f));
        var center = new Double3(10, 10, 10); // squarely on the diagonal, off every cardinal axis
        Spawn(world, center, 1f);
        var ray = new Ray(Double3.Zero, direction);

        Assert.True(world.TryRaycast(in ray, 100.0, out var hit));
        Assert.Equal((10.0 * Math.Sqrt(3)) - 1.0, hit.Distance, 3);
    }

    [Fact]
    public void TryRaycast_NegativeDirection_HitsSphereBehindOrigin()
    {
        // F10: an actual hit while walking backward through the grid — not just a miss test, which would pass
        // even with a completely broken negative-step DDA branch.
        using var world = new GameWorld();
        Spawn(world, new Double3(0, 0, -8), 1f);
        var ray = new Ray(Double3.Zero, -Vector3.UnitZ);

        Assert.True(world.TryRaycast(in ray, 100.0, out var hit));
        Assert.Equal(7.0, hit.Distance, 3);
    }

    [Fact]
    public void TryRaycast_OffAxisSphere_XYOffsetFromTravelAxis()
    {
        // F10: sphere offset in X and Y, not merely along the ray's own Z travel axis.
        using var world = new GameWorld();
        Spawn(world, new Double3(3, -4, 20), 2f);
        var ray = new Ray(new Double3(3, -4, 0), Vector3.UnitZ);

        Assert.True(world.TryRaycast(in ray, 100.0, out var hit));
        Assert.Equal(18.0, hit.Distance, 3);
    }

    [Fact]
    public void TryRaycast_OriginOutsideOccupiedGrid_StillFindsTheHit()
    {
        // F10: the origin starts well outside the occupied-cell AABB (exercises the AABB-bounded walk not
        // accidentally rejecting a ray that hasn't reached the occupied region yet).
        using var world = new GameWorld();
        Spawn(world, new Double3(0, 0, 10), 1f);
        var ray = new Ray(new Double3(0, 0, -500), Vector3.UnitZ);

        Assert.True(world.TryRaycast(in ray, 1000.0, out var hit));
        Assert.Equal(509.0, hit.Distance, 3);
    }
}
