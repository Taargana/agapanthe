using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Rendering;

/// <summary>
/// Free 3D camera. Orientation is stored as yaw/pitch (radians) and turned into a
/// view matrix on demand.
/// </summary>
/// <remarks>
/// Conventions (matching <see cref="MathHelpers"/> / session 1 tests):
/// <list type="bullet">
/// <item>World space is <b>right-handed</b>, world up is <c>+Y</c>.</item>
/// <item>At <c>Yaw == Pitch == 0</c> the camera looks down <b>-Z</b>
///   (forward = <c>(0,0,-1)</c>), matching <see cref="MathHelpers.LookAt"/> where
///   view space looks down its own -Z.</item>
/// <item><b>Yaw</b> rotates around world up. It <i>increases</i> as the view turns
///   toward <c>+X</c>: <c>Yaw = +90°</c> gives forward = <c>(1,0,0)</c> (turning right
///   when up is +Y). This is a compass/heading style yaw, not the right-hand rotation
///   about +Y (which would send -Z toward -X); the sign is chosen so mouse-right = turn-right.</item>
/// <item><b>Pitch</b> tilts up/down: <c>Pitch = +90°</c> looks straight up (<c>+Y</c>).
///   Callers must keep pitch away from ±90° to avoid gimbal degeneracy at the poles
///   (<see cref="FreeCameraController"/> clamps to ±89°).</item>
/// </list>
/// The Vulkan clip-space quirks (Y flip, depth [0,1]) live in
/// <see cref="MathHelpers.PerspectiveVulkan"/>, not here.
/// </remarks>
public sealed class Camera
{
    /// <summary>Eye position in world space.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Heading around world up, in radians. See remarks for sign convention.</summary>
    public float Yaw { get; set; }

    /// <summary>Elevation, in radians. Positive looks up. Keep within (-90°, +90°).</summary>
    public float Pitch { get; set; }

    /// <summary>Vertical field of view, in radians.</summary>
    public float FovY { get; set; } = MathF.PI / 3f; // 60°

    /// <summary>Near plane distance (positive, in front of the camera).</summary>
    public float Near { get; set; } = 0.1f;

    /// <summary>Far plane distance.</summary>
    public float Far { get; set; } = 1000f;

    /// <summary>Viewport aspect ratio (width / height).</summary>
    public float AspectRatio { get; set; } = 16f / 9f;

    /// <summary>Unit forward vector derived from yaw/pitch. <c>(0,0,-1)</c> when both are 0.</summary>
    public Vector3 Forward
    {
        get
        {
            float cp = MathF.Cos(Pitch);
            float sp = MathF.Sin(Pitch);
            float cy = MathF.Cos(Yaw);
            float sy = MathF.Sin(Yaw);
            return new Vector3(sy * cp, sp, -cy * cp);
        }
    }

    /// <summary>Unit right vector (in the horizontal plane), <c>cross(forward, worldUp)</c>.</summary>
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    /// <summary>Unit up vector of the camera basis, <c>cross(right, forward)</c>.</summary>
    public Vector3 Up => Vector3.Cross(Right, Forward);

    /// <summary>Right-handed view matrix looking along <see cref="Forward"/>.</summary>
    public Matrix4x4 ViewMatrix => MathHelpers.LookAt(Position, Position + Forward, Vector3.UnitY);

    /// <summary>Vulkan perspective projection (Y flipped, depth [0,1]).</summary>
    public Matrix4x4 ProjectionMatrix => MathHelpers.PerspectiveVulkan(FovY, AspectRatio, Near, Far);
}
