using Agapanthe.App;
using Agapanthe.Core;
using Agapanthe.Engine;
using Silk.NET.Input;

namespace Sandbox;

/// <summary>
/// AGAPANTHE_SCENE=planet-challenge — the VS-3 gameplay-glue demo: the planet + Newtonian physics, plus a target
/// zone/beacon and the aimed landing challenge (fly, <c>B</c> drops a probe radially below the camera, land N in
/// ≤ M shots; <c>F5</c> quicksaves, relaunch with <c>AGAPANTHE_LOAD</c> to resume).
/// </summary>
internal sealed class PlanetChallengeSceneRecipe : ISceneRecipe
{
    public string Name => "planet-challenge";

    public void Build(SimSceneContext sim, PresentationSceneContext? presentation)
    {
        var p = presentation ?? throw new InvalidOperationException("planet-challenge scene requires a presentation context");
        var info = PlanetStage.Build(sim, p);

        var setup = PlanetContent.SetupPlanetChallenge(
            p.Device, p.Registry, sim.World, p.Renderer.MaterialSetLayout,
            info.WorldOrigin, info.PlanetRadius, sim.Simulation.Settings.FixedDeltaSeconds, spawnEntities: !info.LoadMode);

        // The probe + beacon assets are now registered — safe to restore a snapshot that references them.
        PlanetStage.RequestRestoreIfRequested(sim, in info);

        var physics = setup.Physics;
        sim.AddSystem(Stage.Simulation, new PhysicsSystem(sim.World, in physics));

        var probeSpec = setup.ProbeSpec;
        var challenge = new LandingChallengeSystem(
            sim.World, p.Window, info.WorldOrigin, info.PlanetRadius, setup.SurfaceBand, setup.Target,
            setup.TargetRadius, in probeSpec, setup.ProbeRadius, setup.DropHeight, setup.TargetCount, setup.ShotBudget);
        sim.AddSystem(Stage.PostSimulation, challenge);
        Log.Info(
            $"Sandbox: [challenge] planet-challenge enabled — land {setup.TargetCount} probes in ≤ {setup.ShotBudget} shots; "
            + "fly, B drops (aimed radial), F5 saves. Relaunch with AGAPANTHE_LOAD to resume.");

        sim.Simulation.ApplyCommand = (in SimCommand cmd) =>
        {
            if (cmd.Kind == RecipeInput.SpawnProbeCommandKind)
            {
                challenge.TryShoot(cmd.Vector); // aimed radial drop below the camera, budget-checked
            }
        };

        SandboxCameras.FramePlanetChallengeCamera(
            p.Camera, p.Controller, p.Renderer, info.WorldOrigin, info.SunOrigin, info.PlanetRadius, setup.Beacon);
        RecipeInput.WireFreeFly(sim, p);
        RecipeInput.WireProbeKey(sim, p);

        var world = sim.World;
        p.Window.KeyPressed += key =>
        {
            if (key != Key.F5)
            {
                return;
            }

            var saveTarget = Environment.GetEnvironmentVariable("AGAPANTHE_SAVE") is { Length: > 0 } sp ? sp : "challenge.save";
            try
            {
                using var fs = File.Create(saveTarget);
                world.Save(fs); // Contenu-3a: entities carry their AssetRef → stable AssetKeys, no delegate
                Log.Info($"Sandbox: [challenge] quicksaved to '{saveTarget}'. Relaunch AGAPANTHE_SCENE=planet-challenge " +
                         $"AGAPANTHE_LOAD={saveTarget} to resume.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Sandbox: [challenge] quicksave to '{saveTarget}' failed: {ex.Message}");
            }
        };
    }
}
