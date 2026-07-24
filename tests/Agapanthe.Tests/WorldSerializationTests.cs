using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// World serialization (VS-1): the save/load round-trip that proves the "persistent universe" thesis. These pin the
/// format's guarantees — every archetype survives, the Parent hierarchy is remapped by GlobalId, the identity counter
/// is restored, and the round-trip is BYTE-IDENTICAL (Save(Load(bytes)) == bytes), the format's regression gate — plus
/// the malformed-input rejections that must throw <see cref="WorldSerializationException"/> rather than corrupt or crash.
/// </summary>
[Collection("World")]
public sealed class WorldSerializationTests
{
    private static RenderView ViewAt(Double3 origin)
        => new(origin, Vector3.Zero, Matrix4x4.Identity, Matrix4x4.Identity, 1f, 1f, 0.1f, 1f);

    private static ImportedEntitySpec Drawable(Double3 position, uint order)
        => new(new MeshHandle(3, 7), new MaterialHandle(5, 2), position, Matrix4x4.Identity, new Vector3(0.1f, 0.2f, 0.3f), 2.5f, order);

    // Builds a world exercising all three archetypes plus the NoShadowCast tag and a two-level parent hierarchy.
    private static GameWorld BuildPopulatedWorld()
    {
        var world = new GameWorld();

        // Imported drawables (one a non-shadow-caster → NoShadowCast tag).
        world.SpawnImported(Drawable(new Double3(1_000_000.5, -2.25, 3.75), 10));
        world.SpawnImported(Drawable(new Double3(-4e9, 12.5, 0), 11), castsShadow: false);

        // Physics body (adds Velocity + RigidBody).
        world.SpawnBody(Drawable(new Double3(5, 6, 7), 12), new Vector3(1.5f, 0f, -2f), inverseMass: 0.5f, restitution: 0.3f, radius: 1.25f);

        // Hierarchy: root → child → grandchild (Parent links, deferred → flush).
        var root = world.Spawn(new Double3(100, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), 2f);
        var child = world.Spawn(new Double3(0, 10, 0), Quaternion.Identity, 1f, root);
        world.Spawn(new Double3(0, 0, 5), Quaternion.Identity, 1f, child);
        world.FlushStructuralChanges();

        return world;
    }

    private static byte[] Save(GameWorld world)
    {
        using var ms = new MemoryStream();
        world.Save(ms);
        return ms.ToArray();
    }

    private static GameWorld Load(byte[] bytes)
    {
        var world = new GameWorld();
        using var ms = new MemoryStream(bytes);
        world.Load(ms);
        return world;
    }

    [Fact]
    public void RoundTrip_PreservesCountCounterAndHierarchy_AndIsByteIdentical()
    {
        using var original = BuildPopulatedWorld();

        // An extra un-parented root, to prove a parentless node round-trips alongside the hierarchy.
        original.Spawn(new Double3(0, 0, 0), Quaternion.Identity, 1f);
        original.FlushStructuralChanges();

        var bytes = Save(original);
        using var restored = Load(bytes);

        // Structural fidelity.
        Assert.Equal(original.LiveEntityCount, restored.LiveEntityCount);
        Assert.Equal(original.NextGlobalIdForTest, restored.NextGlobalIdForTest);

        // Byte-identical round-trip (R2): re-saving the restored world must reproduce the exact bytes. This is the
        // strongest format check — a dropped or corrupted component would change the re-save.
        var bytes2 = Save(restored);
        Assert.Equal(bytes, bytes2);
    }

    [Fact]
    public void RoundTrip_RemapsParentLinksByGlobalId()
    {
        using var original = new GameWorld();
        var root = original.Spawn(new Double3(100, 0, 0), Quaternion.Identity, 1f);
        var child = original.Spawn(new Double3(0, 10, 0), Quaternion.Identity, 1f, root);
        original.FlushStructuralChanges();

        var bytes = Save(original);
        using var restored = Load(bytes);

        // The format stores the parent's GlobalId (not an Arch Entity). After load, the child must point back at the
        // same root id — proving the two-pass remap wired the hierarchy from ids alone.
        Assert.Equal(root.Id, restored.ParentIdForTest(child));
        Assert.Equal(0ul, restored.ParentIdForTest(root)); // root has no parent
    }

    [Fact]
    public void RoundTrip_RestoredWorldPropagatesAndCollectsToTheSameRenderCount()
    {
        using var original = BuildPopulatedWorld();
        var originalRender = new RenderList();
        original.PropagateTransforms();
        original.CollectRenderLists(originalRender, new SceneCandidateSet(), ViewAt(Double3.Zero));

        var restored = Load(Save(original));
        var restoredRender = new RenderList();
        restored.PropagateTransforms(); // must not throw (hierarchy intact)
        restored.CollectRenderLists(restoredRender, new SceneCandidateSet(), ViewAt(Double3.Zero));
        restored.Dispose();

        Assert.Equal(originalRender.Count, restoredRender.Count);
    }

    [Fact]
    public void Save_IsDeterministic()
    {
        using var world = BuildPopulatedWorld();
        Assert.Equal(Save(world), Save(world)); // two saves of the same world are byte-identical
    }

    // --- Malformed input (VS-03 / R4): every failure is a typed WorldSerializationException -----------------------

    [Fact]
    public void Load_RejectsBadMagic()
    {
        var bytes = Save(BuildPopulatedWorld());
        bytes[0] ^= 0xFF; // corrupt the magic
        Assert.Throws<WorldSerializationException>(() => Load(bytes));
    }

    [Fact]
    public void Load_RejectsUnknownVersion()
    {
        var bytes = Save(BuildPopulatedWorld());
        bytes[4] = 0xEE; // bump the version field (offset 4) to something unsupported
        Assert.Throws<WorldSerializationException>(() => Load(bytes));
    }

    [Fact]
    public void Load_RejectsWrongComponentCount()
    {
        var bytes = Save(BuildPopulatedWorld());
        bytes[8] = 0x7F; // componentCount field (offset 8) → not the build's count
        Assert.Throws<WorldSerializationException>(() => Load(bytes));
    }

    [Fact]
    public void Load_RejectsTruncatedBody()
    {
        var bytes = Save(BuildPopulatedWorld());
        var truncated = bytes[..^5]; // drop the last 5 bytes → a record ends mid-stream
        Assert.Throws<WorldSerializationException>(() => Load(truncated));
    }

    [Fact]
    public void Load_RejectsOutOfRangeMaskBit()
    {
        var bytes = Save(BuildPopulatedWorld());
        // Header is magic(4)+version(4)+count(4)+nextId(8)+entityCount(4) = 24 bytes; then the first entity's
        // globalId(8); the presence mask is the u32 at offset 32. Set bit 31 → a component index beyond the count.
        bytes[35] |= 0x80;
        Assert.Throws<WorldSerializationException>(() => Load(bytes));
    }

    [Fact]
    public void Load_RejectsNonEmptyWorld()
    {
        var bytes = Save(BuildPopulatedWorld());
        using var target = new GameWorld();
        target.SpawnImported(Drawable(Double3.Zero, 0)); // populate it → Load must refuse
        using var ms = new MemoryStream(bytes);
        Assert.Throws<WorldSerializationException>(() => target.Load(ms));
    }
}
