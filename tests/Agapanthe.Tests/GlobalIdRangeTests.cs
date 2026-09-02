using System.Linq;
using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>MP-0b W2: <see cref="GlobalIdRange"/> and the single <c>NextId()</c> allocation point it feeds.</summary>
[Collection("World")]
public sealed class GlobalIdRangeTests
{
    [Fact]
    public void StartZero_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GlobalIdRange(0, 10));
    }

    [Fact]
    public void StartAtOrBeyondEnd_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GlobalIdRange(10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GlobalIdRange(11, 10));
    }

    [Fact]
    public void Default_Is1ToMaxValue()
    {
        var range = GlobalIdRange.Default;
        Assert.Equal(1UL, range.Start);
        Assert.Equal(ulong.MaxValue, range.EndExclusive);
    }

    // The parameterless constructor must behave exactly as before this milestone: ids from 1, upward.
    [Fact]
    public void ParameterlessConstructor_IssuesIdsFrom1()
    {
        using var world = new GameWorld();
        var a = world.Spawn(Double3.Zero, Quaternion.Identity, 1f);
        var b = world.Spawn(Double3.Zero, Quaternion.Identity, 1f);

        Assert.Equal(1UL, a.Id);
        Assert.Equal(2UL, b.Id);
    }

    [Fact]
    public void RangedConstructor_IssuesIdsStartingAtRangeStart()
    {
        using var world = new GameWorld(new GlobalIdRange(1_000, 2_000));
        var a = world.Spawn(Double3.Zero, Quaternion.Identity, 1f);
        var b = world.Spawn(Double3.Zero, Quaternion.Identity, 1f);

        Assert.Equal(1_000UL, a.Id);
        Assert.Equal(1_001UL, b.Id);
    }

    [Fact]
    public void TwoWorlds_WithDisjointRanges_IssueDisjointIds()
    {
        using var worldA = new GameWorld(new GlobalIdRange(1, 100));
        using var worldB = new GameWorld(new GlobalIdRange(100, 200));

        var idsA = new HashSet<ulong>();
        var idsB = new HashSet<ulong>();
        for (var i = 0; i < 10; i++)
        {
            idsA.Add(worldA.Spawn(Double3.Zero, Quaternion.Identity, 1f).Id);
            idsB.Add(worldB.Spawn(Double3.Zero, Quaternion.Identity, 1f).Id);
        }

        Assert.Empty(idsA.Intersect(idsB));
    }

    [Fact]
    public void Exhaustion_ThrowsLoudly()
    {
        using var world = new GameWorld(new GlobalIdRange(1, 3)); // exactly 2 ids available: 1, 2
        world.Spawn(Double3.Zero, Quaternion.Identity, 1f);
        world.Spawn(Double3.Zero, Quaternion.Identity, 1f);

        Assert.Throws<InvalidOperationException>(
            () => world.Spawn(Double3.Zero, Quaternion.Identity, 1f));
    }

    // The default range's Save/Load byte layout is untouched by W2 (the header format only changes in W3) — proves
    // this milestone did not silently perturb the v1 snapshot.
    [Fact]
    public void DefaultRange_RoundTripsSnapshotBitForBit()
    {
        using var worldA = new GameWorld();
        worldA.Spawn(new Double3(1, 2, 3), Quaternion.Identity, 1f);
        worldA.Spawn(new Double3(4, 5, 6), Quaternion.Identity, 2f);
        worldA.FlushStructuralChanges();

        using var streamA = new MemoryStream();
        worldA.Save(streamA);

        using var worldB = new GameWorld();
        streamA.Position = 0;
        worldB.Load(streamA);

        using var streamB = new MemoryStream();
        worldB.Save(streamB);

        Assert.Equal(streamA.ToArray(), streamB.ToArray());
    }
}
