using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0b W3: the v2 snapshot header (<see cref="UniverseId"/> + a validated/policy-controlled id allocator). v1's
/// structural guarantees (component fidelity, hierarchy remap, byte-identical round-trip) stay covered by
/// <see cref="WorldSerializationTests"/> — this file covers only what W3 adds.
/// </summary>
[Collection("World")]
public sealed class WorldSerializationV2Tests
{
    private static byte[] Save(GameWorld world)
    {
        using var ms = new MemoryStream();
        world.Save(ms);
        return ms.ToArray();
    }

    private static void PopulateOne(GameWorld world)
    {
        world.SpawnImported(new ImportedEntitySpec(
            new MeshHandle(1, 1), new MaterialHandle(1, 1), new Double3(1, 2, 3), Matrix4x4.Identity, Vector3.Zero, 1f, 0));
        world.FlushStructuralChanges();
    }

    // --- Version -----------------------------------------------------------------------------------------------

    [Fact]
    public void Save_WritesVersion2()
    {
        using var world = new GameWorld();
        var bytes = Save(world);
        Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void Load_RefusesV1WithATypedException_NamingTheReason()
    {
        using var producer = new GameWorld();
        PopulateOne(producer);
        var v2Bytes = Save(producer);

        var v1Bytes = (byte[])v2Bytes.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(v1Bytes.AsSpan(4, 4), 1);

        using var target = new GameWorld();
        var ex = Assert.Throws<WorldSerializationException>(() => target.Load(new MemoryStream(v1Bytes)));
        Assert.Contains("v1", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoundTrip_V2IsByteIdentical()
    {
        using var original = new GameWorld(GlobalIdRange.Default, new UniverseId(0x1122, 0x3344));
        PopulateOne(original);
        original.Spawn(new Double3(9, 9, 9), Quaternion.Identity, 1f);
        original.FlushStructuralChanges();

        var bytes = Save(original);

        using var restored = new GameWorld();
        restored.Load(new MemoryStream(bytes));

        Assert.Equal(bytes, Save(restored));
        Assert.Equal(original.LiveEntityCount, restored.LiveEntityCount);
    }

    [Fact]
    public void Load_ReturnsEntityCount()
    {
        using var producer = new GameWorld();
        PopulateOne(producer);
        producer.Spawn(Double3.Zero, Quaternion.Identity, 1f);
        producer.FlushStructuralChanges();
        var bytes = Save(producer);

        using var target = new GameWorld();
        var result = target.Load(new MemoryStream(bytes));

        Assert.Equal(2, result.EntityCount);
        Assert.Equal(target.LiveEntityCount, result.EntityCount);
    }

    // --- Universe reconciliation (5 cases) ----------------------------------------------------------------------

    [Fact]
    public void Universe_BothNone_StaysUnidentified()
    {
        using var producer = new GameWorld(); // UniverseId.None
        PopulateOne(producer);
        var bytes = Save(producer);

        using var target = new GameWorld(); // UniverseId.None
        var result = target.Load(new MemoryStream(bytes));

        Assert.Equal(UniverseId.None, target.Universe);
        Assert.Equal(UniverseOutcome.StayedUnidentified, result.Universe);
    }

    [Fact]
    public void Universe_SnapshotSet_WorldNone_Adopts()
    {
        var universe = new UniverseId(1, 2);
        using var producer = new GameWorld(GlobalIdRange.Default, universe);
        PopulateOne(producer);
        var bytes = Save(producer);

        using var target = new GameWorld(); // UniverseId.None
        var result = target.Load(new MemoryStream(bytes));

        Assert.Equal(universe, target.Universe);
        Assert.Equal(UniverseOutcome.Adopted, result.Universe);
    }

    [Fact]
    public void Universe_SnapshotNone_WorldSet_KeepsWorldsIdentity()
    {
        using var producer = new GameWorld(); // UniverseId.None
        PopulateOne(producer);
        var bytes = Save(producer);

        var universe = new UniverseId(7, 8);
        using var target = new GameWorld(GlobalIdRange.Default, universe);
        var result = target.Load(new MemoryStream(bytes));

        Assert.Equal(universe, target.Universe);
        Assert.Equal(UniverseOutcome.Kept, result.Universe);
    }

    [Fact]
    public void Universe_BothSetAndSame_Confirmed()
    {
        var universe = new UniverseId(42, 99);
        using var producer = new GameWorld(GlobalIdRange.Default, universe);
        PopulateOne(producer);
        var bytes = Save(producer);

        using var target = new GameWorld(GlobalIdRange.Default, universe);
        var result = target.Load(new MemoryStream(bytes)); // must not throw

        Assert.Equal(universe, target.Universe);
        Assert.Equal(UniverseOutcome.Confirmed, result.Universe);
    }

    [Fact]
    public void Universe_BothSetAndDifferent_Throws()
    {
        using var producer = new GameWorld(GlobalIdRange.Default, new UniverseId(1, 1));
        PopulateOne(producer);
        var bytes = Save(producer);

        using var target = new GameWorld(GlobalIdRange.Default, new UniverseId(2, 2));
        Assert.Throws<WorldSerializationException>(() => target.Load(new MemoryStream(bytes)));
    }

    // A rejected Load must not leave the world half-mutated (audit finding): the universe stays exactly what it
    // was constructed with, never flips to the snapshot's before the throw.
    [Fact]
    public void Universe_BothSetAndDifferent_WorldsUniverseUnchangedAfterThrow()
    {
        using var producer = new GameWorld(GlobalIdRange.Default, new UniverseId(1, 1));
        PopulateOne(producer);
        var bytes = Save(producer);

        var targetUniverse = new UniverseId(2, 2);
        using var target = new GameWorld(GlobalIdRange.Default, targetUniverse);
        Assert.ThrowsAny<Exception>(() => target.Load(new MemoryStream(bytes)));

        Assert.Equal(targetUniverse, target.Universe);
    }

    // --- Id allocator: validation + policy ----------------------------------------------------------------------

    [Fact]
    public void Load_AdoptFromHeader_OutOfRangeHeaderCounter_Throws()
    {
        using var producer = new GameWorld(new GlobalIdRange(1_000, 2_000));
        PopulateOne(producer);
        var bytes = Save(producer); // header nextGlobalId is inside [1000, 2000)

        // A world declaring a disjoint range must refuse to adopt a foreign allocator state silently.
        using var target = new GameWorld(new GlobalIdRange(1, 100));
        Assert.Throws<WorldSerializationException>(() => target.Load(new MemoryStream(bytes)));
    }

    [Fact]
    public void Load_AdoptFromHeader_HeaderCounterAtRangeEnd_DoesNotThrow()
    {
        // A world that consumed its whole block: after issuing the sole id in [5, 6), _nextGlobalId == 6 ==
        // EndExclusive. Save must still be loadable by a world declaring exactly that same range (inclusive-at-top).
        using var producer = new GameWorld(new GlobalIdRange(5, 6));
        PopulateOne(producer); // consumes the only id
        var bytes = Save(producer);

        using var target = new GameWorld(new GlobalIdRange(5, 6));
        target.Load(new MemoryStream(bytes)); // must not throw
        Assert.Equal(6UL, target.NextGlobalIdForTest);
    }

    [Fact]
    public void Load_KeepMine_IgnoresHeaderCounter_KeepsWorldsOwnRangeAndCounter()
    {
        using var producer = new GameWorld(new GlobalIdRange(1_000, 2_000));
        PopulateOne(producer);
        var bytes = Save(producer); // header nextGlobalId is ~1001, far outside the receiving world's own range

        var ownRange = new GlobalIdRange(50_000, 60_000);
        using var target = new GameWorld(ownRange);
        target.Load(new MemoryStream(bytes), SnapshotAllocatorPolicy.KeepMine);

        // KeepMine touches nothing: the world's range/counter are exactly what its OWN constructor set, never a
        // value carried in from Load (audit finding — this used to be a GlobalIdRange? parameter that could
        // reassign the lease at Load time; now the lease is fixed at construction only).
        Assert.Equal(ownRange, target.IdRange);
        Assert.Equal(ownRange.Start, target.NextGlobalIdForTest);
    }

    // Decision 1: a node legitimately holds entities it did not allocate. The loaded entity's GlobalId (well
    // outside the receiving world's own range) must survive untouched — Load never validates entity ids against
    // the range.
    [Fact]
    public void Load_KeepMine_KeepsOutOfRangeSerializedEntityIds_WithoutThrowing()
    {
        using var producer = new GameWorld(new GlobalIdRange(1, 100));
        var spec = new ImportedEntitySpec(
            new MeshHandle(1, 1), new MaterialHandle(1, 1), Double3.Zero, Matrix4x4.Identity, Vector3.Zero, 1f, 0);
        var entity = producer.SpawnDeferred(in spec);
        producer.FlushStructuralChanges();
        var bytes = Save(producer);

        using var target = new GameWorld(new GlobalIdRange(50_000, 60_000)); // disjoint from entity.Id (< 100)
        target.Load(new MemoryStream(bytes), SnapshotAllocatorPolicy.KeepMine);

        Assert.Equal(1, target.LiveEntityCount);
        Assert.True(entity.Id < 100);
    }

    // The 🔴 this milestone's audit found: a receiving world whose OWN range overlaps ids already present in a
    // loaded snapshot must not silently orphan the loaded entity when it later issues a colliding id itself — it
    // must fail loud (GameWorld.RegisterLive), never drop the earlier entity from _live without a trace.
    [Fact]
    public void Load_KeepMine_OwnRangeOverlappingLoadedIds_SpawningACollidingIdThrows()
    {
        using var producer = new GameWorld(new GlobalIdRange(1, 1_000));
        PopulateOne(producer); // consumes id 1
        var bytes = Save(producer);

        using var target = new GameWorld(new GlobalIdRange(1, 1_000)); // overlaps the snapshot's id 1 on purpose
        target.Load(new MemoryStream(bytes), SnapshotAllocatorPolicy.KeepMine);
        Assert.Equal(1, target.LiveEntityCount); // loaded entity 1 present

        // NextId() will hand out 1 again — must throw rather than silently overwrite _live[1]. SpawnImported is
        // immediate (unlike the deferred Spawn/SpawnDeferred), so the collision surfaces synchronously here.
        var spec = new ImportedEntitySpec(
            new MeshHandle(1, 1), new MaterialHandle(1, 1), Double3.Zero, Matrix4x4.Identity, Vector3.Zero, 1f, 0);
        Assert.Throws<InvalidOperationException>(() => target.SpawnImported(in spec));
    }
}
