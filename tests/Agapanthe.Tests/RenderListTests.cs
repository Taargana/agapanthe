using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Tests;

public sealed class RenderListTests
{
    private static RenderItem Item(uint order)
        => new(Matrix4x4.Identity, new MeshHandle((int)order), new MaterialHandle(0), order);

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

        list.SortByKey(); // warm up

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            list.SortByKey();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
