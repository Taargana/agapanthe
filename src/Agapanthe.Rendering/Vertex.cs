using System.Numerics;
using System.Runtime.InteropServices;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// Interleaved vertex. Color/Normal/Uv are populated now but consumed progressively
/// (Uv from M3 texturing, Normal from M5 lighting); Tangent is written by M4 (glTF import /
/// analytic primitives) and consumed by M5 normal mapping. Keeping all fields in the layout
/// avoids a vertex format change later.
/// Sequential layout so the managed struct maps 1:1 to the GPU vertex.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Vertex(Vector3 position, Vector3 color, Vector3 normal, Vector2 uv, Vector4 tangent)
{
    public readonly Vector3 Position = position;
    public readonly Vector3 Color = color;
    public readonly Vector3 Normal = normal;
    public readonly Vector2 Uv = uv;

    /// <summary>
    /// xyz = tangent direction, w = handedness (±1). glTF convention:
    /// bitangent = w * cross(Normal, Tangent.xyz). Consumed by M5 normal mapping.
    /// </summary>
    public readonly Vector4 Tangent = tangent;

    /// <summary>
    /// Convenience overload without a tangent (defaults to +X, +1 handedness). Keeps call sites
    /// that predate the tangent field compiling; primitives that know their tangent use the full ctor.
    /// </summary>
    public Vertex(Vector3 position, Vector3 color, Vector3 normal, Vector2 uv)
        : this(position, color, normal, uv, new Vector4(1f, 0f, 0f, 1f))
    {
    }

    /// <summary>Vertex input layout matching the field order above (stride 60 bytes).</summary>
    public static VertexLayout Layout { get; } = new(
        stride: 60,
        attributes:
        [
            new VertexAttribute(Location: 0, Offset: 0, PixelFormat.R32G32B32Sfloat),      // position
            new VertexAttribute(Location: 1, Offset: 12, PixelFormat.R32G32B32Sfloat),     // color
            new VertexAttribute(Location: 2, Offset: 24, PixelFormat.R32G32B32Sfloat),     // normal
            new VertexAttribute(Location: 3, Offset: 36, PixelFormat.R32G32Sfloat),        // uv
            new VertexAttribute(Location: 4, Offset: 44, PixelFormat.R32G32B32A32Sfloat),  // tangent (xyz=T, w=handedness)
        ]);
}
