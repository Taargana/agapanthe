using System.Numerics;

namespace Agapanthe.Assets.Model;

/// <summary>
/// The single implementation of a mesh's LOCAL bounding sphere (spec §3.4). Contenu-3b: the cook side
/// (<c>Agapanthe.Assets.Pipeline</c>) precomputes it into <see cref="MeshAsset.BoundsCenter"/>/<see
/// cref="MeshAsset.BoundsRadius"/>, and <c>Rendering.SceneBuilder</c> reads that — falling back to this
/// same helper for a mesh whose bounds were never cooked (an in-code procedural generator).
/// <para>
/// Centre = the local-space vertex AABB's centre; radius = the farthest vertex from that centre (tight —
/// it hugs the geometry, not the AABB corner). No positions → a zero sphere at the local origin.
/// </para>
/// </summary>
public static class MeshBounds
{
    /// <inheritdoc cref="Compute(System.ReadOnlySpan{System.Numerics.Vector3})"/>
    public static (Vector3 Center, float Radius) Compute(MeshAsset mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return Compute(mesh.Positions);
    }

    /// <summary>The local bounding sphere over <paramref name="positions"/> (local space, untransformed).</summary>
    public static (Vector3 Center, float Radius) Compute(ReadOnlySpan<Vector3> positions)
    {
        if (positions.Length == 0)
        {
            return (Vector3.Zero, 0f);
        }

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        var center = (min + max) * 0.5f;
        var radiusSquared = 0f;
        foreach (var p in positions)
        {
            radiusSquared = MathF.Max(radiusSquared, Vector3.DistanceSquared(center, p));
        }

        return (center, MathF.Sqrt(radiusSquared));
    }
}
