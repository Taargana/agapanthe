using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free tests for the M2 systems (spec §3.5): transform propagation (system 1, incl. cycle detection) and
/// world-bounds aggregation (system 2).
/// </summary>
[Collection("World")]
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
        // Imported drawables carry a BAKED transform and no LocalTransform, so system 1 must never recompute
        // it (spec §6 condition a). Recombined at a zero origin, the split (Double3 position + rotation/scale
        // matrix) reproduces the asset's matrix BIT-FOR-BIT — a float widened to double and narrowed back is
        // the identical float.
        using var world = new GameWorld();
        var baked = Matrix4x4.CreateScale(3f) * Matrix4x4.CreateTranslation(7, 8, 9);
        var rotationScale = baked with { M41 = 0f, M42 = 0f, M43 = 0f };
        world.SpawnImported(new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), new Double3(7, 8, 9), rotationScale,
            Vector3.Zero, 1f, 0));

        world.PropagateTransforms();

        var list = new RenderList();
        world.CollectRenderLists(list, new RenderList(), Double3.Zero);
        Assert.Equal(baked, list.Items[0].WorldTransform); // bit-for-bit unchanged
    }

    [Fact]
    public void CollectRenderLists_SubtractsTheOrigin_SoFarOutIsIdenticalToTheOrigin()
    {
        // THE M3 invariant (spec §3.3): the same scene, rendered from the same relative viewpoint, must produce
        // the SAME model matrices whether it sits at the world origin or 10 000 km away. Absolute float
        // positions cannot do this — at 1e7 m consecutive floats are ~1 m apart, so the geometry would visibly
        // snap to a metre grid. Because the subtraction happens in double, the two agree bit-for-bit.
        var far = new Double3(1e7, 1e7, 1e7);

        using var near = new GameWorld();
        near.SpawnImported(new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), new Double3(1.5, -2.25, 0.125), Matrix4x4.Identity,
            Vector3.Zero, 0f, 0));

        using var farAway = new GameWorld();
        farAway.SpawnImported(new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), far + new Double3(1.5, -2.25, 0.125),
            Matrix4x4.Identity, Vector3.Zero, 0f, 0));

        var atOrigin = new RenderList();
        near.CollectRenderLists(atOrigin, new RenderList(), Double3.Zero);

        var atFar = new RenderList();
        farAway.CollectRenderLists(atFar, new RenderList(), far);

        Assert.Equal(atOrigin.Items[0].WorldTransform, atFar.Items[0].WorldTransform);
    }

    [Fact]
    public void Propagate_FarOutRoot_KeepsItsPositionExact()
    {
        // A root's position is accumulated in double against an identity matrix, so it passes through untouched.
        // Narrowed to float first, 10 000 000.5 would have been rounded to 10 000 000 — half a metre of error
        // before a single frame is drawn.
        using var world = new GameWorld();
        var root = world.SpawnLocalRoot(new Double3(10_000_000.5, 0, 0), Quaternion.Identity, 1f);
        var child = world.SpawnLocalChild(root, new Double3(0, 2, 0), Quaternion.Identity, 1f);

        world.PropagateTransforms();

        Assert.Equal(10_000_000.5, world.GetWorldPosition(root).X);
        Assert.Equal(new Double3(10_000_000.5, 2, 0), world.GetWorldPosition(child));

        // And relative to an eye sitting next to it, the child is exactly 2 m up — no float grid in sight.
        var eye = new Double3(10_000_000.5, 0, 0);
        Assert.Equal(new Vector3(0f, 2f, 0f), Translation(world.GetWorld(child, eye)));
    }

    [Fact]
    public void AggregateBounds_UnionsTheWorldSpheres()
    {
        // Each entity's LOCAL sphere is transformed to world (centre = local centre + WorldPosition, since the
        // rotation/scale here is identity) and its enclosing box is unioned into the extent.
        using var world = new GameWorld();
        world.SpawnImported(new ImportedEntitySpec(       // world sphere (0,0,0) r1 -> [-1,1]^3
            new MeshHandle(0, 1), new MaterialHandle(0, 1), Double3.Zero, Matrix4x4.Identity,
            Vector3.Zero, 1f, 0));
        world.SpawnImported(new ImportedEntitySpec(       // world sphere (10,0,0) r2 -> [8,12]x[-2,2]x[-2,2]
            new MeshHandle(1, 1), new MaterialHandle(0, 1), new Double3(10, 0, 0), Matrix4x4.Identity,
            Vector3.Zero, 2f, 1));

        var bounds = world.AggregateBounds();

        Assert.Equal(new Double3(-1, -2, -2), bounds.Min);
        Assert.Equal(new Double3(12, 2, 2), bounds.Max);
    }

    [Fact]
    public void AggregateBounds_GrowsTheRadiusByTheTransformScale()
    {
        // A local sphere of radius 1, on an entity scaled x3, must aggregate to a world sphere of radius 3 — the
        // conservative max-axis-scale growth, so a scaled object is never under-covered.
        using var world = new GameWorld();
        world.SpawnImported(new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), Double3.Zero, Matrix4x4.CreateScale(3f),
            Vector3.Zero, 1f, 0));

        var bounds = world.AggregateBounds();

        Assert.Equal(new Double3(-3, -3, -3), bounds.Min);
        Assert.Equal(new Double3(3, 3, 3), bounds.Max);
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
                new MeshHandle(i, 1), new MaterialHandle(0, 1), new Double3(i, 0, 0), Matrix4x4.Identity,
                Vector3.Zero, 1f, (uint)i));
        }

        var render = new RenderList();
        var shadow = new RenderList();

        // Warm up: first calls may grow the reused buffers (walk stack, render lists).
        for (var i = 0; i < 5; i++)
        {
            world.PropagateTransforms();
            _ = world.AggregateBounds();
            world.CollectRenderLists(render, shadow, Double3.Zero);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            world.PropagateTransforms();
            _ = world.AggregateBounds();
            world.CollectRenderLists(render, shadow, Double3.Zero);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated); // zero managed allocation per frame on the hot path
    }
}
