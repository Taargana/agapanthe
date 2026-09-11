using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// Contenu-3b — <see cref="GameWorld.ResolveMeshRefs"/> fills the process-local <c>MeshRef</c> render cache from
/// each drawable's stored <c>AssetRef</c> identity (the client path, after uploading models). A <c>None</c>
/// identity is left untouched; the resolver's throw-on-unknown contract propagates.
/// </summary>
[Collection("World")]
public sealed class GameWorldResolveMeshRefsTests
{
    private static ImportedEntitySpec Keyed(string key) => new(
        MeshHandle.Invalid, MaterialHandle.Invalid, Double3.Zero, Matrix4x4.Identity, Vector3.Zero, 1f, 0,
        new MeshRefKey(new AssetKey(key), 2, 3));

    [Fact]
    public void ResolveMeshRefs_FillsInvalidMeshRef_FromAssetRef()
    {
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        world.SpawnImported(Keyed("models/helmet"));

        Assert.False(world.MeshRefForTest(id)!.Value.Mesh.IsValid);

        world.ResolveMeshRefs((key, localMesh, localMat) =>
        {
            Assert.Equal(new AssetKey("models/helmet"), key);
            Assert.Equal(2, localMesh);
            Assert.Equal(3, localMat);
            return (new MeshHandle(7, 1), new MaterialHandle(4, 1));
        });

        var handles = world.MeshRefForTest(id)!.Value;
        Assert.Equal(new MeshHandle(7, 1), handles.Mesh);
        Assert.Equal(new MaterialHandle(4, 1), handles.Material);
    }

    [Fact]
    public void ResolveMeshRefs_LeavesNoneIdentityAlone()
    {
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        world.SpawnImported(new ImportedEntitySpec(
            MeshHandle.Invalid, MaterialHandle.Invalid, Double3.Zero, Matrix4x4.Identity, Vector3.Zero, 1f, 0));

        world.ResolveMeshRefs((_, _, _) => throw new Xunit.Sdk.XunitException("must not be called for a None identity"));

        Assert.False(world.MeshRefForTest(id)!.Value.Mesh.IsValid);
    }

    [Fact]
    public void ResolveMeshRefs_PropagatesResolverThrow()
    {
        using var world = new GameWorld();
        world.SpawnImported(Keyed("models/unknown"));

        Assert.Throws<InvalidOperationException>(() =>
            world.ResolveMeshRefs((_, _, _) => throw new InvalidOperationException("no such key")));
    }

    [Fact]
    public void ResolveMeshRefs_NullResolver_Throws()
    {
        using var world = new GameWorld();
        Assert.Throws<ArgumentNullException>(() => world.ResolveMeshRefs(null!));
    }
}
