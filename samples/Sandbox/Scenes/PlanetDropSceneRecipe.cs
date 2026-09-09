using Agapanthe.App;
using Agapanthe.Core;
using Agapanthe.Engine;

namespace Sandbox;

/// <summary>
/// AGAPANTHE_SCENE=planet-drop — the VS-2 Newtonian integration demo: the P3-M8 planet + Sun, plus a runtime probe
/// spawner (a probe every N ticks + on the <c>B</c> key) and radial gravity. Near-surface camera.
/// </summary>
internal sealed class PlanetDropSceneRecipe : ISceneRecipe
{
    public string Name => "planet-drop";

    public void Build(SimSceneContext sim, PresentationSceneContext? presentation)
    {
        var p = presentation ?? throw new InvalidOperationException("planet-drop scene requires a presentation context");
        var info = PlanetStage.Build(sim, p);

        var (physics, probeSpec, dropCentre, probeRadius) = PlanetContent.SetupPlanetDrop(
            p.Device, p.Registry, p.Renderer.MaterialSetLayout, info.WorldOrigin, info.PlanetRadius,
            sim.Simulation.Settings.FixedDeltaSeconds);

        // The probe asset is now registered — safe to restore a snapshot that references it.
        PlanetStage.RequestRestoreIfRequested(sim, in info);

        sim.AddSystem(Stage.Simulation, new PhysicsSystem(sim.World, in physics));

        var dropEvery = (int)Math.Max(SandboxEnv.EnvDouble("AGAPANTHE_DROP_EVERY", 30.0), 1.0);
        var dropper = new ProbeDropSystem(sim.World, in probeSpec, dropCentre, probeRadius, dropEvery);
        sim.AddSystem(Stage.Input, dropper);
        Log.Info($"Sandbox: [physics] planet-drop enabled — a probe every {dropEvery} ticks, key B drops one.");

        // MP-0d: B enqueues a SpawnProbe command (routed here inside the tick, on the sim owner thread).
        sim.Simulation.ApplyCommand = (in SimCommand cmd) =>
        {
            if (cmd.Kind == RecipeInput.SpawnProbeCommandKind)
            {
                dropper.DropOne(); // the spawner owns its golden-angle spiral; cmd.Vector unused here
            }
        };

        SandboxCameras.FramePlanetDropCamera(
            p.Camera, p.Controller, p.Renderer, info.WorldOrigin, info.SunOrigin, info.PlanetRadius, info.SunTravelDir);
        RecipeInput.WireFreeFly(sim, p);
        RecipeInput.WireProbeKey(sim, p);
    }
}
