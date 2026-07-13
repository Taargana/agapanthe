using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Tests;

public sealed class RenderListTests
{
    private static RenderItem Item(uint order)
        => new(Matrix4x4.Identity, new MeshHandle((int)order), new MaterialHandle(0), order);

    private readonly struct BySortKey : IComparer<RenderItem>
    {
        public int Compare(RenderItem a, RenderItem b) => a.SortKey.CompareTo(b.SortKey);
    }

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
    public void Sort_OrdersByStructComparer()
    {
        var list = new RenderList();
        for (uint i = 10; i-- > 0;)
        {
            list.Add(Item(i)); // inserted 9,8,...,0 (reverse)
        }

        list.Sort(new BySortKey());

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal((ulong)i, list.Items[i].SortKey);
        }
    }
}
