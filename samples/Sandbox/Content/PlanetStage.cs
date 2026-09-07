using System.Numerics;
using Agapanthe.App;
using Agapanthe.Core;
using Agapanthe.Rendering;
using Agapanthe.World;

namespace Sandbox;

/// <summary>
/// The shared setup for the three planet recipes (<c>planet</c>, <c>planet-drop</c>, <c>planet-challenge</c>):
/// build the planet + Sun (or restore them from <c>AGAPANTHE_LOAD</c>), the sun-only point light, and the black
/// environment. Lifted verbatim from the pre-extraction <c>Program.cs</c> planet branch.
/// </summary>
internal static class PlanetStage
{
    internal readonly record struct PlanetInfo(
        Double3 WorldOrigin, Vector3 SunTravelDir, double PlanetRadius, Double3 SunOrigin, string? LoadPath)
    {
        public bool LoadMode => LoadPath is { Length: > 0 };
    }

    public static PlanetInfo Build(SceneContext ctx)
    {
        var renderer = ctx.Renderer;
        var world = ctx.World;
        var worldOrigin = SandboxEnv.ParseDouble3(Environment.GetEnvironmentVariable("AGAPANTHE_WORLD_ORIGIN"));
        var loadPath = Environment.GetEnvironmentVariable("AGAPANTHE_LOAD");
        var loadMode = loadPath is { Length: > 0 };

        var (sunTravelDir, planetRadius, sunOrigin) = PlanetContent.SetupPlanetScene(
            ctx.Device, ctx.Registry, world, renderer.MaterialSetLayout, worldOrigin, spawnEntities: !loadMode);

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

        return new PlanetInfo(worldOrigin, sunTravelDir, planetRadius, sunOrigin, loadPath);
    }

    /// <summary>Restores <c>AGAPANTHE_LOAD</c> (if set) into the world. The caller MUST have loaded every asset the
    /// snapshot can reference first (planet/Sun here, plus the recipe's own probe/beacon) — Contenu-1's resolver
    /// throws for a key that is not registered, which is exactly the "wrong asset load order" bug being closed.</summary>
    public static void RestoreIfRequested(SceneContext ctx, in PlanetInfo info)
    {
        if (!info.LoadMode)
        {
            return;
        }

        using var loadStream = File.OpenRead(info.LoadPath!);
        // The snapshot stores each MeshRef as a stable AssetKey — re-resolve it to live handles through the registry.
        var result = ctx.World.Load(
            loadStream, SnapshotAllocatorPolicy.AdoptFromHeader, ctx.Registry.ResolveMeshRef);
        Log.Info($"Sandbox: [VS-1] world restored from '{info.LoadPath}' — {result.EntityCount} entities, universe {result.Universe}.");
    }
}
