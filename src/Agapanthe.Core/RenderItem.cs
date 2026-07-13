using System.Numerics;
using System.Runtime.InteropServices;

namespace Agapanthe.Core;

/// <summary>
/// One draw in a <see cref="RenderList"/>: a baked world matrix plus the handles the render side resolves to GPU
/// resources, and a sort key. GPU-free by construction (spec §3.2), so the world can build it without
/// referencing the graphics layer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct RenderItem
{
    public readonly Matrix4x4 WorldTransform;
    public readonly MeshHandle Mesh;
    public readonly MaterialHandle Material;

    /// <summary>
    /// Draw-order key. In M2 it carries the source render order (the stable draw order, spec §6 condition b);
    /// the high bits are reserved for material / pipeline / depth batching once real culling arrives (M4).
    /// </summary>
    public readonly ulong SortKey;

    public RenderItem(in Matrix4x4 worldTransform, MeshHandle mesh, MaterialHandle material, ulong sortKey)
    {
        WorldTransform = worldTransform;
        Mesh = mesh;
        Material = material;
        SortKey = sortKey;
    }
}
