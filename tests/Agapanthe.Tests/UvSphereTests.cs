using System.Numerics;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

/// <summary>
/// <see cref="Primitives.UvSphere"/> (P3-M8): the planetary body geometry. These pin the invariants the renderer
/// relies on — every vertex on the unit sphere, normals analytic (= position), indices in range, and winding CCW
/// seen from outside (so backface culling keeps the lit hemisphere, same convention as <see cref="Primitives.Cube"/>).
/// </summary>
public sealed class UvSphereTests
{
    [Fact]
    public void EveryVertexIsOnTheUnitSphere_WithNormalEqualToPosition()
    {
        var (vertices, _) = Primitives.UvSphere(32, 16);

        Assert.NotEmpty(vertices);
        foreach (var v in vertices)
        {
            Assert.Equal(1f, v.Position.Length(), 4);         // radius 1
            Assert.Equal(1f, v.Normal.Length(), 4);           // unit normal
            Assert.True(Vector3.Distance(v.Position, v.Normal) < 1e-4f); // normal == position
            Assert.Equal(1f, v.Tangent.W);                    // handedness +1
            Assert.True(MathF.Abs(Vector3.Dot(new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z), v.Normal)) < 1e-3f); // tangent ⟂ normal
        }
    }

    [Fact]
    public void IndicesAreInRange_AndCountMatchesTessellation()
    {
        const int segments = 24;
        const int rings = 12;
        var (vertices, indices) = Primitives.UvSphere(segments, rings);

        Assert.Equal(rings * segments * 6, indices.Length);
        Assert.Equal((rings + 1) * (segments + 1), vertices.Length);
        foreach (var idx in indices)
        {
            Assert.True(idx < vertices.Length);
        }
    }

    [Fact]
    public void WindingIsCounterClockwiseSeenFromOutside()
    {
        // For every non-degenerate triangle the geometric normal (edge1 × edge2) must point the same way as the
        // outward vertex normals — otherwise backface culling would drop the visible surface.
        var (vertices, indices) = Primitives.UvSphere(24, 12);

        var tested = 0;
        for (var t = 0; t < indices.Length; t += 3)
        {
            var a = vertices[indices[t]].Position;
            var b = vertices[indices[t + 1]].Position;
            var c = vertices[indices[t + 2]].Position;

            var geo = Vector3.Cross(b - a, c - a);
            if (geo.Length() < 1e-6f)
            {
                continue; // degenerate pole triangle
            }

            var outward = (a + b + c) / 3f; // centroid ≈ outward direction on the sphere
            Assert.True(Vector3.Dot(geo, outward) > 0f, $"triangle {t / 3} winds inward");
            tested++;
        }

        Assert.True(tested > 0);
    }

    [Fact]
    public void RejectsDegenerateTessellation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Primitives.UvSphere(2, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => Primitives.UvSphere(32, 1));
    }

    [Fact]
    public void RejectsTessellationOverTheUshortIndexLimit()
    {
        // (256+1)·(256+1) = 66049 > 65536: the ushort index casts would wrap silently into a corrupt mesh, so the
        // generator must throw instead (audit P3-M8 🟡-1). The default 128×64 = 8385 stays well under the limit.
        Assert.Throws<ArgumentOutOfRangeException>(() => Primitives.UvSphere(256, 256));
        _ = Primitives.UvSphere(128, 64); // does not throw
    }
}
