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

    /// <summary>Right-handed look-at view matrix (System.Numerics convention).</summary>
    public static Matrix4x4 LookAt(Vector3 eye, Vector3 target, Vector3 up)
        => Matrix4x4.CreateLookAt(eye, target, up);

    /// <summary>Projects a point through a row-vector matrix with perspective divide.</summary>
    public static Vector3 ProjectPoint(Vector3 point, in Matrix4x4 matrix)
    {
        var v = Vector4.Transform(new Vector4(point, 1f), matrix);
        return new Vector3(v.X, v.Y, v.Z) / v.W;
    }
}
