using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// Shape queries — <see cref="GameWorld.OverlapBox"/>: "what is inside this box?" (spec
/// `docs/plans/2026-09-14-shape-queries-box-overlap-design.md` §5), an AABB counterpart to
/// <see cref="GameWorld.OverlapSphere"/>, reusing the same broadphase and every audit lesson from session 41
/// applied from the start (cost-bound fallback, stamp dedup, loop-condition capacity check, deterministic sort).
/// </summary>
[Collection("World")]
public sealed class GameWorldBoxOverlapTests
{
    private static ImportedEntitySpec Sphere(Double3 position, float radius, uint? layer = null)
        => new(new MeshHandle(0, 1), new MaterialHandle(0, 1), position, Matrix4x4.Identity, Vector3.Zero, radius, 0,
            layer: layer);

    private static void Spawn(GameWorld world, Double3 position, float radius, uint? layer = null)
        => world.SpawnImported(Sphere(position, radius, layer));

    // Audit fix (session 42, csharp-lowlevel F1 BLOCKING / engine-architect F1): OverlapBox falls back to a
    // linear scan whenever the estimated swept cell count meets or exceeds the candidate count — and that
    // estimate is at LEAST 3x3x3=27 for any box (even a degenerate point). A test with only a handful of
    // candidates therefore always takes the scan branch and never exercises the grid-walk path (margin
    // expansion, stamp dedup, capacity-in-loop-condition) at all — proven by mutation in the audit: removing the
    // margin, the dedup, or the capacity guard left every prior test in this file green. Padding the world with
    // enough far-away, non-overlapping decoys (same radius, so cellSize is unchanged) pushes `count` above the
    // estimate and forces the grid branch to actually run.
    private static void SpawnDecoys(GameWorld world, int count, float radius = 1f)
    {
        for (var i = 0; i < count; i++)
        {
            Spawn(world, new Double3(100_000 + (i * 10.0), 0, 0), radius);
        }
    }

    [Fact]
    public void OverlapBox_FindsACandidateWithinRange()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(5, 0, 0), 1f);

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var count = world.OverlapBox(new Double3(-10, -10, -10), new Double3(10, 10, 10), results);

        Assert.Equal(1, count);
        Assert.Equal(5.0, results[0].Distance, 3); // box-center (0,0,0) to candidate-center
    }

    [Fact]
    public void OverlapBox_Miss_NoCandidateInRange()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(100, 0, 0), 1f);

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var count = world.OverlapBox(new Double3(-10, -10, -10), new Double3(10, 10, 10), results);

        Assert.Equal(0, count);
    }

    [Fact]
    public void OverlapBox_EmptyWorld_Misses()
    {
        using var world = new GameWorld();

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var count = world.OverlapBox(new Double3(-10, -10, -10), new Double3(10, 10, 10), results);

        Assert.Equal(0, count);
    }

    [Fact]
    public void OverlapBox_MultipleCandidates_SortedByDistance()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(8, 0, 0), 1f);
        Spawn(world, new Double3(2, 0, 0), 1f);
        Spawn(world, new Double3(5, 0, 0), 1f);

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var count = world.OverlapBox(new Double3(-20, -20, -20), new Double3(20, 20, 20), results);

        Assert.Equal(3, count);
        Assert.True(results[0].Distance < results[1].Distance);
        Assert.True(results[1].Distance < results[2].Distance);
    }

    [Fact]
    public void OverlapBox_TruncatesIntoAnUndersizedBuffer()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(2, 0, 0), 1f);
        Spawn(world, new Double3(5, 0, 0), 1f);
        Spawn(world, new Double3(8, 0, 0), 1f);

        Span<OverlapHit> results = stackalloc OverlapHit[2];
        var count = world.OverlapBox(new Double3(-20, -20, -20), new Double3(20, 20, 20), results);

        Assert.Equal(2, count); // truncated, never throws
    }

    [Fact]
    public void OverlapBox_LayerMask_ExcludesATaggedEntityNotInTheMask()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(5, 0, 0), 1f, layer: 0b0010u); // tagged, layer bit 1 only

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        Assert.Equal(0, world.OverlapBox(new Double3(-10, -10, -10), new Double3(10, 10, 10), results, layerMask: 0b0001u));
        Assert.Equal(1, world.OverlapBox(new Double3(-10, -10, -10), new Double3(10, 10, 10), results, layerMask: 0b0010u));
    }

    [Fact]
    public void OverlapBox_LayerMask_UntaggedEntityIsHitByEveryMask()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(5, 0, 0), 1f); // no layer → AllLayers

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        Assert.Equal(1, world.OverlapBox(new Double3(-10, -10, -10), new Double3(10, 10, 10), results, layerMask: 0b0001u));
        Assert.Equal(1, world.OverlapBox(new Double3(-10, -10, -10), new Double3(10, 10, 10), results, layerMask: 0x8000_0000u));
    }

    [Fact]
    public void OverlapBox_TangentEdgeCase_IsIncluded()
    {
        // Candidate's sphere touches the box boundary exactly: distance-to-clamped-point == radius.
        using var world = new GameWorld();
        Spawn(world, new Double3(11, 0, 0), 1f); // box max.X=10, candidate center 11, radius 1 -> clamp dist = 1

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var count = world.OverlapBox(new Double3(-10, -10, -10), new Double3(10, 10, 10), results);

        Assert.Equal(1, count);
    }

    [Fact]
    public void OverlapBox_BoundaryMargin_CandidateBucketedOutsideNaiveRangeStillFound()
    {
        // Spec §5's concrete derivation: candidate at (2.2,0,0) radius 1, all candidates radius <= 1 -> maxRadius=1,
        // cellSize=2, invCell=0.5. Box (-1.5,-1.5,-1.5)-(1.5,1.5,1.5): naive X cell range floor(-1.5*0.5)=-1 to
        // floor(1.5*0.5)=0. Candidate's own bucket floor(2.2*0.5)=1 — one cell past the naive upper bound.
        // True overlap: clampedX=1.5, distance 2.2-1.5=0.7 <= radius 1 — a real overlap, missed without the
        // one-cell margin expansion. Reported Distance (box-center (0,0,0) to candidate) is 2.2.
        //
        // Audit fix (session 42): this box's own cost estimate is span=4.5 per axis (91.125 cells), so with only
        // the one real candidate the query would take the scan-fallback branch and never touch the margin
        // expansion at all — the exact gap the audit found (and which silently invalidated this same style of
        // test in the session-41 sphere-overlap suite too, fixed alongside this one). Padding with 100 decoys
        // (radius 1, so cellSize is unchanged) pushes count to 101 > 91.125, forcing the grid-walk branch.
        using var world = new GameWorld();
        Spawn(world, new Double3(2.2, 0, 0), 1f);
        SpawnDecoys(world, 100);

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var count = world.OverlapBox(new Double3(-1.5, -1.5, -1.5), new Double3(1.5, 1.5, 1.5), results);

        Assert.Equal(1, count);
        Assert.Equal(2.2, results[0].Distance, 3);
    }

    [Fact]
    public void OverlapBox_MinMaxNonFinite_Throws()
    {
        using var world = new GameWorld();
        Span<OverlapHit> results = stackalloc OverlapHit[4];

        var threwNaNMin = false;
        try
        {
            world.OverlapBox(new Double3(double.NaN, 0, 0), new Double3(1, 1, 1), results);
        }
        catch (ArgumentException)
        {
            threwNaNMin = true;
        }

        Assert.True(threwNaNMin);

        var threwInfMax = false;
        try
        {
            world.OverlapBox(new Double3(-1, -1, -1), new Double3(double.PositiveInfinity, 1, 1), results);
        }
        catch (ArgumentException)
        {
            threwInfMax = true;
        }

        Assert.True(threwInfMax);
    }

    [Fact]
    public void OverlapBox_MinGreaterThanMaxOnAnyAxis_Throws()
    {
        // Audit fix (session 42, csharp-lowlevel F3): the original test only exercised the X-axis inversion —
        // exactly the copy-paste-across-axes defect class this test's own name claims to cover. Y and Z are
        // now exercised explicitly too.
        using var world = new GameWorld();
        Span<OverlapHit> results = stackalloc OverlapHit[4];

        var threwX = false;
        try
        {
            world.OverlapBox(new Double3(1, -1, -1), new Double3(-1, 1, 1), results); // min.X > max.X
        }
        catch (ArgumentException)
        {
            threwX = true;
        }

        Assert.True(threwX);

        var threwY = false;
        try
        {
            world.OverlapBox(new Double3(-1, 1, -1), new Double3(1, -1, 1), results); // min.Y > max.Y
        }
        catch (ArgumentException)
        {
            threwY = true;
        }

        Assert.True(threwY);

        var threwZ = false;
        try
        {
            world.OverlapBox(new Double3(-1, -1, 1), new Double3(1, 1, -1), results); // min.Z > max.Z
        }
        catch (ArgumentException)
        {
            threwZ = true;
        }

        Assert.True(threwZ);
    }

    [Fact]
    public void OverlapBox_HugeBoxOverSparseWorld_CompletesQuickly()
    {
        // Audit fix (session 42, csharp-lowlevel F2 / engine-architect): the original version of this test used
        // a single candidate at the origin, so the occupied-cell AABB was already a single cell regardless of
        // the cost-bound check — the occupied-AABB clamp alone made the query cheap, and the test passed
        // identically with the cost-bound check deleted entirely (verified by audit mutation). A genuine stress
        // test needs candidates spread far enough apart that the occupied AABB itself is huge, so clamping alone
        // cannot save a naive walk — only the cost-bound fallback can.
        using var world = new GameWorld();
        Spawn(world, new Double3(0, 0, 0), 1f);
        Spawn(world, new Double3(500_000_000, 0, 0), 1f);
        Spawn(world, new Double3(1_000_000_000, 0, 0), 1f);

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var min = new Double3(-10, -10, -10);
        var max = new Double3(1_000_000_010, 10, 10); // huge in X only — spans all three candidates

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var count = world.OverlapBox(min, max, results);
        sw.Stop();

        Assert.Equal(3, count); // all three candidates genuinely fall inside this box
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"OverlapBox over widely-spread candidates took {sw.ElapsedMilliseconds} ms — the cost-bound fallback may no longer be triggering.");
    }

    [Fact]
    public void OverlapBox_GridPath_ManyCandidatesInOneCell_TruncatesWithoutThrowing()
    {
        // Audit fix (session 42): exercises the grid-walk's capacity-in-loop-condition guard AND stamp dedup
        // together, forced to actually run via decoys (see SpawnDecoys) — mirrors RaycastAll's own regression
        // test for the analogous pre-existing bug that sibling comparison found in session 41.
        using var world = new GameWorld();
        Spawn(world, new Double3(0, 0, 0), 1f);
        Spawn(world, new Double3(0.1, 0, 0), 1f);
        Spawn(world, new Double3(0.2, 0, 0), 1f); // all three land in the same broadphase cell
        SpawnDecoys(world, 130); // box extent 4, span=5, approxCells=125 -> need count > 125

        Span<OverlapHit> results = stackalloc OverlapHit[2]; // undersized on purpose
        var count = world.OverlapBox(new Double3(-2, -2, -2), new Double3(2, 2, 2), results); // must not throw

        Assert.Equal(2, count);
    }

    [Fact]
    public void OverlapBox_EqualDistanceCandidates_OrderIsDeterministicByEntity()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(5, 0, 0), 1f);
        Spawn(world, new Double3(-5, 0, 0), 1f); // exactly the same distance from the box center

        Span<OverlapHit> results = stackalloc OverlapHit[8];
        var count1 = world.OverlapBox(new Double3(-10, -10, -10), new Double3(10, 10, 10), results);
        var order1 = new[] { results[0].Entity, results[1].Entity };

        for (var i = 0; i < 5; i++)
        {
            var count2 = world.OverlapBox(new Double3(-10, -10, -10), new Double3(10, 10, 10), results);
            Assert.Equal(count1, count2);
            Assert.Equal(order1[0], results[0].Entity);
            Assert.Equal(order1[1], results[1].Entity);
        }
    }

    [Fact]
    public void OverlapBox_AllocatesNothingAfterWarmup()
    {
        using var world = new GameWorld();
        Spawn(world, new Double3(2, 0, 0), 1f);
        Spawn(world, new Double3(5, 0, 0), 1f);
        Spawn(world, new Double3(8, 0, 0), 1f);
        Span<OverlapHit> results = new OverlapHit[8];
        var min = new Double3(-20, -20, -20);
        var max = new Double3(20, 20, 20);
        _ = world.OverlapBox(min, max, results); // warmup

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = world.OverlapBox(min, max, results);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"OverlapBox should allocate nothing, observed {allocated} bytes.");
    }
}
