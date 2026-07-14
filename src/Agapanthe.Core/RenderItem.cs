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
    /// Draw-order key (see <see cref="ComposeSortKey"/>): the material in the high bits so the sorted list groups
    /// draws by material (fewer set-1 rebinds), a stable tie-break in the low bits so the order is deterministic
    /// even where materials collide (spec §6 condition b).
    /// </summary>
    public readonly ulong SortKey;

    public RenderItem(in Matrix4x4 worldTransform, MeshHandle mesh, MaterialHandle material, ulong sortKey)
    {
        WorldTransform = worldTransform;
        Mesh = mesh;
        Material = material;
        SortKey = sortKey;
    }

    /// <summary>
    /// Builds the 64-bit draw-sort key: <paramref name="materialIndex"/> in bits 48-63,
    /// <paramref name="meshIndex"/> in bits 32-47, <paramref name="tieBreak"/> in the low 32 bits. Sorting on it
    /// groups draws first by material (fewer set-1 rebinds) then by mesh, so a run of consecutive items shares one
    /// (material, mesh) pair — exactly one instanced draw per run (P3-M1 batching).
    /// <para>
    /// The tie-break is <b>load-bearing, not decorative</b> (both M4 audits): entities sharing a (material, mesh)
    /// pair have equal high 32 bits, and an equal-key run would otherwise be ordered by Arch's archetype/chunk
    /// iteration — which is not deterministic. Baking a stable per-entity value (its <c>GlobalId</c>/<c>RenderOrder</c>)
    /// into the low bits makes the total order deterministic; a "stable sort" alone would not, because the pre-sort
    /// input order is itself non-deterministic. The tie-break keeps its <b>full 32 bits</b> — never narrow it.
    /// </para>
    /// <para>Material and mesh indices must fit in 16 bits (65 535 max), asserted in Debug builds.</para>
    /// </summary>
    public static ulong ComposeSortKey(int materialIndex, int meshIndex, uint tieBreak)
    {
        System.Diagnostics.Debug.Assert(
            materialIndex is >= 0 and <= 0xFFFF, "materialIndex must fit in 16 bits for the sort key.");
        System.Diagnostics.Debug.Assert(
            meshIndex is >= 0 and <= 0xFFFF, "meshIndex must fit in 16 bits for the sort key.");
        return ((ulong)(uint)materialIndex << 48) | ((ulong)(uint)meshIndex << 32) | tieBreak;
    }
}
