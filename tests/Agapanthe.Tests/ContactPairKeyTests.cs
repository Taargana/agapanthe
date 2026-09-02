using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0b W1: <see cref="ContactPairKey"/> replaces the 32-bit-per-half <see langword="ulong"/> packing physics
/// contact pairs used (<c>(gidLo &lt;&lt; 32) | (uint)gidHi</c>), which silently unified two pairs whose ids agreed
/// in their low 32 bits — the defect that surfaces the moment <c>GlobalId</c> stops being a dense per-run counter.
/// </summary>
public sealed class ContactPairKeyTests
{
    // The defect this milestone closes: the legacy packing truncates the SECOND id to its low 32 bits, so a pair
    // (1, 2^32+2) and a pair (2^32+1, 2^32+2) — genuinely different pairs — pack to the SAME ulong key.
    [Fact]
    public void DoesNotCollideAcross32BitBoundary()
    {
        ulong lowId = 1UL;
        ulong highId = (1UL << 32) + 1UL;
        ulong partner = (1UL << 32) + 2UL;

        var keyA = new ContactPairKey(lowId, partner);
        var keyB = new ContactPairKey(highId, partner);

        Assert.NotEqual(keyA, keyB);

        // Pin the exact legacy defect being fixed (documents the bug, not a requirement on the new type): shifting
        // `highId` left by 32 overflows a 64-bit ulong and wraps back to the same bit pattern as `lowId << 32`, so
        // the packing this replaces genuinely COLLIDES these two distinct pairs into one key.
        var legacyKeyA = (lowId << 32) | (uint)partner;
        var legacyKeyB = (highId << 32) | (uint)partner;
        Assert.Equal(legacyKeyA, legacyKeyB);
    }

    // Board-specified sparse triple: three ids straddling the 32-bit boundary. The legacy packing overflows a
    // ulong entirely once an id itself exceeds 32 bits (`y << 32` wraps to 0), producing a DIFFERENT permutation
    // than the true (min,max) order the new key must produce.
    [Fact]
    public void OrdersSparsePairsByTrueValue_LegacyPackingWouldPermuteDifferently()
    {
        ulong x = (1UL << 32) - 1;
        ulong y = 1UL << 32;
        ulong z = (1UL << 32) + 1;

        var xy = new ContactPairKey(x, y);
        var xz = new ContactPairKey(x, z);
        var yz = new ContactPairKey(y, z);

        var sorted = new[] { yz, xy, xz };
        Array.Sort(sorted);

        // True order: x < y < z, so by (min,max): (x,y) < (x,z) < (y,z).
        Assert.Equal(new[] { xy, xz, yz }, sorted);

        // Legacy packing: (y << 32) overflows a 64-bit ulong to 0, so legacy key(y,z) collapses to ~(uint)z — far
        // SMALLER than legacy key(x,y) or key(x,z). The legacy permutation is therefore (y,z), (x,y), (x,z): the
        // pair the true order sorts LAST, the legacy packing would have resolved FIRST.
        var legacyXy = (x << 32) | (uint)y;
        var legacyXz = (x << 32) | (uint)z;
        var legacyYz = (y << 32) | (uint)z;
        Assert.True(legacyYz < legacyXy && legacyYz < legacyXz);
    }

    // The common case today (a per-run counter from 1): the new key must induce the SAME resolution order as the
    // packing it replaces, or every capture hash (the W1 gate) regresses.
    [Theory]
    [InlineData(1UL, 2UL, 3UL, 4UL)]
    [InlineData(1UL, 3UL, 5UL, 2UL)]
    [InlineData(7UL, 8UL, 7UL, 9UL)]
    public void OrdersDenseIdsIdenticallyToLegacyPacking(ulong a, ulong b, ulong c, ulong d)
    {
        // Dedup in GameWorld.Physics guarantees the smaller id is passed first for every pair.
        var (lo1, hi1) = a < b ? (a, b) : (b, a);
        var (lo2, hi2) = c < d ? (c, d) : (d, c);

        var legacyKey1 = (lo1 << 32) | (uint)hi1;
        var legacyKey2 = (lo2 << 32) | (uint)hi2;
        var legacyOrder = Math.Sign(legacyKey1.CompareTo(legacyKey2));

        var newOrder = Math.Sign(new ContactPairKey(lo1, hi1).CompareTo(new ContactPairKey(lo2, hi2)));

        Assert.Equal(legacyOrder, newOrder);
    }

    [Fact]
    public void NormalizesArgumentOrder()
    {
        Assert.Equal(new ContactPairKey(3, 7), new ContactPairKey(7, 3));
    }

    [Fact]
    public void EqualPairsCompareEqual()
    {
        Assert.Equal(0, new ContactPairKey(4, 4).CompareTo(new ContactPairKey(4, 4)));
    }

    // Array.Sort<TKey,TValue> must resolve to the constrained generic IComparable<T> path, never box through a
    // Comparison<T> delegate or an IComparer<T> instance (P2-M2 precedent: Span.Sort(structComparer) boxed the
    // comparer, ~88 B/call — exactly the class of regression the 0-alloc physics gate would otherwise miss).
    [Fact]
    public void SortViaGenericComparablePath_DoesNotAllocate()
    {
        // long[], not int[] (audit finding): GameWorld.Physics.cs sorts Array.Sort(_pairKey, _pairPacked, ...)
        // where _pairPacked is long[] — ArraySortHelper<ContactPairKey, long> is the exact instantiation production
        // needs proven allocation-free, not ArraySortHelper<ContactPairKey, int>.
        var keys = new[] { new ContactPairKey(3, 9), new ContactPairKey(1, 2), new ContactPairKey(5, 5) };
        var values = new long[] { 30, 10, 50 };

        Array.Sort(keys, values, 0, 3); // warm up any one-time JIT/generic-instantiation cost

        keys = new[] { new ContactPairKey(3, 9), new ContactPairKey(1, 2), new ContactPairKey(5, 5) };
        values = new long[] { 30, 10, 50 };
        var before = GC.GetAllocatedBytesForCurrentThread();
        Array.Sort(keys, values, 0, 3);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
