using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free tests for the M2 systems (spec §3.5): transform propagation (system 1, incl. cycle detection) and
/// world-bounds aggregation (system 2).
/// </summary>
public sealed class WorldSystemsTests
{
    private static Vector3 Translation(Matrix4x4 m) => new(m.M41, m.M42, m.M43);

    [Fact]
    public void Propagate_Root_UsesItsOwnLocalTransform()
    {
        using var world = new GameWorld();
        var root = world.SpawnLocalRoot(new Double3(5, 0, 0), Quaternion.Identity, 1f);

        world.PropagateTransforms();

        Assert.Equal(new Vector3(5, 0, 0), Translation(world.GetWorld(root)));
    }

    [Fact]
    public void Propagate_Child_ComposesWithParent()
    {
        using var world = new GameWorld();
        var root = world.SpawnLocalRoot(new Double3(5, 0, 0), Quaternion.Identity, 1f);
        var child = world.SpawnLocalChild(root, new Double3(0, 2, 0), Quaternion.Identity, 1f);

        world.PropagateTransforms();

        // child local (0,2,0) composed with parent (5,0,0) -> world (5,2,0)
        Assert.Equal(new Vector3(5, 2, 0), Translation(world.GetWorld(child)));
    }

    [Fact]
    public void Propagate_Grandchild_ComposesWholeChain()
    {
        using var world = new GameWorld();
        var root = world.SpawnLocalRoot(new Double3(1, 0, 0), Quaternion.Identity, 1f);
        var child = world.SpawnLocalChild(root, new Double3(0, 1, 0), Quaternion.Identity, 1f);
        var grand = world.SpawnLocalChild(child, new Double3(0, 0, 1), Quaternion.Identity, 1f);

        world.PropagateTransforms();

        Assert.Equal(new Vector3(1, 1, 1), Translation(world.GetWorld(grand)));
    }

    [Fact]
    public void Propagate_ParentScale_ScalesChildOffset()
    {
        using var world = new GameWorld();
        var root = world.SpawnLocalRoot(Double3.Zero, Quaternion.Identity, 2f);
        var child = world.SpawnLocalChild(root, new Double3(3, 0, 0), Quaternion.Identity, 1f);

        world.PropagateTransforms();

        // The child's local offset is scaled by the parent's scale: 3 * 2 = 6.
        Assert.Equal(new Vector3(6, 0, 0), Translation(world.GetWorld(child)));
    }

    [Fact]
    public void Propagate_Cycle_ThrowsWithPath()
    {
        using var world = new GameWorld();
        var a = world.SpawnLocalRoot(Double3.Zero, Quaternion.Identity, 1f);
        var b = world.SpawnLocalChild(a, Double3.Zero, Quaternion.Identity, 1f);
        world.SetParent(a, b); // a -> b -> a : cycle

        var ex = Assert.Throws<WorldHierarchyException>(world.PropagateTransforms);
        Assert.Contains("Cycle in entity hierarchy", ex.Message);
        Assert.Contains("->", ex.Message); // the offending chain is reported
    }

    [Fact]
    public void Propagate_DoesNotTouchImportedEntities()
    {
        // Imported drawables carry a BAKED WorldTransform and no LocalTransform, so system 1 must never
        // recompute their matrix — that is what keeps the M2 capture byte-identical (spec §6 condition a).
        using var world = new GameWorld();
        var baked = Matrix4x4.CreateScale(3f) * Matrix4x4.CreateTranslation(7, 8, 9);
        world.SpawnImported(new ImportedEntitySpec(
            new MeshHandle(0), new MaterialHandle(0), baked,
            new Double3(0, 0, 0), new Double3(1, 1, 1), 0));

        world.PropagateTransforms();

        var list = new RenderList();
        world.CollectRenderLists(list, new RenderList());
        Assert.Equal(baked, list.Items[0].WorldTransform); // bit-for-bit unchanged
    }

    [Fact]
    public void AggregateBounds_UnionsEveryEntity()
    {
        using var world = new GameWorld();
        world.SpawnImported(new ImportedEntitySpec(
            new MeshHandle(0), new MaterialHandle(0), Matrix4x4.Identity,
            new Double3(-1, -1, -1), new Double3(1, 1, 1), 0));
        world.SpawnImported(new ImportedEntitySpec(
            new MeshHandle(1), new MaterialHandle(0), Matrix4x4.Identity,
            new Double3(0, 0, 0), new Double3(5, 2, 0), 1));

        var bounds = world.AggregateBounds();

        Assert.Equal(new Double3(-1, -1, -1), bounds.Min);
        Assert.Equal(new Double3(5, 2, 1), bounds.Max);
    }

    [Fact]
    public void AggregateBounds_NoEntities_ReturnsEmpty()
    {
        using var world = new GameWorld();
        var bounds = world.AggregateBounds();

        // Empty seed: Min is +inf, Max is -inf. Callers must guard this degenerate extent.
        Assert.True(double.IsPositiveInfinity(bounds.Min.X));
        Assert.True(double.IsNegativeInfinity(bounds.Max.X));
    }

    [Fact]
    public void Systems_AreAllocationFreeInSteadyState()
    {
        using var world = new GameWorld();
        var root = world.SpawnLocalRoot(new Double3(1, 2, 3), Quaternion.Identity, 1f);
        world.SpawnLocalChild(root, new Double3(0, 1, 0), Quaternion.Identity, 1f);
        for (var i = 0; i < 32; i++)
        {
            world.SpawnImported(new ImportedEntitySpec(
                new MeshHandle(i), new MaterialHandle(0), Matrix4x4.Identity,
                new Double3(i, 0, 0), new Double3(i + 1, 1, 1), (uint)i));
        }

        var render = new RenderList();
        var shadow = new RenderList();

        // Warm up: first calls may grow the reused buffers (walk stack, render lists).
        for (var i = 0; i < 5; i++)
        {
            world.PropagateTransforms();
            _ = world.AggregateBounds();
            world.CollectRenderLists(render, shadow);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            world.PropagateTransforms();
            _ = world.AggregateBounds();
            world.CollectRenderLists(render, shadow);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated); // zero managed allocation per frame on the hot path
    }
}
