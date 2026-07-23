using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// The entity lifecycle (P3-M2, decision D2): deferred spawn/despawn/reparent applied at a barrier, cascade despawn,
/// and the intra-stage semantics of <see cref="GameWorld.IsAlive"/>. These pin the contract that a system relies on
/// when it reads an entity a peer has just marked dead — the kind of thing that, if wrong, corrupts silently.
/// </summary>
[Collection("World")]
public sealed class LifecycleTests
{
    private static RenderView ViewAt(Double3 origin)
        => new(origin, Vector3.Zero, Matrix4x4.Identity, Matrix4x4.Identity, 1f, 1f, 0.1f, 1f);

    private static ImportedEntitySpec Drawable(Double3 position, uint order)
        => new(new MeshHandle(0, 1), new MaterialHandle(0, 1), position, Matrix4x4.Identity, Vector3.Zero, 1f, order);

    private static int DrawableCount(GameWorld world)
    {
        var render = new RenderList();
        world.CollectRenderLists(render, new SceneCandidateSet(), ViewAt(Double3.Zero));
        return render.Count;
    }

    // --- Spawn ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Spawn_HandleIsAliveImmediately_ButEntityMaterialisesAtTheBarrier()
    {
        using var world = new GameWorld();
        var e = world.Spawn(new Double3(1, 0, 0), Quaternion.Identity, 1f);

        Assert.True(world.IsAlive(e));                             // the handle is usable at once...
        Assert.Throws<InvalidOperationException>(() => world.GetWorldPosition(e)); // ...but nothing to read yet

        world.FlushStructuralChanges();

        Assert.True(world.IsAlive(e));
        world.PropagateTransforms();
        Assert.Equal(new Double3(1, 0, 0), world.GetWorldPosition(e)); // now real
    }

    [Fact]
    public void Spawn_Hierarchy_InOneBatch_WiresTheParentLink()
    {
        // The crux the Arch CommandBuffer could not do (spec D2 correction): a child spawned in the SAME batch as its
        // parent must resolve the link. The commands carry GlobalIds, resolved after every spawn exists.
        using var world = new GameWorld();
        var root = world.Spawn(new Double3(5, 0, 0), Quaternion.Identity, 1f);
        var child = world.Spawn(new Double3(0, 2, 0), Quaternion.Identity, 1f, root);
        world.FlushStructuralChanges();

        world.PropagateTransforms();

        Assert.Equal(new Double3(5, 2, 0), world.GetWorldPosition(child)); // parent offset applied
    }

    [Fact]
    public void SpawnDeferred_Drawable_AppearsInTheRenderListAfterFlush()
    {
        using var world = new GameWorld();
        world.SpawnDeferred(Drawable(new Double3(0, 0, -10), 0));

        Assert.Equal(0, DrawableCount(world)); // not yet
        world.FlushStructuralChanges();
        Assert.Equal(1, DrawableCount(world)); // now drawn
    }

    // --- Despawn + barrier ---------------------------------------------------------------------------------------

    [Fact]
    public void Despawn_IsLogicallyImmediate_ButPhysicallyDeferredToTheBarrier()
    {
        // The whole reason to defer: a system marks an entity dead, a PEER system in the same stage still reads it
        // without corruption; the destroy lands only at the barrier.
        using var world = new GameWorld();
        world.SpawnImported(Drawable(new Double3(0, 0, -10), 0));
        var e = world.Spawn(Double3.Zero, Quaternion.Identity, 1f);
        world.FlushStructuralChanges();

        world.Despawn(e);
        Assert.False(world.IsAlive(e));                 // logically dead at once
        Assert.Equal(1, DrawableCount(world));          // but the world has not moved: the drawable is still there
        Assert.Equal(Double3.Zero, world.GetWorldPosition(e)); // and e is still readable — no corruption

        world.FlushStructuralChanges();
        Assert.False(world.IsAlive(e));
        Assert.Throws<InvalidOperationException>(() => world.GetWorldPosition(e)); // now the slot is gone: deref throws
    }

    [Fact]
    public void Despawn_Twice_IsANoOp()
    {
        using var world = new GameWorld();
        var e = world.Spawn(Double3.Zero, Quaternion.Identity, 1f);
        world.FlushStructuralChanges();

        world.Despawn(e);
        world.Despawn(e); // must not throw, must not double-destroy
        world.FlushStructuralChanges();

        Assert.False(world.IsAlive(e));
    }

    [Fact]
    public void SpawnThenDespawn_InSameBatch_LeavesNothing()
    {
        using var world = new GameWorld();
        var e = world.Spawn(Double3.Zero, Quaternion.Identity, 1f);
        world.Despawn(e); // before it ever materialises
        Assert.False(world.IsAlive(e));

        world.FlushStructuralChanges();

        Assert.False(world.IsAlive(e));
        Assert.Throws<InvalidOperationException>(() => world.GetWorldPosition(e));
    }

    [Fact]
    public void AggregateBounds_IsCorrectAfterADespawn()
    {
        using var world = new GameWorld();
        world.SpawnImported(Drawable(Double3.Zero, 0));               // sphere at origin, r1
        var far = world.SpawnDeferred(Drawable(new Double3(10, 0, 0), 1)); // sphere at (10,0,0), r1
        world.FlushStructuralChanges();

        Assert.Equal(new Double3(11, 1, 1), world.AggregateBounds().Max); // both counted

        world.Despawn(far);
        world.FlushStructuralChanges();

        // The extent shrinks back to just the origin sphere — proof the destroyed entity left the aggregation.
        Assert.Equal(new Double3(1, 1, 1), world.AggregateBounds().Max);
    }

    // --- Cascade (D2.a) ------------------------------------------------------------------------------------------

    [Fact]
    public void Despawn_Parent_CascadesToDescendants()
    {
        using var world = new GameWorld();
        var root = world.SpawnLocalRoot(new Double3(1, 0, 0), Quaternion.Identity, 1f);
        var child = world.SpawnLocalChild(root, new Double3(0, 1, 0), Quaternion.Identity, 1f);
        var grand = world.SpawnLocalChild(child, new Double3(0, 0, 1), Quaternion.Identity, 1f);

        world.Despawn(root);
        world.FlushStructuralChanges();

        Assert.False(world.IsAlive(root));
        Assert.False(world.IsAlive(child));
        Assert.False(world.IsAlive(grand)); // the whole subtree, to arbitrary depth
        Assert.Throws<InvalidOperationException>(() => world.GetWorldPosition(grand));
    }

    [Fact]
    public void Despawn_Parent_LeavesNoDanglingChild_ForPropagation()
    {
        // If the child were orphaned instead of cascaded, PropagateTransforms would walk a Parent pointing at a
        // recycled slot — silent garbage. Cascade removes the child, so propagation runs clean and the surviving
        // sibling hierarchy is untouched.
        using var world = new GameWorld();
        var doomedRoot = world.SpawnLocalRoot(Double3.Zero, Quaternion.Identity, 1f);
        world.SpawnLocalChild(doomedRoot, new Double3(0, 1, 0), Quaternion.Identity, 1f);

        var keepRoot = world.SpawnLocalRoot(new Double3(100, 0, 0), Quaternion.Identity, 1f);
        var keepChild = world.SpawnLocalChild(keepRoot, new Double3(0, 5, 0), Quaternion.Identity, 1f);

        world.Despawn(doomedRoot);
        world.FlushStructuralChanges();

        world.PropagateTransforms(); // must not throw on a recycled parent slot
        Assert.Equal(new Double3(100, 5, 0), world.GetWorldPosition(keepChild)); // survivor intact
    }

    // --- Reparent edge cases (D2.b) ------------------------------------------------------------------------------

    [Fact]
    public void SetParent_TowardADespawnedEntity_IsDropped()
    {
        using var world = new GameWorld();
        var child = world.SpawnLocalRoot(new Double3(3, 0, 0), Quaternion.Identity, 1f);
        var goneParent = world.SpawnLocalRoot(new Double3(50, 0, 0), Quaternion.Identity, 1f);
        world.Despawn(goneParent);
        world.FlushStructuralChanges(); // goneParent is now destroyed

        world.SetParent(child, goneParent); // toward a dead entity
        world.FlushStructuralChanges();     // dropped: child keeps its own transform

        world.PropagateTransforms();
        Assert.Equal(new Double3(3, 0, 0), world.GetWorldPosition(child));
    }

    [Fact]
    public void SetParent_TowardAParentQueuedForDespawn_CarriesTheChildWithIt()
    {
        // Spec D2.b, the explicit case: SetParent(x, P) while P is already queued for despawn. The barrier's cascade
        // scan finds x under P and takes x with it.
        using var world = new GameWorld();
        var x = world.SpawnLocalRoot(new Double3(7, 0, 0), Quaternion.Identity, 1f);
        var p = world.SpawnLocalRoot(new Double3(0, 9, 0), Quaternion.Identity, 1f);

        world.Despawn(p);      // P queued for despawn...
        world.SetParent(x, p); // ...and x attached to it in the same batch

        world.FlushStructuralChanges();

        Assert.False(world.IsAlive(p));
        Assert.False(world.IsAlive(x)); // carried away by the cascade
    }

    [Fact]
    public void IsAlive_IsFalse_ForTheNoneHandle()
    {
        using var world = new GameWorld();
        Assert.False(world.IsAlive(default));
        world.Despawn(default); // must be a harmless no-op
        world.FlushStructuralChanges();
    }

    // --- Zero-alloc under churn (spec F2 — the gate the flat bench cannot give) -----------------------------------

    [Fact]
    public void Churn_IsAllocationFree_InSteadyState()
    {
        // The bench (grid:NxN) never spawns or despawns, so its "0 B/frame" says nothing about the lifecycle path.
        // Churn spawns a HIERARCHY (root + two children, wired in one batch) and despawns a whole subtree every
        // frame — the cascade scan, the archetype moves, the _live add/remove — and proves it allocates nothing once
        // the archetypes, chunks and the reused collections have reached their steady size.
        using var world = new GameWorld();
        var roots = new Queue<EntityRef>();
        const int ring = 8;

        void Frame()
        {
            var root = world.Spawn(Double3.Zero, Quaternion.Identity, 1f);
            world.Spawn(new Double3(0, 1, 0), Quaternion.Identity, 1f, root);
            world.Spawn(new Double3(1, 0, 0), Quaternion.Identity, 1f, root);
            world.FlushStructuralChanges();
            roots.Enqueue(root);

            if (roots.Count > ring)
            {
                world.Despawn(roots.Dequeue()); // cascades to that root's two children
                world.FlushStructuralChanges();
            }
        }

        // Warm up: grow the archetypes/chunks, the _live dictionary, the command list and the despawn scratch, and
        // fill the ring so that from here every frame both spawns AND despawns (the true steady state).
        for (var i = 0; i < 200; i++)
        {
            Frame();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++)
        {
            Frame();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
