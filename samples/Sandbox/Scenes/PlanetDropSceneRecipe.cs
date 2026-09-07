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

    public void Build(SceneContext ctx)
    {
        var info = PlanetStage.Build(ctx);

        var (physics, probeSpec, dropCentre, probeRadius) = PlanetContent.SetupPlanetDrop(
            ctx.Device, ctx.Registry, ctx.Renderer.MaterialSetLayout, info.WorldOrigin, info.PlanetRadius,
            ctx.Simulation.Settings.FixedDeltaSeconds);

        ctx.Orchestrator.Add(Stage.Simulation, new PhysicsSystem(ctx.World, in physics));

        var dropEvery = (int)Math.Max(SandboxEnv.EnvDouble("AGAPANTHE_DROP_EVERY", 30.0), 1.0);
        var dropper = new ProbeDropSystem(ctx.World, in probeSpec, dropCentre, probeRadius, dropEvery);
        ctx.Orchestrator.Add(Stage.Input, dropper);
        Log.Info($"Sandbox: [physics] planet-drop enabled — a probe every {dropEvery} ticks, key B drops one.");

        // MP-0d: B enqueues a SpawnProbe command (routed here inside the tick, on the sim owner thread).
        ctx.Simulation.ApplyCommand = (in SimCommand cmd) =>
        {
            if (cmd.Kind == RecipeInput.SpawnProbeCommandKind)
            {
                dropper.DropOne(); // the spawner owns its golden-angle spiral; cmd.Vector unused here
            }
        };

        SandboxCameras.FramePlanetDropCamera(
            ctx.Camera, ctx.Controller, ctx.Renderer, info.WorldOrigin, info.SunOrigin, info.PlanetRadius, info.SunTravelDir);
        RecipeInput.WireFreeFly(ctx);
        RecipeInput.WireProbeKey(ctx);
    }
}
