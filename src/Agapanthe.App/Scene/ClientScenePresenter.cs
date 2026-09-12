using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Scene;
using Agapanthe.Core;

namespace Agapanthe.App;

/// <summary>
/// Contenu-3b — the presentation tail of loading a cooked scene on a client: upload each referenced model to the
/// GPU, resolve every drawable's <c>MeshRef</c> render cache from its <c>AssetRef</c> identity, then apply the
/// scene's lights, environment and camera. The world was already populated (GPU-free) by
/// <see cref="Agapanthe.Scene.SceneMaterializer"/>; this only adds what needs a device.
/// </summary>
public static class ClientScenePresenter
{
    public static void Apply(
        Agapanthe.Scene.MaterializeResult result, SimSceneContext sim, PresentationSceneContext p)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(p);

        // Upload once per distinct model; the returned specs are ignored — only the GPU upload + key registration
        // side effects matter (the world already holds the entities, keyed by AssetRef).
        foreach (var (key, model) in result.Models)
        {
            p.Registry.Load(p.Device, model, p.Renderer.MaterialSetLayout, key);
        }

        sim.World.ResolveMeshRefs(p.Registry.ResolveMeshRef);

        var def = result.Definition;
        SceneLightRig.Apply(def.Lights, def.Ambient, p.Renderer.Lights);
        ApplyEnvironment(def, p);
        SceneCameraApplier.Apply(def.Camera, p.Camera, p.Controller, p.Renderer, sim.World.AggregateBounds());

        if (def.Camera.FreeFly)
        {
            SceneInput.EnableFreeFly(p.Window, p.Camera, p.Controller);
        }
    }

    private static void ApplyEnvironment(SceneDefinition def, PresentationSceneContext p)
    {
        var environment = def.Environment;
        switch (environment.Mode)
        {
            case SceneEnvironmentMode.HdriPath:
                if (environment.HdriPath.Length == 0)
                {
                    return;
                }

                var path = Path.IsPathRooted(environment.HdriPath)
                    ? environment.HdriPath
                    : Path.Combine(AppContext.BaseDirectory, environment.HdriPath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(path))
                {
                    Log.Warn($"AppHost: [scene] environment HDR '{path}' not found — the scene renders without IBL.");
                    return;
                }

                p.Renderer.SetEnvironment(HdrImageLoader.Load(path));
                Log.Info($"AppHost: [scene] environment '{path}'.");
                break;

            case SceneEnvironmentMode.ProceduralSky:
                // Contenu-3c: derives the sun direction from the scene's own directional light — the same source
                // of truth the light rig just applied, so the sky's sun glow lines up with the actual shadow caster.
                var sunDir = def.Lights.FirstOrDefault(l => l.Kind == SceneLightKind.Directional)?.Direction ?? Vector3.UnitY;
                p.Renderer.SetEnvironment(ProceduralSky.Build(sunDir));
                Log.Info("AppHost: [scene] environment = procedural outdoor sky.");
                break;

            case SceneEnvironmentMode.Black:
                p.Renderer.SetEnvironment(BlackEnvironment.Build());
                Log.Info("AppHost: [scene] environment = black (no ambient/skybox).");
                break;

            case SceneEnvironmentMode.None:
                break;
        }
    }
}
