using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Tests;

public sealed class RenderListTests
{
    private static RenderItem Item(uint order)
        => new(Matrix4x4.Identity, new MeshHandle((int)order, 1), new MaterialHandle(0, 1), order);

    private static RenderItem WithKey(ulong key)
        => new(Matrix4x4.Identity, new MeshHandle(0, 1), new MaterialHandle(0, 1), key);

    [Fact]
    public void Add_GrowsAndPreservesItemsInOrder()
    {
        var list = new RenderList(initialCapacity: 4);
        for (uint i = 0; i < 200; i++)
        {
            list.Add(Item(i));
        }

        Assert.Equal(200, list.Count);
        Assert.True(list.Capacity >= 200);
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal((ulong)i, list.Items[i].SortKey);
        }
    }

    [Fact]
    public void Clear_ResetsCountWithoutReleasingCapacity()
    {
        var list = new RenderList(initialCapacity: 4);
        for (uint i = 0; i < 100; i++)
        {
            list.Add(Item(i));
        }

        var capacityAfterFill = list.Capacity;
        list.Clear();

        Assert.Equal(0, list.Count);
        Assert.Equal(capacityAfterFill, list.Capacity); // no shrink

        // Refilling up to the retained capacity must not reallocate — the zero-alloc-per-frame invariant.
        for (uint i = 0; i < 100; i++)
        {
            list.Add(Item(i));
        }

        Assert.Equal(capacityAfterFill, list.Capacity);
    }

    [Fact]
    public void SortByKey_OrdersAscending()
    {
        var list = new RenderList();
        for (uint i = 10; i-- > 0;)
        {
            list.Add(Item(i)); // inserted 9,8,...,0 (reverse)
        }

        list.SortByKey();

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal((ulong)i, list.Items[i].SortKey);
        }
    }

    [Fact]
    public void SortByKey_IsAllocationFree()
    {
        // Span.Sort(structComparer) allocates ~88 bytes per call (it boxes the comparer internally) — which is
        // why RenderList sorts by hand. This is the regression guard for that.
        var list = new RenderList();
        for (uint i = 64; i-- > 0;)
        {
            list.Add(Item(i));
        }

        list.SortByKey(); // warm up (allocates the radix scratch once)

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            list.SortByKey();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ComposeSortKey_LaysMaterialHigh_TieBreakLow()
    {
        var key = RenderItem.ComposeSortKey(materialIndex: 3, tieBreak: 7);

        Assert.Equal((3UL << 32) | 7UL, key);
        Assert.Equal(3u, (uint)(key >> 32)); // material recoverable from the high bits
        Assert.Equal(7u, (uint)key);         // tie-break from the low bits
    }

    [Fact]
    public void SortByKey_BatchesByMaterial_ThenByTieBreak()
    {
        // Composed keys: material in the high bits, a per-entity tie-break in the low bits. After sorting the list
        // must be grouped by material, and within each material ordered by the tie-break.
        var list = new RenderList();
        list.Add(WithKey(RenderItem.ComposeSortKey(2, 50)));
        list.Add(WithKey(RenderItem.ComposeSortKey(0, 99)));
        list.Add(WithKey(RenderItem.ComposeSortKey(2, 10)));
        list.Add(WithKey(RenderItem.ComposeSortKey(0, 1)));

        list.SortByKey();

        Assert.Equal(RenderItem.ComposeSortKey(0, 1), list.Items[0].SortKey);
        Assert.Equal(RenderItem.ComposeSortKey(0, 99), list.Items[1].SortKey);
        Assert.Equal(RenderItem.ComposeSortKey(2, 10), list.Items[2].SortKey);
        Assert.Equal(RenderItem.ComposeSortKey(2, 50), list.Items[3].SortKey);
    }

    [Fact]
    public void SortByKey_IsDeterministic_WhenMaterialsCollide()
    {
        // The tie-break's whole point: many entities sharing ONE material have equal high bits, so the order is
        // decided entirely by the low-bits tie-break — and must be the same regardless of the input order (Arch's
        // chunk iteration, which the pre-sort order reflects, is not deterministic).
        var forward = new RenderList();
        var reverse = new RenderList();
        for (uint i = 0; i < 200; i++)
        {
            forward.Add(WithKey(RenderItem.ComposeSortKey(1, i)));
            reverse.Add(WithKey(RenderItem.ComposeSortKey(1, 199 - i)));
        }

        forward.SortByKey();
        reverse.SortByKey();

        for (var i = 0; i < 200; i++)
        {
            Assert.Equal((ulong)i, (uint)forward.Items[i].SortKey);
            Assert.Equal((ulong)i, (uint)reverse.Items[i].SortKey); // same result from the reversed input
        }
    }

    [Fact]
    public void SortByKey_OrdersArbitraryKeysAcrossAllEightBytes()
    {
        // Radix correctness across the full 64-bit width: keys spanning every byte lane, in a fixed-seed shuffle,
        // must come out strictly ascending. Catches a pass that drops or mis-shifts a byte.
        var rng = new Random(12345);
        var keys = new ulong[500];
        var list = new RenderList();
        for (var i = 0; i < keys.Length; i++)
        {
            var hi = (ulong)(uint)rng.Next();
            var lo = (ulong)(uint)rng.Next();
            keys[i] = (hi << 32) | lo;
            list.Add(WithKey(keys[i]));
        }

        list.SortByKey();

        Array.Sort(keys);
        for (var i = 0; i < keys.Length; i++)
        {
            Assert.Equal(keys[i], list.Items[i].SortKey);
        }
    }
}
