using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0b W3: the v2 snapshot header (<see cref="UniverseId"/> + a validated/policy-controlled id allocator), plus
/// Contenu-1's v3 additions (the <see cref="AssetKey"/> key table + <c>MeshRef</c> re-resolution via
/// <see cref="MeshRefIdentifier"/>/<see cref="MeshRefResolver"/>). v1's structural guarantees (component fidelity,
/// hierarchy remap, byte-identical round-trip) stay covered by <see cref="WorldSerializationTests"/>.
/// </summary>
[Collection("World")]
public sealed class WorldSerializationV3Tests
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
    public void Save_WritesVersion3()
    {
        using var world = new GameWorld();
        var bytes = Save(world);
        Assert.Equal(3u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void Load_RefusesV1WithATypedException_NamingTheReason()
    {
        using var producer = new GameWorld();
        PopulateOne(producer);
        var v3Bytes = Save(producer);

        var v1Bytes = (byte[])v3Bytes.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(v1Bytes.AsSpan(4, 4), 1);

        using var target = new GameWorld();
        var ex = Assert.Throws<WorldSerializationException>(() => target.Load(new MemoryStream(v1Bytes)));
        Assert.Contains("v1", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_RefusesV2WithATypedException_NamingTheReason()
    {
        using var producer = new GameWorld();
        PopulateOne(producer);
        var v3Bytes = Save(producer);

        var v2Bytes = (byte[])v3Bytes.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(v2Bytes.AsSpan(4, 4), 2);

        using var target = new GameWorld();
        var ex = Assert.Throws<WorldSerializationException>(() => target.Load(new MemoryStream(v2Bytes)));
        Assert.Contains("v2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoundTrip_V3IsByteIdentical()
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

    // --- Contenu-1: the AssetKey key table + MeshRef re-resolution ---------------------------------------------

    private static readonly MeshHandle KnownMesh = new(7, 1);
    private static readonly MaterialHandle KnownMaterial = new(9, 1);
    private static readonly AssetKey KnownKey = new("models/x");

    // identify: the one drawable's handles → (KnownKey, local 3, local 5); anything else → None.
    private static MeshRefKey Identify(MeshHandle mesh, MaterialHandle material)
        => mesh == KnownMesh && material == KnownMaterial ? new MeshRefKey(KnownKey, 3, 5) : MeshRefKey.None;

    // resolve: the inverse — only KnownKey/3/5 is resolvable, everything else throws (the delegate contract).
    private static (MeshHandle, MaterialHandle) Resolve(AssetKey key, int localMesh, int localMat)
        => key == KnownKey && localMesh == 3 && localMat == 5
            ? (KnownMesh, KnownMaterial)
            : throw new InvalidOperationException($"unresolvable asset {key} [{localMesh},{localMat}]");

    private static GameWorld WorldWithOneKnownDrawable()
    {
        var world = new GameWorld();
        world.SpawnImported(new ImportedEntitySpec(
            KnownMesh, KnownMaterial, new Double3(1, 2, 3), Matrix4x4.Identity, Vector3.Zero, 1f, 0));
        world.FlushStructuralChanges();
        return world;
    }

    [Fact]
    public void Save_WithIdentifier_WritesTheKeyInTheTable()
    {
        using var world = WorldWithOneKnownDrawable();

        using var ms = new MemoryStream();
        world.Save(ms, Identify);
        var bytes = ms.ToArray();

        // keyCount at offset 40, then entry 0 (byteLen 0), then entry 1 = "models/x".
        Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4)));
        Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(44, 2)));
        var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(46, 2));
        Assert.Equal("models/x", System.Text.Encoding.UTF8.GetString(bytes.AsSpan(48, len)));
    }

    [Fact]
    public void RoundTrip_WithDelegates_RebuildsHandles_AndIsByteIdentical()
    {
        using var original = WorldWithOneKnownDrawable();
        var drawableId = original.NextGlobalIdForTest - 1; // the id just consumed by SpawnImported

        using var ms = new MemoryStream();
        original.Save(ms, Identify);
        var bytes = ms.ToArray();

        using var restored = new GameWorld();
        restored.Load(new MemoryStream(bytes), SnapshotAllocatorPolicy.AdoptFromHeader, Resolve);

        Assert.Equal((KnownMesh, KnownMaterial), restored.MeshRefForTest(drawableId));

        using var ms2 = new MemoryStream();
        restored.Save(ms2, Identify);
        Assert.Equal(bytes, ms2.ToArray()); // re-save reproduces the exact key table + indices
    }

    [Fact]
    public void RoundTrip_WithDeterministicKeyTable_IsByteIdenticalRunToRun()
    {
        using var world = new GameWorld();
        // Three drawables that map to three different keys — the table must come out ordinal-sorted, stably.
        foreach (var (mh, key) in new[] { (10, "b"), (11, "a"), (12, "c") })
        {
            world.SpawnImported(new ImportedEntitySpec(
                new MeshHandle(mh, 1), new MaterialHandle(1, 1), Double3.Zero, Matrix4x4.Identity, Vector3.Zero, 1f, 0));
        }

        world.FlushStructuralChanges();

        MeshRefKey Id(MeshHandle m, MaterialHandle _) => m.Index switch
        {
            10 => new MeshRefKey(new AssetKey("b"), 0, 0),
            11 => new MeshRefKey(new AssetKey("a"), 0, 0),
            12 => new MeshRefKey(new AssetKey("c"), 0, 0),
            _ => MeshRefKey.None,
        };

        byte[] Once()
        {
            using var ms = new MemoryStream();
            world.Save(ms, Id);
            return ms.ToArray();
        }

        Assert.Equal(Once(), Once());
    }

    [Fact]
    public void Load_NoResolver_MeshRefBecomesInvalid_EvenWithAPopulatedKeyTable()
    {
        using var original = WorldWithOneKnownDrawable();
        var drawableId = original.NextGlobalIdForTest - 1;

        using var ms = new MemoryStream();
        original.Save(ms, Identify); // key table has "models/x"
        var bytes = ms.ToArray();

        using var restored = new GameWorld();
        restored.Load(new MemoryStream(bytes)); // 1-arg Load → resolve is null

        Assert.Equal((MeshHandle.Invalid, MaterialHandle.Invalid), restored.MeshRefForTest(drawableId));
    }

    [Fact]
    public void Save_NoIdentifier_MeshRefRoundTripsAsNone()
    {
        using var original = WorldWithOneKnownDrawable();
        var drawableId = original.NextGlobalIdForTest - 1;

        var bytes = Save(original); // no identifier → every MeshRef is None
        Assert.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4)));

        using var restored = new GameWorld();
        restored.Load(new MemoryStream(bytes), SnapshotAllocatorPolicy.AdoptFromHeader, Resolve);
        Assert.Equal((MeshHandle.Invalid, MaterialHandle.Invalid), restored.MeshRefForTest(drawableId));
    }

    [Fact]
    public void Load_ResolverThatThrows_PropagatesTheFailure()
    {
        using var original = WorldWithOneKnownDrawable();

        using var ms = new MemoryStream();
        original.Save(ms, Identify);
        var bytes = ms.ToArray();

        using var restored = new GameWorld();
        (MeshHandle, MaterialHandle) Boom(AssetKey key, int a, int b) => throw new InvalidOperationException("no such asset");

        Assert.Throws<InvalidOperationException>(
            () => restored.Load(new MemoryStream(bytes), SnapshotAllocatorPolicy.AdoptFromHeader, Boom));
    }

    [Fact]
    public void Load_RejectsMeshRefKeyIndexBeyondTheTable()
    {
        using var original = WorldWithOneKnownDrawable();

        using var ms = new MemoryStream();
        original.Save(ms, Identify);
        var bytes = ms.ToArray();

        // The MeshRef payload is written as keyIdx(1) | localMesh(3) | localMat(5) — a unique LE byte run in a
        // world with one drawable. Overwrite the keyIdx with a value past the 2-entry key table.
        ReadOnlySpan<byte> meshRefRun = [1, 0, 0, 0, 3, 0, 0, 0, 5, 0, 0, 0];
        var at = bytes.AsSpan().IndexOf(meshRefRun);
        Assert.True(at >= 0, "MeshRef payload run not found");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at, 4), 999);

        using var restored = new GameWorld();
        Assert.Throws<WorldSerializationException>(
            () => restored.Load(new MemoryStream(bytes), SnapshotAllocatorPolicy.AdoptFromHeader, Resolve));
    }

    // --- Contenu-1 (audit): key-table corruption is rejected, and rejected BEFORE any state mutation -----------

    [Fact]
    public void Load_RejectsZeroKeyCount()
    {
        using var producer = new GameWorld();
        PopulateOne(producer);
        var bytes = Save(producer); // keyCount at offset 40 == 1 (just None)

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), 0);
        using var target = new GameWorld();
        Assert.Throws<WorldSerializationException>(() => target.Load(new MemoryStream(bytes)));
    }

    [Fact]
    public void Load_RejectsKeyTableEntryZeroWithNonZeroLength()
    {
        using var producer = new GameWorld();
        PopulateOne(producer);
        var bytes = Save(producer);

        // entry 0's byteLen u16 sits right after keyCount, at offset 44 — must be 0 (AssetKey.None).
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(44, 2), 3);
        using var target = new GameWorld();
        Assert.Throws<WorldSerializationException>(() => target.Load(new MemoryStream(bytes)));
    }

    [Fact]
    public void Load_RejectsNonAscendingKeyTable()
    {
        using var world = new GameWorld();
        foreach (var mh in new[] { 10, 11 })
        {
            world.SpawnImported(new ImportedEntitySpec(
                new MeshHandle(mh, 1), new MaterialHandle(1, 1), Double3.Zero, Matrix4x4.Identity, Vector3.Zero, 1f, 0));
        }

        world.FlushStructuralChanges();
        MeshRefKey Id(MeshHandle m, MaterialHandle _) => new(new AssetKey(m.Index == 10 ? "a" : "b"), 0, 0);

        using var ms = new MemoryStream();
        world.Save(ms, Id);
        var bytes = ms.ToArray();

        // Table is ["", "a", "b"] at offset 40: keyCount(4)=3, [len(2)=0], [len(2)=1 "a"], [len(2)=1 "b"].
        // Swap the two payload bytes 'a' (0x61) and 'b' (0x62) → table becomes ["", "b", "a"], not ascending.
        var ai = bytes.AsSpan().IndexOf((byte)'a');
        var bi = bytes.AsSpan().IndexOf((byte)'b');
        (bytes[ai], bytes[bi]) = (bytes[bi], bytes[ai]);

        using var target = new GameWorld();
        Assert.Throws<WorldSerializationException>(() => target.Load(new MemoryStream(bytes)));
    }

    [Fact]
    public void Load_RejectsInvalidUtf8InKeyTable()
    {
        using var world = WorldWithOneKnownDrawable();
        using var ms = new MemoryStream();
        world.Save(ms, Identify); // table ["", "models/x"]
        var bytes = ms.ToArray();

        // Corrupt the first byte of "models/x" to a lone continuation byte (0x80) — invalid UTF-8.
        var mi = bytes.AsSpan().IndexOf("models/x"u8);
        Assert.True(mi >= 0);
        bytes[mi] = 0x80;

        using var target = new GameWorld();
        Assert.Throws<WorldSerializationException>(() => target.Load(new MemoryStream(bytes)));
    }

    [Fact]
    public void Load_CorruptKeyTable_LeavesWorldUnchanged()
    {
        using var producer = new GameWorld(GlobalIdRange.Default, new UniverseId(5, 6));
        PopulateOne(producer);
        var bytes = Save(producer);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), 0); // keyCount = 0

        using var target = new GameWorld(); // UniverseId.None
        Assert.Throws<WorldSerializationException>(() => target.Load(new MemoryStream(bytes)));

        // The key table is read + validated before any state change (audit): a rejected Load must not have
        // adopted the snapshot's universe or advanced the id counter.
        Assert.Equal(UniverseId.None, target.Universe);
        Assert.Equal(0, target.LiveEntityCount);
    }

    [Fact]
    public void Save_AfterLoadWithoutResolver_DoesNotThrowOnInvalidHandles()
    {
        using var original = WorldWithOneKnownDrawable();
        using var ms = new MemoryStream();
        original.Save(ms, Identify);

        using var restored = new GameWorld();
        restored.Load(new MemoryStream(ms.ToArray())); // no resolver → MeshRef becomes Invalid

        // Re-saving with an identifier must treat the Invalid drawable as None, not blow up (audit finding).
        using var ms2 = new MemoryStream();
        restored.Save(ms2, Identify);
        Assert.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(ms2.ToArray().AsSpan(40, 4)));
    }
}
