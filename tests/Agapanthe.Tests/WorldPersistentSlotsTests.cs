using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// The persistent-slot maintenance (P3-M6 AW-004/005): CollectRenderLists rebuilds structurally on spawn/despawn
/// and origin re-snap, patches incrementally otherwise, stamps a stable slot per drawable, and queues only the
/// drawables a mutator moved. The "static scene → empty dirty queue" case is the win the animated bench cannot show.
/// </summary>
[Collection("World")]
public sealed class WorldPersistentSlotsTests
{
    private static RenderView ViewAt(Double3 origin)
        => new(origin, Vector3.Zero, Matrix4x4.Identity, Matrix4x4.Identity, 1f, 1f, 0.1f, 1f);

    private static ImportedEntitySpec Drawable(Double3 position, uint order)
        => new(new MeshHandle(0, 1), new MaterialHandle(0, 1), position, Matrix4x4.Identity, Vector3.Zero, 1f, order);

    // Rotation-only animator (keeps the translation row zero) — the AnimateDrawables path.
    private struct Spin : IDrawableAnimator
    {
        public void Animate(ulong globalId, ref Double3 position, ref Matrix4x4 rotationScale)
            => rotationScale = Matrix4x4.CreateRotationY(0.01f) * rotationScale;
    }

    [Fact]
    public void FirstCollect_IsStructural_ThenIncremental_WithStableVersion()
    {
        using var world = new GameWorld();
        world.SpawnImported(Drawable(new Double3(0, 0, 0), 0));
        world.SpawnImported(Drawable(new Double3(5, 0, 0), 1));
        var render = new RenderList();
        var set = new SceneCandidateSet();

        world.CollectRenderLists(render, set, ViewAt(Double3.Zero));
        Assert.True(set.Structural);
        Assert.Equal(2, set.Count);
        var version = set.StructuralVersion;

        world.CollectRenderLists(render, set, ViewAt(Double3.Zero));
        Assert.False(set.Structural);               // nothing changed → incremental
        Assert.Equal(version, set.StructuralVersion); // no re-slot
    }

    [Fact]
    public void Spawn_ForcesStructuralRebuild()
    {
        using var world = new GameWorld();
        world.SpawnImported(Drawable(new Double3(0, 0, 0), 0));
        var render = new RenderList();
        var set = new SceneCandidateSet();
        world.CollectRenderLists(render, set, ViewAt(Double3.Zero)); // structural
        world.CollectRenderLists(render, set, ViewAt(Double3.Zero)); // incremental
        var version = set.StructuralVersion;

        world.SpawnDeferred(Drawable(new Double3(9, 0, 0), 1));
        world.FlushStructuralChanges();
        world.CollectRenderLists(render, set, ViewAt(Double3.Zero));

        Assert.True(set.Structural);
        Assert.Equal(version + 1, set.StructuralVersion);
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void OriginResnap_ForcesStructuralRebuild()
    {
        using var world = new GameWorld();
        world.SpawnImported(Drawable(new Double3(0, 0, 0), 0));
        var render = new RenderList();
        var set = new SceneCandidateSet();
        world.CollectRenderLists(render, set, ViewAt(Double3.Zero)); // structural
        world.CollectRenderLists(render, set, ViewAt(Double3.Zero)); // incremental

        world.CollectRenderLists(render, set, ViewAt(new Double3(1024, 0, 0))); // origin moved a cell
        Assert.True(set.Structural); // every slot's origin-relative data is stale → full re-bake
    }

    [Fact]
    public void Incremental_QueuesOnlyMovedDrawables()
    {
        using var world = new GameWorld();
        world.SpawnImported(Drawable(new Double3(0, 0, 0), 0));
        world.SpawnImported(Drawable(new Double3(5, 0, 0), 1));
        var render = new RenderList();
        var set = new SceneCandidateSet();
        world.CollectRenderLists(render, set, ViewAt(Double3.Zero)); // structural (slots assigned)

        // Static frame: no mutator ran → nothing queued (the static-scene win).
        world.CollectRenderLists(render, set, ViewAt(Double3.Zero));
        Assert.False(set.Structural);
        Assert.Equal(0, set.Dirty.Length);

        // Animate both → both queued.
        var spin = new Spin();
        world.AnimateDrawables(ref spin);
        world.CollectRenderLists(render, set, ViewAt(Double3.Zero));
        Assert.Equal(2, set.Dirty.Length);
    }

    [Fact]
    public void Slots_AreDeterministic_AndStableAcrossIncremental()
    {
        using var world = new GameWorld();
        // Same mesh/material, distinct RenderOrder → the sort key ranks by order, so slot == order rank.
        var e0 = world.SpawnDeferred(Drawable(new Double3(0, 0, 0), 0));
        var e1 = world.SpawnDeferred(Drawable(new Double3(5, 0, 0), 1));
        var e2 = world.SpawnDeferred(Drawable(new Double3(9, 0, 0), 2));
        world.FlushStructuralChanges();

        var render = new RenderList();
        var set = new SceneCandidateSet();
        world.CollectRenderLists(render, set, ViewAt(Double3.Zero)); // structural: stamps slots

        Assert.Equal(0, world.SlotOf(e0));
        Assert.Equal(1, world.SlotOf(e1));
        Assert.Equal(2, world.SlotOf(e2));

        // An incremental frame must not re-slot.
        var spin = new Spin();
        world.AnimateDrawables(ref spin);
        world.CollectRenderLists(render, set, ViewAt(Double3.Zero));
        Assert.False(set.Structural);
        Assert.Equal(0, world.SlotOf(e0));
        Assert.Equal(1, world.SlotOf(e1));
        Assert.Equal(2, world.SlotOf(e2));
    }
}
