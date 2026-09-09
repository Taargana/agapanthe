using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// Contenu-3a AW-007 — <c>AssetRef</c> is the project's first <b>managed</b> ECS component (it carries an
/// <see cref="AssetKey"/> string). This proves it survives the archetype-move paths (structural spawn/despawn
/// flushed at a barrier) with its value intact, in JIT. The NativeAOT proof is <c>AotComponentProbe</c>
/// (<c>AotRootingSmoke</c> now iterates 13 components).
/// </summary>
[Collection("World")]
public sealed class AssetRefComponentTests
{
    private static ImportedEntitySpec KeyedSpec(int h, string key) => new(
        new MeshHandle(h, 1), new MaterialHandle(h, 1), new Double3(h, 0, 0), Matrix4x4.Identity, Vector3.Zero, 1f,
        (uint)h, new MeshRefKey(new AssetKey(key), h, h + 1));

    [Fact]
    public void MaterialiseDrawable_StampsAssetRef_FromSpecIdentity()
    {
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        world.SpawnImported(KeyedSpec(3, "models/helmet"));

        Assert.Equal(new MeshRefKey(new AssetKey("models/helmet"), 3, 4), world.AssetRefForTest(id));
    }

    [Fact]
    public void HandAuthoredSpec_DefaultIdentity_IsNone()
    {
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        world.SpawnImported(new ImportedEntitySpec(
            new MeshHandle(1, 1), new MaterialHandle(1, 1), Double3.Zero, Matrix4x4.Identity, Vector3.Zero, 1f, 0));

        Assert.Equal(MeshRefKey.None, world.AssetRefForTest(id));
    }

    [Fact]
    public void AssetRef_SurvivesStructuralChurn_ValueIntact()
    {
        using var world = new GameWorld();

        var a = world.NextGlobalIdForTest;
        world.SpawnImported(KeyedSpec(10, "a"));
        var b = world.NextGlobalIdForTest;
        world.SpawnImported(KeyedSpec(11, "models/b"));
        var c = world.NextGlobalIdForTest;
        world.SpawnBody(KeyedSpec(12, "c/sphere"), new Vector3(0, -1, 0), inverseMass: 1f, restitution: 0.2f, radius: 0.5f);

        // structural churn at a barrier: deferred keyed bodies + a hierarchy + despawns → archetype moves and, for
        // the despawned KEYED body, Arch's swap-backfill copy over the managed AssetRef[] chunk array.
        var churn = new List<EntityRef>();
        for (var i = 0; i < 6; i++)
        {
            churn.Add(world.SpawnBodyDeferred(KeyedSpec(20 + i, $"churn/{i}"), Vector3.Zero, 1f, 0.3f, 0.4f));
        }

        var root = world.Spawn(new Double3(100, 0, 0), Quaternion.Identity, 1f);
        world.Spawn(new Double3(1, 0, 0), Quaternion.Identity, 1f, root);
        world.FlushStructuralChanges();
        world.Despawn(root);
        world.Despawn(churn[2]); // a keyed drawable in the middle of a chunk → forces the backfill copy
        world.Despawn(churn[4]);
        world.FlushStructuralChanges();

        Assert.Equal(new MeshRefKey(new AssetKey("a"), 10, 11), world.AssetRefForTest(a));
        Assert.Equal(new MeshRefKey(new AssetKey("models/b"), 11, 12), world.AssetRefForTest(b));
        Assert.Equal(new MeshRefKey(new AssetKey("c/sphere"), 12, 13), world.AssetRefForTest(c));
    }
}
