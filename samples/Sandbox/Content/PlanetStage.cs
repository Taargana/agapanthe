using System.Numerics;
using Agapanthe.App;
using Agapanthe.Core;
using Agapanthe.Rendering;

namespace Sandbox;

/// <summary>
/// The shared setup for the three planet recipes (<c>planet</c>, <c>planet-drop</c>, <c>planet-challenge</c>):
/// build the planet + Sun (or restore them from <c>AGAPANTHE_LOAD</c>), the sun-only point light, and the black
/// environment. Lifted verbatim from the pre-extraction <c>Program.cs</c> planet branch.
/// </summary>
internal static class PlanetStage
{
    internal readonly record struct PlanetInfo(
        Double3 WorldOrigin, Vector3 SunTravelDir, double PlanetRadius, Double3 SunOrigin, bool LoadMode);

    public static PlanetInfo Build(SceneContext ctx)
    {
        var renderer = ctx.Renderer;
        var world = ctx.World;
        var worldOrigin = SandboxEnv.ParseDouble3(Environment.GetEnvironmentVariable("AGAPANTHE_WORLD_ORIGIN"));
        var loadPath = Environment.GetEnvironmentVariable("AGAPANTHE_LOAD");
        var loadMode = loadPath is { Length: > 0 };

        var (sunTravelDir, planetRadius, sunOrigin) = PlanetContent.SetupPlanetScene(
            ctx.Device, ctx.Registry, world, renderer.MaterialSetLayout, worldOrigin, spawnEntities: !loadMode);

        if (loadMode)
        {
            using var loadStream = File.OpenRead(loadPath!);
            var result = world.Load(loadStream);
            Log.Info($"Sandbox: [VS-1] world restored from '{loadPath}' — {result.EntityCount} entities, universe {result.Universe}.");
        }

        // The Sun is the ONLY emitter: a point light co-located with the Sun sphere (inverse-square), not an
        // abstract directional. At 7.48e10 m the rays reach near-parallel — the same crisp terminator — but the
        // light is now TIED to the Sun's position.
        var sunDist = Double3.Distance(worldOrigin, sunOrigin);
        const float targetIrradiance = 3.5f;
        renderer.Lights.Points[0] = new PointLight
        {
            Position = sunOrigin,
            Color = new Vector3(1f, 0.97f, 0.92f),
            Intensity = (float)(targetIrradiance * sunDist * sunDist),
            Range = 0f,
        };
        renderer.Lights.PointCount = 1;
        renderer.Lights.Directional = new DirectionalLight
        {
            Direction = sunTravelDir,
            Color = Vector3.One,
            Intensity = 0f,
        };
        renderer.Lights.Ambient = Vector3.Zero;

        renderer.SetEnvironment(PlanetContent.BuildBlackEnvironment());
        renderer.ClearColor = (0f, 0f, 0f, 1f);
        Log.Info("Sandbox: environment = black space (sun-only).");

        return new PlanetInfo(worldOrigin, sunTravelDir, planetRadius, sunOrigin, loadMode);
    }
}
