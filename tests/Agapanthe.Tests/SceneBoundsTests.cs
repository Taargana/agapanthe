using System.Numerics;
using Agapanthe.Assets.Model;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

// Exercises SceneBuilder.ComputeWorldBounds — the pure, GPU-free world-space AABB fold that backs
// Scene.BoundsMin/Max/Center/Diagonal. Synthetic MeshAssets keep this testable without a device.
public class SceneBoundsTests
{
    private static MeshAsset Mesh(Matrix4x4 world, params Vector3[] positions)
        => new() { Positions = positions, Indices = [], WorldTransform = world };

    [Fact]
    public void ComputeWorldBounds_SingleIdentityMesh_FoldsLocalPositions()
    {
        var mesh = Mesh(
            Matrix4x4.Identity,
            new Vector3(-1f, -2f, -3f),
            new Vector3(4f, 5f, 6f),
            new Vector3(0f, 1f, 0f));

        var (min, max) = SceneBuilder.ComputeWorldBounds([mesh]);

        Assert.Equal(new Vector3(-1f, -2f, -3f), min);
        Assert.Equal(new Vector3(4f, 5f, 6f), max);
    }

    [Fact]
    public void ComputeWorldBounds_AppliesWorldTransform()
    {
        // A unit cube [0,1]^3 translated by (10,20,30) must land at [10,11]×[20,21]×[30,31].
        var mesh = Mesh(
            Matrix4x4.CreateTranslation(10f, 20f, 30f),
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 1f, 1f));

        var (min, max) = SceneBuilder.ComputeWorldBounds([mesh]);

        Assert.Equal(new Vector3(10f, 20f, 30f), min);
        Assert.Equal(new Vector3(11f, 21f, 31f), max);
    }

    [Fact]
    public void ComputeWorldBounds_MultipleMeshes_UnionsBoxes()
    {
        var a = Mesh(Matrix4x4.Identity, new Vector3(-5f, 0f, 0f), new Vector3(0f, 2f, 0f));
        var b = Mesh(Matrix4x4.CreateTranslation(0f, 0f, 7f), new Vector3(0f, -3f, 0f), new Vector3(4f, 0f, 0f));

        var (min, max) = SceneBuilder.ComputeWorldBounds([a, b]);

        Assert.Equal(new Vector3(-5f, -3f, 0f), min);
        Assert.Equal(new Vector3(4f, 2f, 7f), max);
    }

    [Fact]
    public void ComputeWorldBounds_EmptyModel_CollapsesToOrigin()
    {
        var (min, max) = SceneBuilder.ComputeWorldBounds([]);

        Assert.Equal(Vector3.Zero, min);
        Assert.Equal(Vector3.Zero, max);
    }

    [Fact]
    public void ComputeWorldBounds_MeshesWithoutPositions_CollapseToOrigin()
    {
        var empty = Mesh(Matrix4x4.CreateTranslation(100f, 100f, 100f));

        var (min, max) = SceneBuilder.ComputeWorldBounds([empty]);

        Assert.Equal(Vector3.Zero, min);
        Assert.Equal(Vector3.Zero, max);
    }

    [Fact]
    public void ComputeWorldBounds_DerivedCenterAndDiagonal_MatchExpected()
    {
        // Box [0,2]×[0,4]×[0,4]: centre (1,2,2), diagonal = sqrt(4+16+16) = 6.
        var mesh = Mesh(Matrix4x4.Identity, Vector3.Zero, new Vector3(2f, 4f, 4f));

        var (min, max) = SceneBuilder.ComputeWorldBounds([mesh]);
        var center = (min + max) * 0.5f;
        var diagonal = Vector3.Distance(min, max);

        Assert.Equal(new Vector3(1f, 2f, 2f), center);
        Assert.Equal(6f, diagonal, 5);
    }

    [Fact]
    public void ComputeWorldBounds_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => SceneBuilder.ComputeWorldBounds(null!));
}
