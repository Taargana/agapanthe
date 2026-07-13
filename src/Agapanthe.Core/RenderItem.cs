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
    /// Builds the 64-bit draw-sort key: <paramref name="materialIndex"/> in the high 32 bits (batches same-material
    /// draws together, cutting descriptor-set rebinds), <paramref name="tieBreak"/> in the low 32 bits.
    /// <para>
    /// The tie-break is <b>load-bearing, not decorative</b> (both M4 audits): once the key leads with the material,
    /// entities sharing a material have equal high bits, and an equal-key run would otherwise be ordered by Arch's
    /// archetype/chunk iteration — which is not deterministic. Baking a stable per-entity value (its
    /// <c>GlobalId</c>/<c>RenderOrder</c>) into the low bits makes the total order deterministic; a "stable sort"
    /// alone would not, because the pre-sort input order is itself non-deterministic.
    /// </para>
    /// </summary>
    public static ulong ComposeSortKey(int materialIndex, uint tieBreak)
        => ((ulong)(uint)materialIndex << 32) | tieBreak;
}
