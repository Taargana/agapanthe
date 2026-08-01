using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free tests for VS-3's generic surface/zone query <see cref="GameWorld.QuerySurfaceContacts"/>: the spatial
/// aggregation the landing-challenge rule composes. Attractor at the origin, surface radius 100, band 5, target zone
/// a 15 m disc around the surface point (0,100,0).
/// </summary>
[Collection("World")]
public sealed class SurfaceContactsTests
{
    private const double C_SurfaceRadius = 100.0;
    private const double C_Band = 5.0;                       // on-surface cut = 105
    private static readonly Double3 C_Zone = new(0, 100, 0); // a point ON the surface
    private const double C_ZoneRadius = 15.0;

    private static void SpawnAt(GameWorld world, Double3 position, float radius = 1f)
    {
        var spec = new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), position, Matrix4x4.Identity, Vector3.Zero, radius, 0);
        world.SpawnBody(in spec, Vector3.Zero, inverseMass: 1f, restitution: 0.3f, radius: radius);
    }

    private static LandingCounts Query(GameWorld world)
        => world.QuerySurfaceContacts(Double3.Zero, C_SurfaceRadius, C_Band, C_Zone, C_ZoneRadius);

    [Fact]
    public void OnSurfaceInsideZone_CountsInZone_NotAirborne()
    {
        using var world = new GameWorld();
        SpawnAt(world, new Double3(0, 101, 0)); // rests r=1 above the surface point → altitude 100, dist to zone 1
        var c = Query(world);
        Assert.Equal(1, c.Total);
        Assert.Equal(0, c.Airborne);
        Assert.Equal(1, c.InZone);
    }

    [Fact]
    public void OnSurfaceOutsideZone_NotInZone_NotAirborne()
    {
        using var world = new GameWorld();
        SpawnAt(world, new Double3(101, 0, 0)); // on the surface (altitude 100) but far from the zone point
        var c = Query(world);
        Assert.Equal(1, c.Total);
        Assert.Equal(0, c.Airborne);
        Assert.Equal(0, c.InZone);
    }

    [Fact]
    public void HighAboveZone_IsAirborne_NotInZone()
    {
        using var world = new GameWorld();
        SpawnAt(world, new Double3(0, 200, 0)); // altitude 199 ≫ 105 → airborne, even though it is above the zone
        var c = Query(world);
        Assert.Equal(1, c.Total);
        Assert.Equal(1, c.Airborne);
        Assert.Equal(0, c.InZone);
    }

    [Fact]
    public void JustAboveTheBand_IsAirborne_JustBelow_IsOnSurface()
    {
        using var world = new GameWorld();
        SpawnAt(world, new Double3(0, 107, 0)); // altitude 106 > 105 → airborne
        SpawnAt(world, new Double3(0, 105, 0)); // altitude 104 ≤ 105 → on surface, in zone (dist 5)
        var c = Query(world);
        Assert.Equal(2, c.Total);
        Assert.Equal(1, c.Airborne);
        Assert.Equal(1, c.InZone);
    }

    [Fact]
    public void MixedScene_CountsEachBucket()
    {
        using var world = new GameWorld();
        SpawnAt(world, new Double3(0, 101, 0));   // in zone
        SpawnAt(world, new Double3(3, 101, 2));   // on surface, dist √13 ≈ 3.6 < 15 → in zone
        SpawnAt(world, new Double3(101, 0, 0));   // on surface, out of zone
        SpawnAt(world, new Double3(0, 300, 0));   // airborne
        var c = Query(world);
        Assert.Equal(4, c.Total);
        Assert.Equal(1, c.Airborne);
        Assert.Equal(2, c.InZone);
    }

    [Fact]
    public void EmptyWorld_AllZero()
    {
        using var world = new GameWorld();
        Assert.Equal(new LandingCounts(0, 0, 0), Query(world));
    }

    [Fact]
    public void QuerySurfaceContacts_AllocatesNothingAfterWarmup()
    {
        using var world = new GameWorld();
        SpawnAt(world, new Double3(0, 101, 0));
        SpawnAt(world, new Double3(0, 300, 0));
        _ = Query(world); // warm up JIT + any first-touch

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = Query(world);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"QuerySurfaceContacts should allocate nothing, observed {allocated} bytes.");
    }
}
