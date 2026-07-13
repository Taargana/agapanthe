using System.Numerics;
using Agapanthe.Assets.Model;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

// Exercises the PRODUCTION bounds path: SceneBuilder.ComputeMeshLocalSphere, the per-mesh LOCAL bounding sphere
// that BuildEntries bakes into each ImportedEntitySpec (M4, spec §3.4). The world transforms it per frame
// (covered by WorldSystemsTests.AggregateBounds). Local, not world: the entity's placement travels separately.
// Synthetic MeshAssets keep this testable without a device.
public class SceneBoundsTests
{
    private static MeshAsset Mesh(params Vector3[] positions)
        => new() { Positions = positions, Indices = [], WorldTransform = Matrix4x4.Identity };

    [Fact]
    public void Sphere_CentreIsTheLocalAabbCentre()
    {
        // AABB [-1,4]×[-2,5]×[-3,6] → centre (1.5, 1.5, 1.5).
        var (center, _) = SceneBuilder.ComputeMeshLocalSphere(Mesh(
            new Vector3(-1f, -2f, -3f),
            new Vector3(4f, 5f, 6f),
            new Vector3(0f, 1f, 0f)));

        Assert.Equal(new Vector3(1.5f, 1.5f, 1.5f), center);
    }

    [Fact]
    public void Sphere_RadiusReachesTheFarthestVertex_NotTheAabbCorner()
    {
        // Two vertices symmetric about the origin on X: centre (0,0,0), radius = 5 (the vertex distance), NOT the
        // AABB half-diagonal. A vertex-tight radius keeps culling false positives down.
        var (center, radius) = SceneBuilder.ComputeMeshLocalSphere(Mesh(
            new Vector3(-5f, 0f, 0f),
            new Vector3(5f, 0f, 0f)));

        Assert.Equal(Vector3.Zero, center);
        Assert.Equal(5f, radius, 5);
    }

    [Fact]
    public void Sphere_EnclosesEveryVertex()
    {
        var mesh = Mesh(
            new Vector3(1f, 2f, 3f),
            new Vector3(-4f, 0f, 2f),
            new Vector3(2f, -6f, 1f),
            new Vector3(0f, 3f, -5f));

        var (center, radius) = SceneBuilder.ComputeMeshLocalSphere(mesh);

        foreach (var p in mesh.Positions)
        {
            Assert.True(Vector3.Distance(center, p) <= radius + 1e-4f, $"vertex {p} escapes the sphere");
        }
    }

    [Fact]
    public void Sphere_IsLocal_IgnoresTheWorldTransform()
    {
        // The world matrix must NOT enter the local sphere: the entity's placement lives in its
        // WorldTransform/WorldPosition and is applied per frame, not baked here.
        var mesh = new MeshAsset
        {
            Positions = [new Vector3(0f, 0f, 0f), new Vector3(2f, 0f, 0f)],
            Indices = [],
            WorldTransform = Matrix4x4.CreateTranslation(1000f, 1000f, 1000f),
        };

        var (center, radius) = SceneBuilder.ComputeMeshLocalSphere(mesh);

        Assert.Equal(new Vector3(1f, 0f, 0f), center); // local centre, not shifted by 1000
        Assert.Equal(1f, radius, 5);
    }

    [Fact]
    public void Sphere_NoPositions_IsAZeroSphereAtTheOrigin()
    {
        var (center, radius) = SceneBuilder.ComputeMeshLocalSphere(Mesh());

        Assert.Equal(Vector3.Zero, center);
        Assert.Equal(0f, radius);
    }

    [Fact]
    public void ComputeMeshLocalSphere_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => SceneBuilder.ComputeMeshLocalSphere(null!));
}
