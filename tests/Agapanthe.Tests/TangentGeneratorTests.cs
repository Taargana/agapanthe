using System.Numerics;
using Agapanthe.Assets;

namespace Agapanthe.Tests;

public class TangentGeneratorTests
{
    private const float Tolerance = 1e-4f;

    /// <summary>Flat quad in the XY plane, normal +Z, standard 0..1 UVs (u right, v up).</summary>
    private static (Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, uint[] Indices) FlatQuad(bool mirrorU = false)
    {
        var positions = new Vector3[]
        {
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
        };
        var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
        var uvs = mirrorU
            ? new Vector2[] { new(1, 0), new(0, 0), new(0, 1), new(1, 1) }
            : new Vector2[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
        var indices = new uint[] { 0, 1, 2, 0, 2, 3 };
        return (positions, normals, uvs, indices);
    }

    [Fact]
    public void FlatQuad_ProducesUnitXTangentWithPositiveHandedness()
    {
        var (positions, normals, uvs, indices) = FlatQuad();
        var tangents = TangentGenerator.Generate(positions, normals, uvs, indices);

        Assert.All(tangents, t =>
        {
            Assert.True((new Vector3(t.X, t.Y, t.Z) - Vector3.UnitX).Length() < Tolerance, $"expected +X tangent, got {t}");
            Assert.Equal(1f, t.W);
        });
    }

    [Fact]
    public void MirroredU_FlipsTangentAndHandedness()
    {
        var (positions, normals, uvs, indices) = FlatQuad(mirrorU: true);
        var tangents = TangentGenerator.Generate(positions, normals, uvs, indices);

        Assert.All(tangents, t =>
        {
            Assert.True((new Vector3(t.X, t.Y, t.Z) + Vector3.UnitX).Length() < Tolerance, $"expected -X tangent, got {t}");
            Assert.Equal(-1f, t.W);
        });
    }

    [Fact]
    public void UvSphere_TangentsAreUnitLengthAndPerpendicularToNormals()
    {
        // 8x8-segment UV sphere generated inline.
        const int segments = 8;
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        for (var ring = 0; ring <= segments; ring++)
        {
            var phi = MathF.PI * ring / segments;
            for (var seg = 0; seg <= segments; seg++)
            {
                var theta = 2f * MathF.PI * seg / segments;
                var n = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                positions.Add(n);
                normals.Add(n);
                uvs.Add(new Vector2((float)seg / segments, (float)ring / segments));
            }
        }

        var indices = new List<uint>();
        for (var ring = 0; ring < segments; ring++)
        {
            for (var seg = 0; seg < segments; seg++)
            {
                var a = (uint)((ring * (segments + 1)) + seg);
                var b = a + segments + 1;
                indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
        }

        var tangents = TangentGenerator.Generate(
            positions.ToArray(), normals.ToArray(), uvs.ToArray(), indices.ToArray());

        for (var i = 0; i < tangents.Length; i++)
        {
            var t = new Vector3(tangents[i].X, tangents[i].Y, tangents[i].Z);
            Assert.False(float.IsNaN(t.X) || float.IsNaN(t.Y) || float.IsNaN(t.Z), $"NaN tangent at {i}");
            Assert.True(MathF.Abs(t.Length() - 1f) < Tolerance, $"non-unit tangent at {i}: {t}");
            Assert.True(MathF.Abs(Vector3.Dot(t, normals[i])) < Tolerance, $"tangent not ⊥ normal at {i}");
            Assert.True(MathF.Abs(tangents[i].W) == 1f, $"handedness not ±1 at {i}: {tangents[i].W}");
        }
    }

    [Fact]
    public void DegenerateUvs_FallBackToValidTangents()
    {
        var (positions, normals, _, indices) = FlatQuad();
        var uvs = new[] { Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero };

        var tangents = TangentGenerator.Generate(positions, normals, uvs, indices);

        Assert.All(tangents, t =>
        {
            var xyz = new Vector3(t.X, t.Y, t.Z);
            Assert.False(float.IsNaN(xyz.X) || float.IsNaN(xyz.Y) || float.IsNaN(xyz.Z));
            Assert.True(MathF.Abs(xyz.Length() - 1f) < Tolerance);
            Assert.True(MathF.Abs(Vector3.Dot(xyz, Vector3.UnitZ)) < Tolerance, "fallback must stay ⊥ N");
            Assert.Equal(1f, t.W);
        });
    }

    [Fact]
    public void SharedVerticesAcrossOpposedFaces_ProduceNoNaN()
    {
        // Two triangles sharing an edge but facing opposite directions with clashing UV gradients.
        var positions = new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0) };
        var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ };
        var uvs = new Vector2[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
        var indices = new uint[] { 0, 1, 2, 2, 1, 3 };

        var tangents = TangentGenerator.Generate(positions, normals, uvs, indices);

        Assert.All(tangents, t =>
        {
            var xyz = new Vector3(t.X, t.Y, t.Z);
            Assert.False(float.IsNaN(xyz.X) || float.IsNaN(xyz.Y) || float.IsNaN(xyz.Z));
            Assert.True(MathF.Abs(xyz.Length() - 1f) < Tolerance);
        });
    }

    [Fact]
    public void MismatchedStreamLengths_Throw()
    {
        var positions = new Vector3[4];
        var normals = new Vector3[3];
        var uvs = new Vector2[4];

        Assert.Throws<AssetException>(() =>
            TangentGenerator.Generate(positions, normals, uvs, new uint[] { 0, 1, 2 }));
    }

    [Fact]
    public void NonTriangleIndexCount_Throws()
    {
        var positions = new Vector3[3];
        var normals = new Vector3[3];
        var uvs = new Vector2[3];

        Assert.Throws<AssetException>(() =>
            TangentGenerator.Generate(positions, normals, uvs, new uint[] { 0, 1 }));
    }
}
