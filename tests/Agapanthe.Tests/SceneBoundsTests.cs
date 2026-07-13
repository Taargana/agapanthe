using System.Numerics;
using Agapanthe.Assets.Model;
using Agapanthe.Core;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

// Exercises the PRODUCTION bounds path: SceneBuilder.ComputeMeshWorldBounds (the per-mesh float fold) unioned
// through Double3Bounds.Union — which is exactly what SceneBuilder.BuildSpecs feeds the world and what the
// world's system 2 folds back together. It replaces the old whole-model ComputeWorldBounds (removed: it was
// dead in production and had become a second, diverging source of truth — audit M2 MEDIUM-2).
// Synthetic MeshAssets keep this testable without a device.
public class SceneBoundsTests
{
    private static MeshAsset Mesh(Matrix4x4 world, params Vector3[] positions)
        => new() { Positions = positions, Indices = [], WorldTransform = world };

    // The production fold: per-mesh bounds widened to Double3 and unioned, as BuildSpecs + system 2 do.
    private static Double3Bounds Fold(params MeshAsset[] meshes)
    {
        var acc = Double3Bounds.Empty;
        foreach (var mesh in meshes)
        {
            var (min, max) = SceneBuilder.ComputeMeshWorldBounds(mesh);
            acc = Double3Bounds.Union(acc, new Double3Bounds(new Double3(min), new Double3(max)));
        }

        return acc;
    }

    // What consumers (camera framing, shadow fit) actually read: narrow to float, guarding the empty fold.
    private static (Vector3 Min, Vector3 Max) Narrow(in Double3Bounds b)
        => b.IsEmpty
            ? (Vector3.Zero, Vector3.Zero)
            : (b.Min.ToVector3(Double3.Zero), b.Max.ToVector3(Double3.Zero));

    [Fact]
    public void SingleIdentityMesh_FoldsLocalPositions()
    {
        var mesh = Mesh(
            Matrix4x4.Identity,
            new Vector3(-1f, -2f, -3f),
            new Vector3(4f, 5f, 6f),
            new Vector3(0f, 1f, 0f));

        var (min, max) = Narrow(Fold(mesh));

        Assert.Equal(new Vector3(-1f, -2f, -3f), min);
        Assert.Equal(new Vector3(4f, 5f, 6f), max);
    }

    [Fact]
    public void AppliesWorldTransform()
    {
        // A unit cube [0,1]^3 translated by (10,20,30) must land at [10,11]×[20,21]×[30,31].
        var mesh = Mesh(
            Matrix4x4.CreateTranslation(10f, 20f, 30f),
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 1f, 1f));

        var (min, max) = Narrow(Fold(mesh));

        Assert.Equal(new Vector3(10f, 20f, 30f), min);
        Assert.Equal(new Vector3(11f, 21f, 31f), max);
    }

    [Fact]
    public void MultipleMeshes_UnionsBoxes()
    {
        var a = Mesh(Matrix4x4.Identity, new Vector3(-5f, 0f, 0f), new Vector3(0f, 2f, 0f));
        var b = Mesh(Matrix4x4.CreateTranslation(0f, 0f, 7f), new Vector3(0f, -3f, 0f), new Vector3(4f, 0f, 0f));

        var (min, max) = Narrow(Fold(a, b));

        Assert.Equal(new Vector3(-5f, -3f, 0f), min);
        Assert.Equal(new Vector3(4f, 2f, 7f), max);
    }

    [Fact]
    public void EmptyModel_CollapsesToOrigin()
    {
        var (min, max) = Narrow(Fold());

        Assert.Equal(Vector3.Zero, min);
        Assert.Equal(Vector3.Zero, max);
    }

    [Fact]
    public void MeshesWithoutPositions_CollapseToOrigin()
    {
        var empty = Mesh(Matrix4x4.CreateTranslation(100f, 100f, 100f));

        var (min, max) = Narrow(Fold(empty));

        Assert.Equal(Vector3.Zero, min);
        Assert.Equal(Vector3.Zero, max);
    }

    [Fact]
    public void EmptyMesh_DoesNotDragBoundsBackToTheOrigin()
    {
        // Audit M2 MEDIUM-1, the regression this guards: an EMPTY primitive must contribute NOTHING to the fold.
        // A per-mesh (0,0,0) fallback is not the neutral element of a union — it would pull a model sitting at
        // (1000,1000,1000) back to the origin, doubling the extent and silently moving the camera framing and
        // the shadow matrix. The empty fold (inverted infinities) IS neutral.
        var far = Mesh(
            Matrix4x4.CreateTranslation(1000f, 1000f, 1000f),
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 1f, 1f));
        var empty = Mesh(Matrix4x4.Identity);

        var withEmpty = Narrow(Fold(far, empty));
        var withoutEmpty = Narrow(Fold(far));

        Assert.Equal(withoutEmpty.Min, withEmpty.Min);
        Assert.Equal(withoutEmpty.Max, withEmpty.Max);
        Assert.Equal(new Vector3(1000f, 1000f, 1000f), withEmpty.Min); // not dragged to (0,0,0)
    }

    [Fact]
    public void DerivedCenterAndDiagonal_MatchExpected()
    {
        // Box [0,2]×[0,4]×[0,4]: centre (1,2,2), diagonal = sqrt(4+16+16) = 6.
        var mesh = Mesh(Matrix4x4.Identity, Vector3.Zero, new Vector3(2f, 4f, 4f));

        var (min, max) = Narrow(Fold(mesh));
        var center = (min + max) * 0.5f;
        var diagonal = Vector3.Distance(min, max);

        Assert.Equal(new Vector3(1f, 2f, 2f), center);
        Assert.Equal(6f, diagonal, 5);
    }

    [Fact]
    public void ComputeMeshWorldBounds_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => SceneBuilder.ComputeMeshWorldBounds(null!));
}
