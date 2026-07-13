using System.Numerics;

namespace Agapanthe.Core;

/// <summary>
/// Math helpers targeting Vulkan clip space: Y points down, depth range [0, 1].
/// Convention: right-handed view space, System.Numerics row-vector matrices.
/// Uploaded as-is to GLSL (std140 column-major) they read as the column-vector
/// transpose, so shaders use the usual `proj * view * model * position` order.
/// </summary>
public static class MathHelpers
{
    /// <summary>
    /// Right-handed perspective projection for Vulkan: depth [0, 1], Y flipped
    /// so world +Y maps to up on screen despite Vulkan's Y-down clip space.
    /// </summary>
    public static Matrix4x4 PerspectiveVulkan(float fovYRadians, float aspect, float near, float far)
    {
        var m = Matrix4x4.CreatePerspectiveFieldOfView(fovYRadians, aspect, near, far);
        m.M22 = -m.M22;
        return m;
    }

    /// <summary>
    /// Right-handed, centred orthographic projection for Vulkan: depth [0, 1], Y flipped so world +Y
    /// maps to up on screen despite Vulkan's Y-down clip space. Same convention as
    /// <see cref="PerspectiveVulkan"/>.
    /// <para>
    /// The base <see cref="Matrix4x4.CreateOrthographic"/> already maps view-space depth to [0, 1]
    /// (view z = -near → NDC z = 0, view z = -far → NDC z = 1; NOT the OpenGL [-1, 1] range), and leaves
    /// Y unflipped (Y-up NDC). Negating M22 — exactly as <see cref="PerspectiveVulkan"/> does — turns it
    /// into Vulkan's Y-down clip space, so a view-space +Y point projects to a negative NDC Y.
    /// </para>
    /// </summary>
    public static Matrix4x4 OrthographicVulkan(float width, float height, float near, float far)
    {
        var m = Matrix4x4.CreateOrthographic(width, height, near, far);
        m.M22 = -m.M22;
        return m;
    }

    /// <summary>Right-handed look-at view matrix (System.Numerics convention).</summary>
    public static Matrix4x4 LookAt(Vector3 eye, Vector3 target, Vector3 up)
        => Matrix4x4.CreateLookAt(eye, target, up);

    /// <summary>
    /// The largest factor by which the transform's linear part can stretch a vector — its largest singular value
    /// σ_max. Used to grow a local bounding sphere's radius into world space: a sphere of radius <c>r</c> under
    /// <c>m</c> is contained in a sphere of radius <c>r · σ_max</c>, and never a smaller one. Getting this wrong
    /// UNDER-covers and drops a visible object at the frustum edge (a false-negative cull, audit P2-M4 M1).
    /// <para>
    /// It must be σ_max, not the longest basis row: the row length equals σ_max only when the rows stay
    /// orthogonal and equal (pure rotation, or uniform scale, or scale·rotation). Under shear — rotation with a
    /// non-uniform scale, which a hierarchical glTF bakes — the longest row is strictly SHORTER than σ_max, so a
    /// row-length radius under-covers by an unbounded factor. σ_max is computed exactly (the largest eigenvalue of
    /// <c>mᵀm</c>), so it is tight: for rotation × uniform scale it returns the scale exactly, with no inflation
    /// of the common case (unlike a Frobenius bound, which would over-cover every rotation by up to √3).
    /// </para>
    /// </summary>
    public static float MaxStretch(in Matrix4x4 m)
    {
        // A = (linear part)ᵀ · (linear part): symmetric positive-semidefinite; σ_max(m) = sqrt(λ_max(A)).
        var a11 = (m.M11 * m.M11) + (m.M21 * m.M21) + (m.M31 * m.M31);
        var a22 = (m.M12 * m.M12) + (m.M22 * m.M22) + (m.M32 * m.M32);
        var a33 = (m.M13 * m.M13) + (m.M23 * m.M23) + (m.M33 * m.M33);
        var a12 = (m.M11 * m.M12) + (m.M21 * m.M22) + (m.M31 * m.M32);
        var a13 = (m.M11 * m.M13) + (m.M21 * m.M23) + (m.M31 * m.M33);
        var a23 = (m.M12 * m.M13) + (m.M22 * m.M23) + (m.M32 * m.M33);

        // Largest eigenvalue of a symmetric 3×3 (Smith's closed form). p1 is the off-diagonal energy: zero means
        // A is already diagonal, so the eigenvalues are the diagonal entries.
        var p1 = (a12 * a12) + (a13 * a13) + (a23 * a23);
        float lambdaMax;
        if (p1 <= 1e-20f)
        {
            lambdaMax = MathF.Max(a11, MathF.Max(a22, a33));
        }
        else
        {
            var q = (a11 + a22 + a33) / 3f;
            var d11 = a11 - q;
            var d22 = a22 - q;
            var d33 = a33 - q;
            var p2 = (d11 * d11) + (d22 * d22) + (d33 * d33) + (2f * p1);
            var p = MathF.Sqrt(p2 / 6f);

            // det((A - qI)/p) / 2, clamped so acos stays in domain despite rounding.
            var det = (d11 * ((d22 * d33) - (a23 * a23)))
                    - (a12 * ((a12 * d33) - (a23 * a13)))
                    + (a13 * ((a12 * a23) - (d22 * a13)));
            var r = Math.Clamp(det / (2f * p * p * p), -1f, 1f);
            var phi = MathF.Acos(r) / 3f;

            // The three eigenvalues are q + 2p·cos(phi + k·2π/3); the largest is at k = 0.
            lambdaMax = q + (2f * p * MathF.Cos(phi));
        }

        return MathF.Sqrt(MathF.Max(lambdaMax, 0f));
    }

    /// <summary>Projects a point through a row-vector matrix with perspective divide.</summary>
    public static Vector3 ProjectPoint(Vector3 point, in Matrix4x4 matrix)
    {
        var v = Vector4.Transform(new Vector4(point, 1f), matrix);
        return new Vector3(v.X, v.Y, v.Z) / v.W;
    }
}
