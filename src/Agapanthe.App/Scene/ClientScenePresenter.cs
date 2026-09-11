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
        ApplyEnvironment(def.Environment, p);
        SceneCameraApplier.Apply(def.Camera, p.Camera, p.Controller, p.Renderer, sim.World.AggregateBounds());

        if (def.Camera.FreeFly)
        {
            SceneInput.EnableFreeFly(p.Window, p.Camera, p.Controller);
        }
    }

    private static void ApplyEnvironment(SceneEnvironment environment, PresentationSceneContext p)
    {
        if (environment.Mode != SceneEnvironmentMode.HdriPath || environment.HdriPath.Length == 0)
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
    }
}
