using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// Shape queries — <see cref="GameWorld.OverlapSphere"/>: "what is inside this sphere?" (spec
/// `docs/plans/2026-09-14-shape-queries-overlap-design.md` §5), reusing the physics-queries broadphase and
/// mirroring <see cref="GameWorldRaycastTests"/>'s/<see cref="SurfaceContactsTests"/>'s conventions.
/// </summary>
[Collection("World")]
public sealed class GameWorldOverlapTests
{
    private static ImportedEntitySpec Sphere(Double3 position, float radius, uint? layer = null)
        => new(new MeshHandle(0, 1), new MaterialHandle(0, 1), position, Matrix4x4.Identity, Vector3.Zero, radius, 0,
            layer: layer);

    private static void Spawn(GameWorld world, Double3 position, float radius, uint? layer = null)
        => world.SpawnImported(Sphere(position, radius, layer));

    // Audit fix (session 42, discovered while auditing OverlapBox against this sibling): OverlapSphere's own
    // cost-bound fallback (added by this file's own session-41 audit) means any test with only a handful of
    // candidates always takes the linear-scan branch, never the grid-walk path the boundary-margin test below
    // was written to pin. Padding with far-away, non-overlapping decoys (same radius, so cellSize is unchanged)
    // forces the grid branch to actually run.
    private static void SpawnDecoys(GameWorld world, int count, float radius = 1f)
    {
        for (var i = 0; i < count; i++)
        {
            Spawn(world, new Double3(100_000 + (i * 10.0), 0, 0), radius);
        }
    }

    [Fact]
    public void OverlapSphere_FindsACandidateWithinRange()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(5, 0, 0), 1f);

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var count = world.OverlapSphere(Double3.Zero, 10f, results);

        Assert.Equal(1, count);
        Assert.Equal(5.0, results[0].Distance, 3); // center-to-center, not adjusted for either radius
    }

    [Fact]
    public void OverlapSphere_Miss_NoCandidateInRange()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(100, 0, 0), 1f);

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var count = world.OverlapSphere(Double3.Zero, 10f, results);

        Assert.Equal(0, count);
    }

    [Fact]
    public void OverlapSphere_EmptyWorld_Misses()
    {
        using var world = new GameWorld();

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var count = world.OverlapSphere(Double3.Zero, 10f, results);

        Assert.Equal(0, count);
    }

    [Fact]
    public void OverlapSphere_MultipleCandidates_SortedByDistance()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(8, 0, 0), 1f);
        Spawn(world, new Double3(2, 0, 0), 1f);
        Spawn(world, new Double3(5, 0, 0), 1f);

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var count = world.OverlapSphere(Double3.Zero, 20f, results);

        Assert.Equal(3, count);
        Assert.True(results[0].Distance < results[1].Distance);
        Assert.True(results[1].Distance < results[2].Distance);
    }

    [Fact]
    public void OverlapSphere_TruncatesIntoAnUndersizedBuffer()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(2, 0, 0), 1f);
        Spawn(world, new Double3(5, 0, 0), 1f);
        Spawn(world, new Double3(8, 0, 0), 1f);

        Span<OverlapHit> results = stackalloc OverlapHit[2];
        var count = world.OverlapSphere(Double3.Zero, 20f, results);

        Assert.Equal(2, count); // truncated, never throws
    }

    [Fact]
    public void OverlapSphere_LayerMask_ExcludesATaggedEntityNotInTheMask()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(5, 0, 0), 1f, layer: 0b0010u); // tagged, layer bit 1 only

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        Assert.Equal(0, world.OverlapSphere(Double3.Zero, 10f, results, layerMask: 0b0001u));
        Assert.Equal(1, world.OverlapSphere(Double3.Zero, 10f, results, layerMask: 0b0010u));
    }

    [Fact]
    public void OverlapSphere_LayerMask_UntaggedEntityIsHitByEveryMask()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(5, 0, 0), 1f); // no layer → AllLayers

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        Assert.Equal(1, world.OverlapSphere(Double3.Zero, 10f, results, layerMask: 0b0001u));
        Assert.Equal(1, world.OverlapSphere(Double3.Zero, 10f, results, layerMask: 0x8000_0000u));
    }

    [Fact]
    public void OverlapSphere_TangentEdgeCase_IsIncluded()
    {
        // distance == radius + candidateRadius exactly — the inclusive "<=" overlap test must include it.
        using var world = new GameWorld();
        Spawn(world, new Double3(10, 0, 0), 5f); // distance 10, query radius 5 + candidate radius 5 = 10

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var count = world.OverlapSphere(Double3.Zero, 5f, results);

        Assert.Equal(1, count);
    }

    [Fact]
    public void OverlapSphere_BoundaryMargin_CandidateBucketedOutsideNaiveRangeStillFound()
    {
        // Spec §3.1's correctness fix: BuildGrid buckets a candidate by its CENTER alone (floor(center/cellSize)),
        // so a candidate whose center falls in the cell just past the query sphere's naive (unexpanded)
        // [center-radius, center+radius] cell range can still geometrically overlap it via its own radius. The
        // walk must expand its iterated cell range by one cell in every direction to find it.
        //
        // Setup (derived in the spec's review): candidate radius R=1, all candidates radius <= 1, so maxRadius=1
        // and cellSize=2*R=2. Query radius=1.5, center=0: naive AABB cell range along X is floor(-1.5/2)=-1 to
        // floor(1.5/2)=0. Candidate at X=2.2 is bucketed in cell floor(2.2/2)=1 — one cell past the naive upper
        // bound (0) — yet the true distance 2.2 <= queryRadius(1.5) + candidateRadius(1) = 2.5, a real overlap.
        // Without the one-cell margin expansion, this candidate is silently missed.
        //
        // Audit fix (session 42): this query's own cost estimate is span=4.5 (91.125 cells), so with only the
        // one real candidate the call would take the scan-fallback branch and never touch the margin expansion
        // at all — this test passed even with the margin deleted entirely until this fix (found while auditing
        // OverlapBox against this sibling). Padding with 100 decoys (radius 1, cellSize unchanged) pushes count
        // to 101 > 91.125, forcing the grid-walk branch.
        using var world = new GameWorld();
        Spawn(world, new Double3(2.2, 0, 0), 1f);
        SpawnDecoys(world, 100);

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var count = world.OverlapSphere(Double3.Zero, 1.5f, results);

        Assert.Equal(1, count);
        Assert.Equal(2.2, results[0].Distance, 3);
    }

    // Assert.Throws takes a delegate, and a Span (ref struct) cannot be captured in a lambda closure — call
    // directly and assert on the thrown exception instead (mirrors GameWorldRaycastTests' same pattern).
    [Fact]
    public void OverlapSphere_RadiusLessThanOrEqualToZero_Throws()
    {
        using var world = new GameWorld();
        Span<OverlapHit> results = stackalloc OverlapHit[4];

        var threwZero = false;
        try
        {
            world.OverlapSphere(Double3.Zero, 0f, results);
        }
        catch (ArgumentOutOfRangeException)
        {
            threwZero = true;
        }

        Assert.True(threwZero);

        var threwNegative = false;
        try
        {
            world.OverlapSphere(Double3.Zero, -1f, results);
        }
        catch (ArgumentOutOfRangeException)
        {
            threwNegative = true;
        }

        Assert.True(threwNegative);
    }

    [Fact]
    public void OverlapSphere_RadiusNaN_Throws()
    {
        using var world = new GameWorld();
        Span<OverlapHit> results = stackalloc OverlapHit[4];

        var threw = false;
        try
        {
            world.OverlapSphere(Double3.Zero, float.NaN, results);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        Assert.True(threw);
    }

    [Fact]
    public void OverlapSphere_RadiusInfinity_Throws()
    {
        using var world = new GameWorld();
        Span<OverlapHit> results = stackalloc OverlapHit[4];

        var threw = false;
        try
        {
            world.OverlapSphere(Double3.Zero, float.PositiveInfinity, results);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        Assert.True(threw);
    }

    // --- Audit fixes (session 41, csharp-lowlevel F1/F3, engine-architect F1/F4) -------------------------------

    [Fact]
    public void OverlapSphere_HugeRadiusOverSparseWorld_CompletesQuickly()
    {
        // F1 (BLOCKING): cellSize is dictated by the largest candidate in the world, not by the query's own
        // radius — a small candidate world plus a large query radius used to sweep a huge cell-box even though
        // almost none of it is occupied. Now falls back to a direct candidate scan once the estimated swept
        // cell count would meet or exceed the candidate count.
        //
        // Audit fix (session 42): the original version used a single candidate at the origin, so the occupied-
        // cell AABB was already a single cell regardless of the cost-bound check — clamping alone made this
        // cheap, and the test passed identically with the cost-bound check deleted (found while auditing
        // OverlapBox against this sibling). Candidates spread far enough apart that the occupied AABB itself is
        // huge genuinely require the cost-bound fallback, not just the clamp.
        using var world = new GameWorld();
        Spawn(world, new Double3(0, 0, 0), 1f);
        Spawn(world, new Double3(500_000_000, 0, 0), 1f);
        Spawn(world, new Double3(1_000_000_000, 0, 0), 1f);

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var count = world.OverlapSphere(new Double3(500_000_000, 0, 0), 500_001_000f, results);
        sw.Stop();

        Assert.Equal(3, count); // all three candidates genuinely fall inside this radius
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"OverlapSphere over widely-spread candidates took {sw.ElapsedMilliseconds} ms — the cost-bound fallback may no longer be triggering.");
    }

    [Fact]
    public void OverlapSphere_CenterNaN_Throws()
    {
        using var world = new GameWorld();
        Span<OverlapHit> results = stackalloc OverlapHit[4];

        var threw = false;
        try
        {
            world.OverlapSphere(new Double3(double.NaN, 0, 0), 1f, results);
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Assert.True(threw);
    }

    [Fact]
    public void OverlapSphere_CenterInfinity_Throws()
    {
        using var world = new GameWorld();
        Span<OverlapHit> results = stackalloc OverlapHit[4];

        var threw = false;
        try
        {
            world.OverlapSphere(new Double3(double.PositiveInfinity, 0, 0), 1f, results);
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Assert.True(threw);
    }

    [Fact]
    public void OverlapSphere_EqualDistanceCandidates_OrderIsDeterministicByEntity()
    {
        // engine-architect F4: Span<T>.Sort is an unstable introsort, so two candidates at the exact same
        // distance must be ordered by a secondary key (GlobalId), not by whatever order the grid walk or Arch's
        // archetype iteration happened to produce.
        using var world = new GameWorld();
        Spawn(world, new Double3(5, 0, 0), 1f);
        Spawn(world, new Double3(-5, 0, 0), 1f); // exactly the same distance from the origin

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var count1 = world.OverlapSphere(Double3.Zero, 10f, results);
        var order1 = new[] { results[0].Entity, results[1].Entity };

        // Repeat several times: the order must be identical every call, not just internally consistent once.
        for (var i = 0; i < 5; i++)
        {
            var count2 = world.OverlapSphere(Double3.Zero, 10f, results);
            Assert.Equal(count1, count2);
            Assert.Equal(order1[0], results[0].Entity);
            Assert.Equal(order1[1], results[1].Entity);
        }
    }

    [Fact]
    public void OverlapSphere_AllocatesNothingAfterWarmup()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(2, 0, 0), 1f);
        Spawn(world, new Double3(5, 0, 0), 1f);
        Spawn(world, new Double3(8, 0, 0), 1f);
        Span<OverlapHit> results = new OverlapHit[8];
        _ = world.OverlapSphere(Double3.Zero, 20f, results); // warmup

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = world.OverlapSphere(Double3.Zero, 20f, results);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"OverlapSphere should allocate nothing, observed {allocated} bytes.");
    }
}
