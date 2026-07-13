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
    /// The largest per-axis scale factor of a row-vector transform: the longest of its three basis rows. Used to
    /// grow a local bounding sphere's radius into world space (a sphere scaled non-uniformly is bounded by the
    /// largest axis scale), so the world sphere stays conservative — it may over-cover, never under-cover.
    /// </summary>
    public static float MaxAxisScale(in Matrix4x4 m)
    {
        var x = new Vector3(m.M11, m.M12, m.M13).LengthSquared();
        var y = new Vector3(m.M21, m.M22, m.M23).LengthSquared();
        var z = new Vector3(m.M31, m.M32, m.M33).LengthSquared();
        return MathF.Sqrt(MathF.Max(x, MathF.Max(y, z)));
    }

    /// <summary>Projects a point through a row-vector matrix with perspective divide.</summary>
    public static Vector3 ProjectPoint(Vector3 point, in Matrix4x4 matrix)
    {
        var v = Vector4.Transform(new Vector4(point, 1f), matrix);
        return new Vector3(v.X, v.Y, v.Z) / v.W;
    }
}
