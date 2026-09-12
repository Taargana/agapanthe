using System.Linq;
using Agapanthe.Assets.Scene;
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

        // Contenu-3c-2 (fixes a 🔴 both audits found: F5/AGAPANTHE_LOAD resume was silently write-only for any
        // SceneRecipe-driven scene): AGAPANTHE_LOAD takes priority over the scene's own baked [restore] block
        // (no shipped scene authors one yet — a fixed cook-time snapshot path could never match a runtime
        // AGAPANTHE_LOAD override anyway) and, when set, suppresses entity spawn so the world stays empty for
        // GameWorld.Load — mirrors the pre-3c hand-coded recipes' `spawnEntities: !loadMode`, generalized here
        // instead of reimplemented per recipe.
        var loadPath = sim.Options.LoadPath;
        var spawnEntities = loadPath is not { Length: > 0 };

        // Audit finding (3c-3, both csharp-lowlevel and engine-architect, 🟠): a system that resolves its target
        // via MaterializeResult.SpawnedEntities (DriveControl's ControlledEntityIndex) fundamentally cannot
        // survive spawn suppression — the restored world's entities are NOT the cook-time-ordered spawn list an
        // index names, so there is no correct "resolve later" story here (unlike LandingChallengeSystem, whose
        // seed is just a count re-derived from world state post-restore). Reject loudly and specifically, before
        // dispatch, rather than let the factory throw a generic "materializer ordering bug" message for what is
        // actually a perfectly reachable user invocation (AGAPANTHE_LOAD on a `drive`-shaped scene).
        if (!spawnEntities && def.Systems.Any(s => s.Kind == SceneSystemKind.DriveControl))
        {
            throw new InvalidOperationException(
                $"scene '{Name}' declares a DriveControl system, which steers an entity spawned at cook time — "
                + "it is incompatible with a pending restore (AGAPANTHE_LOAD or a scene [restore] block), since "
                + "spawn is suppressed until the restore is applied. Do not combine AGAPANTHE_LOAD with this scene.");
        }

        var result = Agapanthe.Scene.SceneLoader.LoadHeadless(
            def, sim.Catalog, sim.World, sim.Simulation.Settings.FixedDeltaSeconds, spawnEntities);

        if (result.Physics is { } ps)
        {
            sim.AddSystem(Stage.Simulation, new PhysicsSystem(sim.World, in ps));
            Log.Info($"Sandbox: [scene '{Name}'] physics — gravity {ps.Gravity}, ground y={ps.GroundY:F2}.");
        }

        if (loadPath is { Length: > 0 })
        {
            sim.RequestRestore(loadPath, SnapshotAllocatorPolicy.AdoptFromHeader);
        }
        else if (result.RestorePath is { } rp)
        {
            sim.RequestRestore(rp, SnapshotAllocatorPolicy.AdoptFromHeader);
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
            // Widened in 3c-3 (closes 3c-2 audit finding F4): the guard originally covered only ApplyCommand,
            // leaving a factory that assigns InputMap/SampleInput without touching ApplyCommand undetected —
            // DriveControlSystemFactory is the first factory to assign all three.
            var priorApplyCommand = sim.Simulation.ApplyCommand;
            var priorInputMap = sim.Simulation.InputMap;
            var priorSampleInput = sim.Simulation.SampleInput;
            foreach (var spec in def.Systems)
            {
                // Audit finding (3c-3, engine-architect, 🟡): FirstOrDefault silently picks the first of several
                // factories registered for the same Kind — a shadowed second registration is exactly the kind of
                // bug a debugging session doesn't enjoy. SingleOrDefault + an explicit ambiguity message costs
                // nothing at today's registry sizes (3 factories).
                var matches = p.SceneSystemFactories.Where(f => f.Kind == spec.Kind).ToList();
                var factory = matches.Count switch
                {
                    0 => throw new InvalidOperationException(
                        $"scene '{Name}' declares a '{spec.Kind}' system but this game registered no factory for it."),
                    1 => matches[0],
                    _ => throw new InvalidOperationException(
                        $"scene '{Name}' declares a '{spec.Kind}' system, but this game registered {matches.Count} "
                        + "factories for it — exactly one is required."),
                };
                var system = factory.Create(spec, sim, p, result);

                var clobbered =
                    (priorApplyCommand is not null && !ReferenceEquals(sim.Simulation.ApplyCommand, priorApplyCommand))
                    || (priorInputMap is not null && !ReferenceEquals(sim.Simulation.InputMap, priorInputMap))
                    || (priorSampleInput is not null && !ReferenceEquals(sim.Simulation.SampleInput, priorSampleInput));
                if (clobbered)
                {
                    throw new InvalidOperationException(
                        $"scene '{Name}' declares more than one input-wiring system — SimulationHost.ApplyCommand/"
                        + "InputMap/SampleInput are single-slot today.");
                }

                priorApplyCommand = sim.Simulation.ApplyCommand;
                priorInputMap = sim.Simulation.InputMap;
                priorSampleInput = sim.Simulation.SampleInput;
                sim.AddSystem(factory.Stage, system);
            }
        }
    }
}
