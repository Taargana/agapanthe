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

    public static PlanetInfo Build(SimSceneContext sim, PresentationSceneContext p)
    {
        var renderer = p.Renderer;
        var world = sim.World;
        var worldOrigin = SandboxEnv.ParseDouble3(Environment.GetEnvironmentVariable("AGAPANTHE_WORLD_ORIGIN"));
        var loadPath = sim.Options.LoadPath;
        var loadMode = loadPath is { Length: > 0 };

        var (sunTravelDir, planetRadius, sunOrigin) = PlanetContent.SetupPlanetScene(
            p.Device, p.Registry, world, renderer.MaterialSetLayout, worldOrigin, spawnEntities: !loadMode);

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

    /// <summary>If <c>AGAPANTHE_LOAD</c> is set, asks the host to restore the snapshot <b>after</b> <c>Build</c>
    /// returns (Contenu-3a). The recipe calls this LAST, once it has registered its own assets (probe/beacon) —
    /// the host then applies the restore with every referenced asset present.</summary>
    public static void RequestRestoreIfRequested(SimSceneContext sim, in PlanetInfo info)
    {
        if (!info.LoadMode)
        {
            return;
        }

        sim.RequestRestore(info.LoadPath!, SnapshotAllocatorPolicy.AdoptFromHeader);
    }
}
