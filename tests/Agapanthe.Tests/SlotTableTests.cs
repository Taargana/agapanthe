using Agapanthe.Graphics;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

/// <summary>
/// The handle lifecycle behind <c>ResourceRegistry</c> (spec §3.2), exercised GPU-free: the slot map is generic, so
/// these assertions are the same ones that hold for meshes and materials on a real device.
/// <para>
/// This is the P2-M2 audit's red debt: handles used to be a bare index into a PER-MODEL table, so
/// <c>MeshHandle(0)</c> of model A and of model B were the same handle, and a handle outliving its model resolved
/// silently to whatever now sat in the slot.
/// </para>
/// </summary>
public sealed class SlotTableTests
{
    private sealed record Res(string Name);

    [Fact]
    public void Resolve_ReturnsTheStoredValue()
    {
        var table = new SlotTable<Res>("Mesh");
        var value = new Res("a");
        var (index, generation) = table.Add(value);

        Assert.Same(value, table.Resolve(index, generation));
    }

    [Fact]
    public void DefaultHandle_NeverResolves()
    {
        // A default MeshHandle is (index 0, generation 0). Fresh slots start at generation 1, so it cannot match
        // the very first resource ever added — a forgotten initialization is an error, not a wrong draw.
        var table = new SlotTable<Res>("Mesh");
        var (index, generation) = table.Add(new Res("a"));

        Assert.Equal(0, index);
        Assert.Equal(1u, generation);
        Assert.Throws<GraphicsException>(() => table.Resolve(0, 0));
    }

    [Fact]
    public void Resolve_OutOfRangeIndex_Throws()
    {
        var table = new SlotTable<Res>("Mesh");
        table.Add(new Res("a"));

        var ex = Assert.Throws<GraphicsException>(() => table.Resolve(7, 1));
        Assert.Contains("out of range", ex.Message);

        // Negative indices (Invalid handles) take the same path — (uint) makes them huge.
        Assert.Throws<GraphicsException>(() => table.Resolve(-1, 0));
    }

    [Fact]
    public void Resolve_StaleHandleAfterFree_Throws()
    {
        var table = new SlotTable<Res>("Mesh");
        var (index, generation) = table.Add(new Res("a"));

        table.Free(index);

        var ex = Assert.Throws<GraphicsException>(() => table.Resolve(index, generation));
        Assert.Contains("stale", ex.Message);
    }

    [Fact]
    public void Resolve_HandleFromRecycledSlot_ThrowsRatherThanReturningTheNewValue()
    {
        // The scenario that must never draw the wrong mesh: an entity outlives Unload, its slot is reused by the
        // next model, and its handle still points at that index.
        var table = new SlotTable<Res>("Mesh");
        var (index, staleGeneration) = table.Add(new Res("old"));
        table.Free(index);

        var fresh = new Res("new");
        var (reusedIndex, freshGeneration) = table.Add(fresh);

        Assert.Equal(index, reusedIndex);            // the slot IS recycled (no leak of slots)
        Assert.NotEqual(staleGeneration, freshGeneration); // but the generation moved on
        Assert.Same(fresh, table.Resolve(reusedIndex, freshGeneration));
        Assert.Throws<GraphicsException>(() => table.Resolve(index, staleGeneration));
    }

    [Fact]
    public void TwoModelsLoadedAtOnce_GetDistinctHandles()
    {
        // The collision the audit flagged: with per-model tables, both models minted handle 0. One global table
        // means every live resource has its own slot — which is what M4's single render list needs.
        var table = new SlotTable<Res>("Mesh");
        var modelA = new[] { table.Add(new Res("a0")), table.Add(new Res("a1")) };
        var modelB = new[] { table.Add(new Res("b0")), table.Add(new Res("b1")) };

        var indices = modelA.Concat(modelB).Select(h => h.Index).ToArray();
        Assert.Equal(4, indices.Distinct().Count());

        Assert.Equal("a0", table.Resolve(modelA[0].Index, modelA[0].Generation).Name);
        Assert.Equal("b0", table.Resolve(modelB[0].Index, modelB[0].Generation).Name);
    }

    [Fact]
    public void Add_GrowsBeyondInitialCapacity()
    {
        var table = new SlotTable<Res>("Mesh");
        var handles = new List<(int Index, uint Generation)>();
        for (var i = 0; i < 100; i++) // initial capacity is 16
        {
            handles.Add(table.Add(new Res($"r{i}")));
        }

        Assert.Equal(100, table.Count);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal($"r{i}", table.Resolve(handles[i].Index, handles[i].Generation).Name);
        }
    }
}
