using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.World;

namespace Agapanthe.App;

/// <summary>
/// Contenu-3b — a scene loaded from cooked data: <c>scenes/&lt;name&gt;.agscene</c> in the content manifest. One
/// instance per scene (<c>new SceneRecipe("model")</c>, <c>new SceneRecipe("grid")</c>, …); the name is both the
/// <c>AGAPANTHE_SCENE</c> token and the manifest key stem. Replaces the hand-coded <c>ModelSceneRecipe</c> — the
/// <c>model</c> family (single / grid / cluster) is now authored TOML, expanded at cook time.
/// </summary>
public sealed class SceneRecipe : ISceneRecipe
{
    public SceneRecipe(string sceneName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneName);
        Name = sceneName;
    }

    public string Name { get; }

    public void Build(SimSceneContext sim, PresentationSceneContext? presentation)
    {
        var def = sim.Catalog.LoadScene(new AssetKey($"scenes/{Name}"));
        var result = Agapanthe.Scene.SceneLoader.LoadHeadless(
            def, sim.Catalog, sim.World, sim.Simulation.Settings.FixedDeltaSeconds);

        if (result.Physics is { } ps)
        {
            sim.AddSystem(Stage.Simulation, new PhysicsSystem(sim.World, in ps));
            Log.Info($"Sandbox: [scene '{Name}'] physics — gravity {ps.Gravity}, ground y={ps.GroundY:F2}.");
        }

        if (result.RestorePath is { } rp)
        {
            sim.RequestRestore(rp, SnapshotAllocatorPolicy.AdoptFromHeader);
        }

        Log.Info($"Sandbox: [scene '{Name}'] {sim.World.LiveEntityCount} entities from cooked data.");

        if (presentation is { } p)
        {
            ClientScenePresenter.Apply(result, sim, p);
        }
    }
}
