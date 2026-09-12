using System.Linq;
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
        else if (sim.Options.LoadPath is { Length: > 0 })
        {
            // Audit finding (engine-architect): every cooked SceneRecipe scene ignores AGAPANTHE_LOAD unless the
            // scene itself authors a [restore] block (none does yet) — true since Contenu-3b for model/grid/drop/
            // metalrough, not a 3c-1-specific regression, but it was worth a warning the same way DriveSceneRecipe
            // already has one rather than a silent no-op.
            Log.Warn(
                $"Sandbox: AGAPANTHE_LOAD is set but scene '{Name}' has no [restore] block — it is ignored. "
                + "Author one in the scene's .toml to resume a snapshot.");
        }

        Log.Info($"Sandbox: [scene '{Name}'] {sim.World.LiveEntityCount} entities from cooked data.");

        // Contenu-3c: scene systems are a client-only concept (window/camera-coupled) — a headless build must
        // never silently run a partial simulation for a scene that declares one (HeadlessSim's RunScene refuses
        // such a scene outright before Build is even called, so this is a defence against a future headless
        // caller that skips that check, not the primary gate).
        if (def.Systems.Count > 0 && presentation is null)
        {
            throw new InvalidOperationException(
                $"scene '{Name}' declares {def.Systems.Count} game system(s), which require a presentation context.");
        }

        if (presentation is { } p)
        {
            ClientScenePresenter.Apply(result, sim, p);

            // Audit finding (engine-architect): SimulationHost.ApplyCommand/InputMap/SampleInput are single-slot
            // properties, not multicast — hoisted here (one generic check) rather than duplicated inside every
            // ISceneSystemFactory that wires input, which would be an easy thing for a future factory to forget.
            var priorApplyCommand = sim.Simulation.ApplyCommand;
            foreach (var spec in def.Systems)
            {
                var factory = p.SceneSystemFactories.FirstOrDefault(f => f.Kind == spec.Kind)
                    ?? throw new InvalidOperationException(
                        $"scene '{Name}' declares a '{spec.Kind}' system but this game registered no factory for it.");
                var system = factory.Create(spec, sim, p, result);
                if (!ReferenceEquals(sim.Simulation.ApplyCommand, priorApplyCommand) && priorApplyCommand is not null)
                {
                    throw new InvalidOperationException(
                        $"scene '{Name}' declares more than one input-wiring system — SimulationHost.ApplyCommand is single-slot today.");
                }

                priorApplyCommand = sim.Simulation.ApplyCommand;
                sim.AddSystem(factory.Stage, system);
            }
        }
    }
}
