using System.Numerics;
using System.Runtime.InteropServices;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

public class PrimitivesTests
{
    [Fact]
    public void Cube_Has24VerticesAnd36Indices()
    {
        var (vertices, indices) = Primitives.Cube();
        Assert.Equal(24, vertices.Length);
        Assert.Equal(36, indices.Length);
    }

    [Fact]
    public void Cube_AllIndicesInRange()
    {
        var (vertices, indices) = Primitives.Cube();
        Assert.All(indices, i => Assert.InRange(i, 0, vertices.Length - 1));
    }

    [Fact]
    public void Vertex_LayoutStride_MatchesStructSize()
    {
        // The declared vertex layout stride must equal the actual struct size or the GPU
        // reads garbage between attributes.
        Assert.Equal((uint)Marshal.SizeOf<Vertex>(), Vertex.Layout.Stride);
    }

    [Fact]
    public void Vertex_LayoutHasFiveAttributes()
    {
        Assert.Equal(5, Vertex.Layout.Attributes.Count);
    }

    [Fact]
    public void Cube_Tangents_AreUnitLengthWithValidHandedness()
    {
        var (vertices, _) = Primitives.Cube();
        Assert.All(vertices, v =>
        {
            var t = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
            Assert.Equal(1f, t.Length(), 5);
            Assert.True(MathF.Abs(v.Tangent.W) == 1f, $"handedness must be ±1, got {v.Tangent.W}");
        });
    }

    [Fact]
    public void Cube_Tangents_ArePerpendicularToNormals()
    {
        var (vertices, _) = Primitives.Cube();
        Assert.All(vertices, v =>
        {
            var t = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
            Assert.Equal(0f, Vector3.Dot(t, v.Normal), 5);
        });
    }

    [Fact]
    public void Cube_Tangents_FollowTheUDirectionOfEachFace()
    {
        // Per face the corners are laid out A(0,0) B(1,0) C(1,1) D(0,1): the tangent must be
        // the normalized A→B edge and bitangent = w*cross(N,T) must point along A→D (+V).
        var (vertices, _) = Primitives.Cube();
        for (var f = 0; f < 6; f++)
        {
            var a = vertices[f * 4 + 0];
            var b = vertices[f * 4 + 1];
            var d = vertices[f * 4 + 3];

            var t = new Vector3(a.Tangent.X, a.Tangent.Y, a.Tangent.Z);
            var expectedT = Vector3.Normalize(b.Position - a.Position);
            Assert.Equal(0f, (t - expectedT).Length(), 5);

            var bitangent = a.Tangent.W * Vector3.Cross(a.Normal, t);
            var expectedB = Vector3.Normalize(d.Position - a.Position);
            Assert.True(Vector3.Dot(bitangent, expectedB) > 0.999f,
                $"face {f}: bitangent {bitangent} does not follow +V {expectedB}");
        }
    }
}
