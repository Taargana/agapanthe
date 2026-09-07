using System.Globalization;
using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Rendering;

namespace Sandbox;

/// <summary>Camera framing + light-rig helpers, lifted verbatim from the pre-extraction <c>Program.cs</c>. Each
/// scene recipe calls the framer it needs.</summary>
internal static class SandboxCameras
{
    /// <summary>The scene's centre (double — may be 10 000 km out) and the float diagonal the framing and light
    /// setup scale by. The extent is measured relative to the bounds' own corner so a far model's diagonal stays
    /// exact; only then is the half-extent added back onto the double centre. Empty world → degenerate zero box.</summary>
    public static (Double3 Center, float Diagonal) NarrowBounds(in Double3Bounds bounds)
    {
        if (bounds.IsEmpty)
        {
            return (Double3.Zero, 0f);
        }

        var extent = bounds.Max.ToVector3(bounds.Min);
        return (bounds.Min + new Double3(extent * 0.5f), extent.Length());
    }

    public static void SetupLights(SceneLights lights, in Double3Bounds bounds, bool multiInstance)
    {
        var (center, diagonal) = NarrowBounds(in bounds);
        var reach = MathF.Max(diagonal, 0.001f);

        var sunDir = new Vector3(0.4f, -0.7f, -0.6f);
        if (Environment.GetEnvironmentVariable("AGAPANTHE_SUN") is { } sunSpec)
        {
            var parts = sunSpec.Split(',');
            if (parts.Length == 3
                && float.TryParse(parts[0], CultureInfo.InvariantCulture, out var sx)
                && float.TryParse(parts[1], CultureInfo.InvariantCulture, out var sy)
                && float.TryParse(parts[2], CultureInfo.InvariantCulture, out var sz))
            {
                sunDir = Vector3.Normalize(new Vector3(sx, sy, sz));
            }
        }

        lights.Directional = new DirectionalLight
        {
            Direction = sunDir,
            Color = new Vector3(1f, 0.96f, 0.9f),
            Intensity = 12f,
        };

        if (multiInstance)
        {
            lights.PointCount = 0;
            lights.Ambient = new Vector3(0.08f, 0.08f, 0.09f);
            return;
        }

        lights.Points[0] = new PointLight
        {
            Position = center + new Double3(new Vector3(-0.8f, 0.9f, -1.1f) * reach),
            Color = new Vector3(0.55f, 0.65f, 1f),
            Intensity = 4f * reach * reach,
            Range = 6f * reach,
        };
        lights.Points[1] = new PointLight
        {
            Position = center + new Double3(new Vector3(0.9f, -0.2f, 1.2f) * reach),
            Color = new Vector3(1f, 0.85f, 0.7f),
            Intensity = 1.5f * reach * reach,
            Range = 6f * reach,
        };
        lights.PointCount = 2;
        lights.Ambient = new Vector3(0.08f, 0.08f, 0.09f);
    }

    public static void FrameCamera(Camera camera, FreeCameraController controller, Renderer renderer, in Double3Bounds bounds)
    {
        var (center, diagonal) = NarrowBounds(in bounds);

        if (diagonal <= 0f)
        {
            camera.Position = new Double3(0, 0, 3);
            camera.Yaw = 0f;
            camera.Pitch = 0f;
            return;
        }

        var distance = MathF.Max(diagonal * 1.5f, 0.001f);

        var dir = Vector3.Normalize(new Vector3(0f, 0.35f, 1f));
        if (Environment.GetEnvironmentVariable("AGAPANTHE_VIEW") is { } viewSpec)
        {
            var parts = viewSpec.Split(',');
            if (parts.Length == 3
                && float.TryParse(parts[0], CultureInfo.InvariantCulture, out var vx)
                && float.TryParse(parts[1], CultureInfo.InvariantCulture, out var vy)
                && float.TryParse(parts[2], CultureInfo.InvariantCulture, out var vz))
            {
                dir = Vector3.Normalize(new Vector3(vx, vy, vz));
            }
        }

        camera.Position = center + new Double3(dir * distance);

        var forward = -dir;
        camera.Pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
        camera.Yaw = MathF.Atan2(forward.X, -forward.Z);

        camera.Near = MathF.Max(diagonal * 0.01f, 0.01f);
        camera.Far = (distance + diagonal) * 4f;

        controller.MoveSpeed = MathF.Max(diagonal * 0.5f, 0.01f);

        renderer.ShadowDistance = MathF.Max(MathF.Max(diagonal * 4f, 1f), renderer.Cascades.MaxDistance);
    }

    public static void FramePlanetCamera(
        Camera camera, FreeCameraController controller, Renderer renderer,
        Double3 planetOrigin, Double3 sunOrigin, double planetRadius, Vector3 sunTravelDir)
    {
        var backDir = Vector3.Normalize(sunTravelDir);
        var worldUp = MathF.Abs(backDir.Y) > 0.95f ? Vector3.UnitX : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(backDir, worldUp));
        var up = Vector3.Cross(right, backDir);
        var beta = (float)(SandboxEnv.EnvDouble("AGAPANTHE_PLANET_PHASE", 40.0) * MathF.PI / 180.0);
        var side = Vector3.Normalize((right * 0.9f) + (up * 0.35f));
        var camDir = Vector3.Normalize((backDir * MathF.Cos(beta)) + (side * MathF.Sin(beta)));

        var altMul = (float)SandboxEnv.EnvDouble("AGAPANTHE_PLANET_ALT", 3.4);
        var distance = planetRadius * altMul;
        camera.Position = planetOrigin + new Double3(new Vector3(
            (float)(camDir.X * distance), (float)(camDir.Y * distance), (float)(camDir.Z * distance)));

        var forward = -camDir;
        camera.Pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
        camera.Yaw = MathF.Atan2(forward.X, -forward.Z);
        camera.FovY = (float)(SandboxEnv.EnvDouble("AGAPANTHE_PLANET_FOV", 70.0) * MathF.PI / 180.0);

        camera.Near = (float)MathF.Max((float)(planetRadius * 0.01), 100f);
        camera.Far = (float)(Double3.Distance(planetOrigin, sunOrigin) * 1.4);

        renderer.ShadowDistance = 1f;
        controller.MoveSpeed = (float)(planetRadius * 0.5);
    }

    public static void FramePlanetDropCamera(
        Camera camera, FreeCameraController controller, Renderer renderer,
        Double3 planetCentre, Double3 sunOrigin, double planetRadius, Vector3 sunTravelDir)
    {
        var surface = planetCentre + new Double3(0.0, planetRadius, 0.0);
        var sunDir = -Vector3.Normalize(sunTravelDir);
        var bearing = new Vector3(sunDir.X, 0f, sunDir.Z);
        bearing = bearing.LengthSquared() > 1e-6f ? Vector3.Normalize(bearing) : -Vector3.UnitZ;

        var sunOff = (float)(SandboxEnv.EnvDouble("AGAPANTHE_DROP_SUN_OFF", 32.0) * Math.PI / 180.0);
        var (so, co) = MathF.SinCos(sunOff);
        var lookAz = new Vector3((bearing.X * co) + (bearing.Z * so), 0f, (-bearing.X * so) + (bearing.Z * co));

        var camBack = (float)SandboxEnv.EnvDouble("AGAPANTHE_DROP_CAM_BACK", 60.0);
        var camHeight = (float)SandboxEnv.EnvDouble("AGAPANTHE_DROP_CAM_HEIGHT", 8.0);
        camera.Position = surface + new Double3(-lookAz.X * camBack, camHeight, -lookAz.Z * camBack);

        var target = surface + new Double3(0.0, 30.0, 0.0);
        var forward = Vector3.Normalize((target - camera.Position).ToVector3(Double3.Zero));
        camera.Pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
        camera.Yaw = MathF.Atan2(forward.X, -forward.Z);
        camera.FovY = (float)(SandboxEnv.EnvDouble("AGAPANTHE_PLANET_FOV", 70.0) * MathF.PI / 180.0);

        camera.Near = 1f;
        camera.Far = (float)(Double3.Distance(planetCentre, sunOrigin) * 1.4);
        renderer.ShadowDistance = 1f;
        controller.MoveSpeed = 20f;
    }

    public static void FramePlanetChallengeCamera(
        Camera camera, FreeCameraController controller, Renderer renderer,
        Double3 planetCentre, Double3 sunOrigin, double planetRadius, Double3 beacon)
    {
        var startAlt = SandboxEnv.EnvDouble("AGAPANTHE_CHALLENGE_CAM_ALT", 120.0);
        var eye = planetCentre + new Double3(0.0, planetRadius + startAlt, 0.0);
        camera.Position = eye;

        var forward = Vector3.Normalize((beacon - eye).ToVector3(Double3.Zero));
        camera.Pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
        camera.Yaw = MathF.Atan2(forward.X, -forward.Z);
        camera.FovY = (float)(SandboxEnv.EnvDouble("AGAPANTHE_PLANET_FOV", 70.0) * MathF.PI / 180.0);

        camera.Near = 1f;
        camera.Far = (float)(Double3.Distance(planetCentre, sunOrigin) * 1.4);
        renderer.ShadowDistance = 1f;
        controller.MoveSpeed = (float)SandboxEnv.EnvDouble("AGAPANTHE_CHALLENGE_MOVE_SPEED", 60.0);
    }
}
