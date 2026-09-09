using System.Numerics;

namespace Agapanthe.Assets;

/// <summary>
/// CPU tangent generation for glTF meshes that ship without a TANGENT stream (common in the
/// Khronos sample models). Per-triangle tangents/bitangents are derived from the UV gradients
/// (Lengyel's method), accumulated per vertex, then Gram-Schmidt-orthogonalized against the
/// vertex normal. Handedness follows the glTF convention: <c>bitangent = w · cross(N, T)</c>.
/// <para>
/// This per-triangle accumulated method is sufficient for phase 1 (spec §3.6); switch to
/// MikkTSpace if visible normal-mapping artifacts appear on production assets. Runs at asset
/// load time — not a per-frame path.
/// </para>
/// </summary>
public static class TangentGenerator
{
    /// <summary>UV-determinant threshold under which a triangle is considered degenerate and skipped.</summary>
    private const float DegenerateUvEpsilon = 1e-8f;

    /// <summary>Squared-length threshold under which an accumulated tangent falls back to an arbitrary frame.</summary>
    private const float DegenerateTangentEpsilon = 1e-12f;

    /// <summary>
    /// Generates one tangent per vertex. <paramref name="positions"/>, <paramref name="normals"/>
    /// and <paramref name="uvs"/> must have equal lengths and <paramref name="indices"/> must
    /// describe a triangle list. Vertices whose triangles all have degenerate UVs receive a
    /// deterministic fallback tangent perpendicular to their normal (w = +1) — never NaN.
    /// </summary>
    /// <exception cref="AssetException">Mismatched stream lengths or a non-triangle index count.</exception>
    public static Vector4[] Generate(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector3> normals,
        ReadOnlySpan<Vector2> uvs,
        ReadOnlySpan<uint> indices)
    {
        if (normals.Length != positions.Length || uvs.Length != positions.Length)
        {
            throw new AssetException(
                "<tangent generation>",
                $"stream lengths differ (positions {positions.Length}, normals {normals.Length}, uvs {uvs.Length}).");
        }

        if (indices.Length % 3 != 0)
        {
            throw new AssetException(
                "<tangent generation>",
                $"index count {indices.Length} is not a triangle list (not a multiple of 3).");
        }

        var tanAccum = new Vector3[positions.Length];
        var bitanAccum = new Vector3[positions.Length];

        for (var t = 0; t < indices.Length; t += 3)
        {
            var i0 = (int)indices[t + 0];
            var i1 = (int)indices[t + 1];
            var i2 = (int)indices[t + 2];

            var e1 = positions[i1] - positions[i0];
            var e2 = positions[i2] - positions[i0];
            var duv1 = uvs[i1] - uvs[i0];
            var duv2 = uvs[i2] - uvs[i0];

            var det = (duv1.X * duv2.Y) - (duv2.X * duv1.Y);
            if (MathF.Abs(det) < DegenerateUvEpsilon)
            {
                continue; // Degenerate UV mapping: this triangle contributes nothing.
            }

            var r = 1f / det;
            var tangent = r * ((duv2.Y * e1) - (duv1.Y * e2));
            var bitangent = r * ((duv1.X * e2) - (duv2.X * e1));

            tanAccum[i0] += tangent;
            tanAccum[i1] += tangent;
            tanAccum[i2] += tangent;
            bitanAccum[i0] += bitangent;
            bitanAccum[i1] += bitangent;
            bitanAccum[i2] += bitangent;
        }

        var result = new Vector4[positions.Length];
        for (var v = 0; v < result.Length; v++)
        {
            var n = normals[v];
            var t = tanAccum[v];

            // Gram-Schmidt: project the accumulated tangent onto the plane perpendicular to N.
            var orthogonal = t - (n * Vector3.Dot(n, t));
            if (orthogonal.LengthSquared() < DegenerateTangentEpsilon)
            {
                result[v] = new Vector4(FallbackTangent(n), 1f);
                continue;
            }

            var tangent = Vector3.Normalize(orthogonal);
            var w = Vector3.Dot(Vector3.Cross(n, tangent), bitanAccum[v]) < 0f ? -1f : 1f;
            result[v] = new Vector4(tangent, w);
        }

        return result;
    }

    /// <summary>
    /// Deterministic tangent perpendicular to <paramref name="normal"/> for vertices with no
    /// usable UV gradient: cross the normal with the world axis it is least aligned with.
    /// </summary>
    private static Vector3 FallbackTangent(Vector3 normal)
    {
        var axis = MathF.Abs(normal.X) <= MathF.Abs(normal.Y) && MathF.Abs(normal.X) <= MathF.Abs(normal.Z)
            ? Vector3.UnitX
            : (MathF.Abs(normal.Y) <= MathF.Abs(normal.Z) ? Vector3.UnitY : Vector3.UnitZ);

        var tangent = Vector3.Cross(normal, axis);
        return tangent.LengthSquared() < DegenerateTangentEpsilon
            ? Vector3.UnitX // Normal itself is degenerate (zero); nothing meaningful to derive.
            : Vector3.Normalize(tangent);
    }
}
