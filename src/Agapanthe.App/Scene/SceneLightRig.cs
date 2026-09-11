using System.Numerics;
using Agapanthe.Assets.Scene;
using Agapanthe.Core;
using RenderLights = Agapanthe.Rendering.SceneLights;

namespace Agapanthe.App;

/// <summary>
/// Contenu-3b — applies a cooked scene's <see cref="SceneLight"/> list + ambient to the renderer's live light rig.
/// The hardcoded 1-directional / 2-point rig that used to live in <c>SandboxCameras.SetupLights</c> is now authored
/// data: this is the pure translation from <see cref="SceneLight"/> records to <see cref="RenderLights"/> fields.
/// </summary>
public static class SceneLightRig
{
    public static void Apply(IReadOnlyList<SceneLight> lights, Vector3 ambient, RenderLights target)
    {
        ArgumentNullException.ThrowIfNull(lights);
        ArgumentNullException.ThrowIfNull(target);

        target.Ambient = ambient;

        var pointCount = 0;
        foreach (var light in lights)
        {
            switch (light.Kind)
            {
                case SceneLightKind.Directional:
                    target.Directional = new Agapanthe.Rendering.DirectionalLight
                    {
                        Direction = Vector3.Normalize(light.Direction),
                        Color = light.Color,
                        Intensity = light.Intensity,
                    };
                    break;

                case SceneLightKind.Point:
                    if (pointCount < target.Points.Length)
                    {
                        target.Points[pointCount++] = new Agapanthe.Rendering.PointLight
                        {
                            Position = light.Position,
                            Color = light.Color,
                            Intensity = light.Intensity,
                            Range = light.Range,
                        };
                    }

                    break;
            }
        }

        target.PointCount = pointCount;
    }
}
