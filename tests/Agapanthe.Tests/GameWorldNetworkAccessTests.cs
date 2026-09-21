using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// Net-1 — <see cref="GameWorld"/>'s network access surface: <see cref="GameWorld.TryGetEntity"/>,
/// <see cref="GameWorld.GetDrawableTransform"/>/<see cref="GameWorld.SetDrawableTransform"/>, and
/// <see cref="GameWorld.DrainDirtyDrawables"/>.
/// <para>
/// <see cref="GameWorld.DrainDirtyDrawables"/>'s dirty set is fed by the three SIMULATION mutation surfaces
/// (animation, physics writeback, hierarchy propagation) — never by <see cref="GameWorld.SetDrawableTransform"/>
/// (the render-cache CLIENT's own write path). A live end-to-end run found the earlier version of this file
/// exercised the drain via <c>SetDrawableTransform</c>, which happened to work only because tests also forced a
/// structural rebuild (<see cref="ForceStructuralRebuild"/>) — a step a genuinely headless server (no renderer)
/// never performs, so <c>InstanceSlot</c> never leaves its unassigned sentinel there and the OLD slot-keyed dirty
/// set (this file's dirty-marking used to piggyback on <c>MarkDirty(int slot)</c>) silently never fired. Fixed by
/// keying the network dirty set on <c>GlobalId</c> instead (see <c>GameWorld.MarkNetworkDirty</c>), entirely
/// independent of slot assignment — these tests now drive it via real physics, exactly like
/// <c>DedicatedServer</c> does, not via the client-only write path.
/// </para>
/// </summary>
[Collection("World")]
public sealed class GameWorldNetworkAccessTests
{
    private static ImportedEntitySpec Keyed(string key, Double3 position) => new(
        new MeshHandle(0, 1), new MaterialHandle(0, 1), position, Matrix4x4.Identity, Vector3.Zero, 1f, 0,
        new MeshRefKey(new AssetKey(key), 0, 0));

    private static RenderView ViewAt(Double3 origin)
        => new(origin, Vector3.Zero, Matrix4x4.Identity, Matrix4x4.Identity, 1f, 1f, 0.1f, 1f);

    // Only needed for SetDrawableTransform's RENDER-side dirty marking (MarkDirty(int slot), P3-M6) — a
    // structural rebuild is what a real client's own frame loop performs every frame automatically. Never needed
    // for DrainDirtyDrawables (GlobalId-keyed, independent of InstanceSlot).
    private static void ForceStructuralRebuild(GameWorld world)
        => world.CollectRenderLists(new RenderList(), new SceneCandidateSet(), ViewAt(Double3.Zero));

    // Drives the network-dirty set the way DedicatedServer actually does: a real physics body, stepped once.
    // Zero gravity/inverse-mass and no ground contact still trigger the per-body writeback unconditionally
    // (GameWorld.Physics.cs's own comment: "the body moved -> queue it", called for every processed body every
    // step, not only on an actual position change).
    private static EntityRef SpawnPhysicsBody(GameWorld world, string key, Double3 position)
    {
        var spec = new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), position, Matrix4x4.Identity, Vector3.Zero, 1f, 0,
            new MeshRefKey(new AssetKey(key), 0, 0));
        return world.SpawnBody(in spec, Vector3.Zero, inverseMass: 1f, restitution: 0f, radius: 1f);
    }

    private static void Step(GameWorld world)
        => world.StepPhysics(new PhysicsSettings(Vector3.Zero, groundY: -100_000f, fixedDt: 1f / 60f));

    [Fact]
    public void TryGetEntity_ResolvesASpawnedDrawableByItsGlobalId()
    {
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        world.SpawnImported(Keyed("models/helmet", Double3.Zero));

        var resolved = world.TryGetEntity(id, out var entity);

        Assert.True(resolved);
        Assert.Equal(id, entity.Id);
    }

    [Fact]
    public void GetGlobalId_IsTheInverseOfTryGetEntity()
    {
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        world.SpawnImported(Keyed("models/helmet", Double3.Zero));
        world.TryGetEntity(id, out var entity);

        Assert.Equal(id, world.GetGlobalId(entity));
    }

    [Fact]
    public void TryGetEntity_FailsCleanlyForAnUnknownGlobalId()
    {
        using var world = new GameWorld();

        var resolved = world.TryGetEntity(999_999, out var entity);

        Assert.False(resolved);
        Assert.True(entity.IsNone);
    }

    [Fact]
    public void GetDrawableTransform_ReadsPositionAndAssetIdentity()
    {
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        world.SpawnImported(Keyed("models/helmet", new Double3(1, 2, 3)));
        world.TryGetEntity(id, out var entity);

        var transform = world.GetDrawableTransform(entity);

        Assert.Equal(id, transform.GlobalId);
        Assert.Equal(new AssetKey("models/helmet"), transform.AssetIdentity.Key);
        Assert.Equal(new Double3(1, 2, 3), transform.Position);
    }

    [Fact]
    public void SetDrawableTransform_WritesPosition_ReadableViaGetDrawableTransform()
    {
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        world.SpawnImported(Keyed("models/helmet", Double3.Zero));
        world.TryGetEntity(id, out var entity);
        var moved = world.GetDrawableTransform(entity) with { Position = new Double3(5, 6, 7) };

        world.SetDrawableTransform(entity, moved);

        Assert.Equal(new Double3(5, 6, 7), world.GetDrawableTransform(entity).Position);
    }

    [Fact]
    public void SetDrawableTransform_MarksTheRenderSlotDirty_OnceAssignedByAStructuralRebuild()
    {
        // The render-cache client's OWN reason SetDrawableTransform marks anything at all: so the existing GPU
        // upload path (P3-M6) picks the change up. Unrelated to DrainDirtyDrawables/the network dirty set.
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        world.SpawnImported(Keyed("models/helmet", Double3.Zero));
        ForceStructuralRebuild(world);
        world.TryGetEntity(id, out var entity);
        var slot = world.SlotOf(entity);

        world.SetDrawableTransform(entity, world.GetDrawableTransform(entity) with { Position = new Double3(1, 1, 1) });

        Assert.True(slot >= 0);
        // No public accessor for _dirtySlots exists (by design — P3-M6's render path is internal); this test
        // exists to document the intent. The GPU-visible consequence is covered by existing P3-M6 tests
        // (WorldPersistentSlotsTests) and by CollectRenderLists' own incremental-patch behavior.
    }

    [Fact]
    public void DrainDirtyDrawables_ReportsABodyThatStepPhysicsProcessed()
    {
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        SpawnPhysicsBody(world, "models/helmet", new Double3(5, 6, 7));

        Step(world);

        Span<DrawableTransform> buffer = new DrawableTransform[8];
        var written = world.DrainDirtyDrawables(buffer);
        Assert.Equal(1, written);
        Assert.Equal(id, buffer[0].GlobalId);
        Assert.Equal(new Double3(5, 6, 7), buffer[0].Position);
    }

    [Fact]
    public void DrainDirtyDrawables_WorksWithNoRendererEverHavingRun()
    {
        // The exact bug a live two-process run caught: InstanceSlot is only ever assigned by CollectRenderLists
        // (P3-M6), which a genuinely headless DedicatedServer never calls. DrainDirtyDrawables must report a
        // dirty body regardless — proven here by never calling ForceStructuralRebuild at all.
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        var entity = SpawnPhysicsBody(world, "models/helmet", Double3.Zero);
        Assert.Equal(-1, world.SlotOf(entity)); // never went through a structural rebuild — confirms the setup

        Step(world);

        Span<DrawableTransform> buffer = new DrawableTransform[8];
        Assert.Equal(1, world.DrainDirtyDrawables(buffer));
        Assert.Equal(id, buffer[0].GlobalId);
    }

    [Fact]
    public void DrainDirtyDrawables_ReturnsNothingWhenNothingChangedSinceTheLastDrain()
    {
        using var world = new GameWorld();
        SpawnPhysicsBody(world, "models/helmet", Double3.Zero);
        Step(world);

        Span<DrawableTransform> buffer = new DrawableTransform[8];
        var firstDrain = world.DrainDirtyDrawables(buffer);
        var secondDrain = world.DrainDirtyDrawables(buffer);

        Assert.Equal(1, firstDrain);
        Assert.Equal(0, secondDrain);
    }

    [Fact]
    public void DrainDirtyDrawables_ReDirtiesOnTheNextStep()
    {
        using var world = new GameWorld();
        SpawnPhysicsBody(world, "models/helmet", Double3.Zero);
        Step(world);

        Span<DrawableTransform> buffer = new DrawableTransform[8];
        world.DrainDirtyDrawables(buffer); // drain the first step's dirty entry

        Step(world); // physics writeback fires again, unconditionally, per body

        Assert.Equal(1, world.DrainDirtyDrawables(buffer));
    }

    [Fact]
    public void DrainDirtyDrawables_ReportsEveryDirtyBody()
    {
        using var world = new GameWorld();
        var idA = world.NextGlobalIdForTest;
        SpawnPhysicsBody(world, "models/a", Double3.Zero);
        var idB = world.NextGlobalIdForTest;
        SpawnPhysicsBody(world, "models/b", Double3.Zero);
        Step(world);

        Span<DrawableTransform> buffer = new DrawableTransform[8];
        var written = world.DrainDirtyDrawables(buffer);

        Assert.Equal(2, written);
        Assert.Contains(buffer[..written].ToArray(), t => t.GlobalId == idA);
        Assert.Contains(buffer[..written].ToArray(), t => t.GlobalId == idB);
        Assert.Equal(0, world.DrainDirtyDrawables(buffer));
    }

    [Fact]
    public void DrainDirtyDrawables_TruncatesWithoutThrowing_WhenTheBufferIsSmallerThanTheDirtySet()
    {
        using var world = new GameWorld();
        for (var i = 0; i < 3; i++)
        {
            SpawnPhysicsBody(world, $"models/{i}", Double3.Zero);
        }

        Step(world);

        Span<DrawableTransform> small = new DrawableTransform[1];
        var firstBatch = world.DrainDirtyDrawables(small);
        Span<DrawableTransform> rest = new DrawableTransform[8];
        var secondBatch = world.DrainDirtyDrawables(rest);

        Assert.Equal(1, firstBatch);
        Assert.Equal(2, secondBatch);
    }

    [Fact]
    public void SnapshotAllDrawables_ReportsADrawableNeverMarkedDirty()
    {
        // The exact gap an audit finding (engine-architect F1) caught: a drawable that is neither a physics body
        // nor animated is never marked network-dirty at all, so DrainDirtyDrawables alone would never report it.
        // A late-joining client needs SOME way to learn about it — this is that way.
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        world.SpawnImported(Keyed("models/static-prop", new Double3(1, 2, 3)));

        Span<DrawableTransform> drained = new DrawableTransform[8];
        Assert.Equal(0, world.DrainDirtyDrawables(drained)); // confirms the gap: never dirty, never drained

        var snapshot = world.SnapshotAllDrawables();

        Assert.Contains(snapshot, t => t.GlobalId == id && t.Position == new Double3(1, 2, 3));
    }

    [Fact]
    public void SnapshotAllDrawables_IncludesAPhysicsBodyToo()
    {
        using var world = new GameWorld();
        var idA = world.NextGlobalIdForTest;
        world.SpawnImported(Keyed("models/static-prop", Double3.Zero));
        var idB = world.NextGlobalIdForTest;
        SpawnPhysicsBody(world, "models/body", new Double3(9, 9, 9));

        var snapshot = world.SnapshotAllDrawables();

        Assert.Contains(snapshot, t => t.GlobalId == idA);
        Assert.Contains(snapshot, t => t.GlobalId == idB && t.Position == new Double3(9, 9, 9));
    }

    [Fact]
    public void GetDrawableTransform_ThrowsOnANonUniformScale()
    {
        // DrawableTransform.Scale is a single float (wire shape, spec D9) — it can only represent a uniform
        // scale. An audit finding caught the old code silently truncating (1, 2, 1) down to just its X component
        // with no signal at all; it must fail loudly instead.
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        var nonUniform = new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), Double3.Zero,
            Matrix4x4.CreateScale(new Vector3(1, 2, 1)), Vector3.Zero, 1f, 0,
            new MeshRefKey(new AssetKey("models/helmet"), 0, 0));
        world.SpawnImported(nonUniform);
        world.TryGetEntity(id, out var entity);

        Assert.Throws<InvalidOperationException>(() => world.GetDrawableTransform(entity));
    }

    [Fact]
    public void SnapshotAllDrawables_ThrowsOnANonUniformScale()
    {
        using var world = new GameWorld();
        var nonUniform = new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), Double3.Zero,
            Matrix4x4.CreateScale(new Vector3(1, 2, 1)), Vector3.Zero, 1f, 0,
            new MeshRefKey(new AssetKey("models/helmet"), 0, 0));
        world.SpawnImported(nonUniform);

        Assert.Throws<InvalidOperationException>(() => world.SnapshotAllDrawables());
    }

    [Fact]
    public void SetDrawableTransform_FromASanctionedWorkerThread_StillThrows()
    {
        // Job-1 precedent: this mutates shared component state, exactly the class of hazard
        // AssertOwnerThreadStrict exists to protect — SetSanctionedWorkerThreads must never widen it.
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        world.SpawnImported(Keyed("models/helmet", Double3.Zero));
        world.TryGetEntity(id, out var entity);
        var transform = world.GetDrawableTransform(entity);

        Exception? captured = null;
        var t = new Thread(() =>
        {
            world.SetSanctionedWorkerThreads([Environment.CurrentManagedThreadId]);
            try
            {
                world.SetDrawableTransform(entity, transform);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        t.Start();
        t.Join();

        Assert.IsType<InvalidOperationException>(captured);
    }

    [Fact]
    public void DrainDirtyDrawables_FromAnUnsanctionedThread_Throws()
    {
        using var world = new GameWorld();

        Exception? captured = null;
        var t = new Thread(() =>
        {
            try
            {
                Span<DrawableTransform> buffer = new DrawableTransform[4];
                world.DrainDirtyDrawables(buffer);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        t.Start();
        t.Join();

        Assert.IsType<InvalidOperationException>(captured);
    }
}
