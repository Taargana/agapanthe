using System.Numerics;
using System.Runtime.InteropServices;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// Interleaved vertex. Normal and Uv are populated now but only consumed from M5 (lighting)
/// and M3 (texturing); keeping them in the layout avoids a vertex format change later.
/// Sequential layout so the managed struct maps 1:1 to the GPU vertex.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Vertex(Vector3 position, Vector3 color, Vector3 normal, Vector2 uv)
{
    public readonly Vector3 Position = position;
    public readonly Vector3 Color = color;
    public readonly Vector3 Normal = normal;
    public readonly Vector2 Uv = uv;

    /// <summary>Vertex input layout matching the field order above (stride 44 bytes).</summary>
    public static VertexLayout Layout { get; } = new(
        stride: 44,
        attributes:
        [
            new VertexAttribute(Location: 0, Offset: 0, PixelFormat.R32G32B32Sfloat),  // position
            new VertexAttribute(Location: 1, Offset: 12, PixelFormat.R32G32B32Sfloat), // color
            new VertexAttribute(Location: 2, Offset: 24, PixelFormat.R32G32B32Sfloat), // normal
            new VertexAttribute(Location: 3, Offset: 36, PixelFormat.R32G32Sfloat),    // uv
        ]);
}
