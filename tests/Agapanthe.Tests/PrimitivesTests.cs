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
    public void Vertex_LayoutHasFourAttributes()
    {
        Assert.Equal(4, Vertex.Layout.Attributes.Count);
    }
}
