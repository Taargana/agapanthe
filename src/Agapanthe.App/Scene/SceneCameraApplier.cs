using System.Globalization;
using System.Numerics;
using Agapanthe.Assets.Scene;
using Agapanthe.Core;
using Agapanthe.Rendering;

namespace Agapanthe.App;

/// <summary>
/// Contenu-3b — applies a cooked <see cref="SceneCamera"/> to the live <see cref="Camera"/>. The
/// <see cref="SceneCameraMode.FrameBounds"/> path is the framing math lifted verbatim from the Sandbox's
/// <c>SandboxCameras.FrameCamera</c>/<c>NarrowBounds</c> (S30 debt, scoped): it reads the just-built world's
/// <see cref="GameWorld.AggregateBounds"/> and places the eye along the scene's <c>view_dir</c> at
/// <c>distance_mul × diagonal</c>. <see cref="SceneCameraMode.Fixed"/> writes the pose fields directly (no 3b
/// scene uses it — it is <c>drive</c>'s camera, migrated in 3c).
/// </summary>
public static class SceneCameraApplier
{
    public static void Apply(
        SceneCamera scene, Camera camera, FreeCameraController controller, Renderer renderer, Double3Bounds bounds)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(renderer);

        if (scene.FovY > 0f)
        {
            camera.FovY = scene.FovY * MathF.PI / 180f;
        }

        if (scene.Mode == SceneCameraMode.Fixed)
        {
            camera.Position = scene.Position;
            camera.Yaw = scene.Yaw;
            camera.Pitch = scene.Pitch;
            if (scene.Near > 0f)
            {
                camera.Near = scene.Near;
            }

            if (scene.Far > 0f)
            {
                camera.Far = scene.Far;
            }

            // Contenu-3c: baked planet/drive framing has no scene bounds to derive these from dynamically (unlike
            // FrameBounds below) — the cook-time trig that computed the fixed pose also computes these two.
            if (scene.MoveSpeed > 0f)
            {
                controller.MoveSpeed = scene.MoveSpeed;
            }

            if (scene.ShadowDistance > 0f)
            {
                renderer.ShadowDistance = scene.ShadowDistance;
            }

            // Slice-2: Agapanthe.Assets.Scene.CameraProjection (this cooked DTO, GPU-free) and
            // Agapanthe.Rendering.CameraProjection (the live Camera, Rendering-side) are deliberately two
            // distinct enums — Assets cannot reference Rendering — so the mapping is explicit, not a cast.
            // Always applied (not sentinel-gated like MoveSpeed/ShadowDistance above): SceneCamera.Projection
            // defaults to Perspective when a scene doesn't author one, which re-asserts Camera's own default —
            // harmless, and OrthoWidth/OrthoHeight are simply unused whenever Projection stays Perspective.
            camera.Projection = scene.Projection switch
            {
                Agapanthe.Assets.Scene.CameraProjection.Perspective => Agapanthe.Rendering.CameraProjection.Perspective,
                Agapanthe.Assets.Scene.CameraProjection.Orthographic => Agapanthe.Rendering.CameraProjection.Orthographic,
                _ => throw new InvalidOperationException($"unknown scene camera projection {scene.Projection}"),
            };
            camera.OrthoWidth = scene.OrthoWidth;
            camera.OrthoHeight = scene.OrthoHeight;

            return;
        }

        var (center, diagonal) = NarrowBounds(in bounds);
        if (diagonal <= 0f)
        {
            camera.Position = new Double3(0, 0, 3);
            camera.Yaw = 0f;
            camera.Pitch = 0f;
            return;
        }

        var viewDir = scene.ViewDir.LengthSquared() > 1e-6f ? scene.ViewDir : new Vector3(0f, 0.35f, 1f);
        if (Environment.GetEnvironmentVariable("AGAPANTHE_VIEW") is { } viewSpec)
        {
            var parts = viewSpec.Split(',');
            if (parts.Length == 3
                && float.TryParse(parts[0], CultureInfo.InvariantCulture, out var vx)
                && float.TryParse(parts[1], CultureInfo.InvariantCulture, out var vy)
                && float.TryParse(parts[2], CultureInfo.InvariantCulture, out var vz))
            {
                viewDir = new Vector3(vx, vy, vz);
            }
        }

        var dir = Vector3.Normalize(viewDir);
        var distanceMul = scene.DistanceMul > 0f ? scene.DistanceMul : 1.5f;
        var distance = MathF.Max(diagonal * distanceMul, 0.001f);

        camera.Position = center + new Double3(dir * distance);

        var forward = -dir;
        camera.Pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
        camera.Yaw = MathF.Atan2(forward.X, -forward.Z);

        camera.Near = MathF.Max(diagonal * 0.01f, 0.01f);
        camera.Far = (distance + diagonal) * 4f;

        controller.MoveSpeed = MathF.Max(diagonal * 0.5f, 0.01f);
        renderer.ShadowDistance = MathF.Max(MathF.Max(diagonal * 4f, 1f), renderer.Cascades.MaxDistance);
    }

    /// <summary>The scene's centre (double — may be far out) and the float diagonal the framing scales by. Lifted
    /// from <c>SandboxCameras.NarrowBounds</c>.</summary>
    public static (Double3 Center, float Diagonal) NarrowBounds(in Double3Bounds bounds)
    {
        if (bounds.IsEmpty)
        {
            return (Double3.Zero, 0f);
        }

        var extent = bounds.Max.ToVector3(bounds.Min);
        return (bounds.Min + new Double3(extent * 0.5f), extent.Length());
    }
}
