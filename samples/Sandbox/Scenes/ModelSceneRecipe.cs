using System.Numerics;
using Agapanthe.App;
using Agapanthe.Assets;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Rendering;
using Agapanthe.World;
using Silk.NET.Input;

namespace Sandbox;

/// <summary>
/// The <c>model</c> family: a glTF model rendered once (default / <c>--&lt;model&gt;.glb</c>), as an N×N grid
/// (<c>grid:NxN</c>, the M4 load bench) or as a cloud of physics bodies above the ground (<c>drop:N</c>, P3-M3).
/// One shared code path — glTF load → replicate → ground → 3-point rig → frame — as in the pre-extraction
/// <c>Program.cs</c>. <c>AGAPANTHE_CULL_STATS</c> adds the bench spinner, <c>AGAPANTHE_CHURN</c> the lifecycle
/// stress, <c>AGAPANTHE_PHYSICS=1</c> the rigid-body integration for a <c>drop:</c> scene.
/// </summary>
internal sealed class ModelSceneRecipe : ISceneRecipe
{
    public string Name => "model";

    public bool Matches(string? sceneToken)
        => string.IsNullOrEmpty(sceneToken)
           || string.Equals(sceneToken, Name, StringComparison.OrdinalIgnoreCase)
           || sceneToken.StartsWith("grid:", StringComparison.OrdinalIgnoreCase)
           || sceneToken.StartsWith("drop:", StringComparison.OrdinalIgnoreCase);

    public void Build(SceneContext ctx)
    {
        var device = ctx.Device;
        var registry = ctx.Registry;
        var world = ctx.World;
        var renderer = ctx.Renderer;
        var camera = ctx.Camera;
        var controller = ctx.Controller;
        var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
        var sceneSpec = Environment.GetEnvironmentVariable("AGAPANTHE_SCENE");

        if (Environment.GetEnvironmentVariable("AGAPANTHE_LOAD") is { Length: > 0 })
        {
            Log.Warn(
                $"Sandbox: AGAPANTHE_LOAD is set but scene '{sceneSpec ?? "model"}' does not restore snapshots — "
                + "it is ignored. Use AGAPANTHE_SCENE=planet (or planet-challenge) to resume a saved world.");
        }

        var modelPath = ModelContent.ResolveModelPath(ctx.Args, modelsDir);
        if (modelPath is null)
        {
            throw new InvalidOperationException(
                $"Sandbox: model '{ctx.Args[0]}' not found (tried as-is and under '{modelsDir}').");
        }

        var model = GltfLoader.Load(modelPath);
        ModelContent.LogModelStats(model, modelPath);

        if (int.TryParse(Environment.GetEnvironmentVariable("AGAPANTHE_UNLOAD_TEST"), out var unloadCycles)
            && unloadCycles > 0)
        {
            for (var i = 0; i < unloadCycles; i++)
            {
                var (throwaway, _) = registry.Load(device, model, renderer.MaterialSetLayout);
                registry.Unload(throwaway);
            }

            Log.Info($"Sandbox: [unload-test] {unloadCycles} load/unload cycles done; the leak report below covers them.");
        }

        var worldOrigin = SandboxEnv.ParseDouble3(Environment.GetEnvironmentVariable("AGAPANTHE_WORLD_ORIGIN"));
        var originReach = Math.Max(Math.Max(Math.Abs(worldOrigin.X), Math.Abs(worldOrigin.Y)), Math.Abs(worldOrigin.Z));
        if (originReach > 1e12)
        {
            Log.Warn(
                $"Sandbox: AGAPANTHE_WORLD_ORIGIN is {originReach:0.###e+00} m out — a double's step there is " +
                $"~{Math.BitIncrement(originReach) - originReach:F3} m, so camera movement will be quantized or drop " +
                "entirely on the large axes. Fine for a precision capture; unusable for flying around.");
        }

        var (_, specs) = registry.Load(device, model, renderer.MaterialSetLayout, worldOrigin);

        var (rows, cols) = ModelContent.ParseGrid(sceneSpec);
        var dropCount = ModelContent.ParseDrop(sceneSpec);
        bool multiInstance;
        double? physicsGroundY = null;

        if (dropCount > 0)
        {
            physicsGroundY = ModelContent.SpawnDropScene(world, specs, dropCount, worldOrigin);
            multiInstance = true;
            Log.Info($"Sandbox: [scene] drop {dropCount} bodies = {dropCount * specs.Length} entities " +
                     $"(ground y={physicsGroundY:F2}); set AGAPANTHE_PHYSICS=1 to simulate.");
        }
        else if (rows * cols > 1)
        {
            var spacing = ModelContent.ModelDiagonal(specs) * 1.5;
            ModelContent.SpawnGrid(world, specs, rows, cols, spacing);
            multiInstance = true;
            Log.Info($"Sandbox: [scene] grid {rows}x{cols} = {rows * cols * specs.Length} entities " +
                     $"(spacing {spacing:F1} m), one model upload.");
        }
        else
        {
            foreach (var spec in specs)
            {
                world.SpawnImported(in spec);
            }

            multiInstance = false;
        }

        var sceneBounds = world.AggregateBounds();

        var benchMode = Environment.GetEnvironmentVariable("AGAPANTHE_CULL_STATS") is { Length: > 0 };
        if (benchMode)
        {
            var (center, diagonal) = SandboxCameras.NarrowBounds(in sceneBounds);
            camera.Position = center;
            camera.Yaw = 0f;
            camera.Pitch = 0f;
            camera.Near = MathF.Max(diagonal * 0.001f, 0.01f);
            camera.Far = MathF.Max(diagonal * 0.5f, 1f);
            renderer.ShadowDistance = camera.Far;
        }
        else
        {
            SandboxCameras.FrameCamera(camera, controller, renderer, in sceneBounds);
        }

        var groundSpawned = false;
        if (Environment.GetEnvironmentVariable("AGAPANTHE_GROUND") is not "0" && !sceneBounds.IsEmpty)
        {
            var extent = sceneBounds.Max - sceneBounds.Min;
            var span = Math.Max(Math.Max(extent.X, extent.Z), 1e-3);
            var groundSize = (float)Math.Max((span * 2.5) + (extent.Y * 8.0), 40.0);
            var groundY = physicsGroundY ?? (sceneBounds.Min.Y - (extent.Y * 0.02) - 0.01);
            var groundOrigin = new Double3(
                (sceneBounds.Min.X + sceneBounds.Max.X) * 0.5,
                groundY,
                (sceneBounds.Min.Z + sceneBounds.Max.Z) * 0.5);

            var (_, groundSpecs) = registry.Load(
                device, ModelContent.BuildGroundModel(groundSize), renderer.MaterialSetLayout, groundOrigin);
            foreach (var spec in groundSpecs)
            {
                world.SpawnImported(in spec, castsShadow: false);
            }

            sceneBounds = world.AggregateBounds();
            groundSpawned = true;
            Log.Info(
                $"Sandbox: [scene] ground plane {groundSize:F1} m at y={groundOrigin.Y:F2} (AGAPANTHE_GROUND=0 to remove).");
        }

        SandboxCameras.SetupLights(renderer.Lights, in sceneBounds, multiInstance);

        var hdriOverride = Environment.GetEnvironmentVariable("AGAPANTHE_HDRI");
        if (hdriOverride is { Length: > 0 } && File.Exists(hdriOverride))
        {
            renderer.SetEnvironment(HdrImageLoader.Load(hdriOverride));
            Log.Info($"Sandbox: environment '{hdriOverride}'.");
        }
        else if (groundSpawned)
        {
            renderer.SetEnvironment(ModelContent.BuildSkyEnvironment(renderer.Lights.Directional.Direction));
            Log.Info("Sandbox: environment = procedural outdoor sky (AGAPANTHE_HDRI=<path.hdr> to override).");
        }
        else
        {
            var iblHdrPath = Path.Combine(modelsDir, "studio_small_1k.hdr");
            if (File.Exists(iblHdrPath))
            {
                renderer.SetEnvironment(HdrImageLoader.Load(iblHdrPath));
                Log.Info($"Sandbox: environment '{iblHdrPath}'.");
            }
            else
            {
                Log.Error($"Sandbox: HDRI environment '{iblHdrPath}' not found; the M7 renderer requires one to draw.");
            }
        }

        if (benchMode)
        {
            ctx.Orchestrator.Add(Stage.Simulation, new BenchSpinSystem(world, camera));
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("AGAPANTHE_CHURN"), out var churn) && churn > 0)
        {
            ctx.Orchestrator.Add(Stage.Simulation, new ChurnSystem(world, churn));
        }

        if (physicsGroundY is { } physGroundY && Environment.GetEnvironmentVariable("AGAPANTHE_PHYSICS") is "1")
        {
            // The fixed step comes from the single definition (SimulationSettings), not a re-literalled 1/60 —
            // same value, but PhysicsSystem.RatesMatch's assert now covers this seam.
            var settings = new PhysicsSettings(
                new Vector3(0f, -9.81f, 0f), (float)physGroundY, ctx.Simulation.Settings.FixedDeltaSeconds);
            ctx.Orchestrator.Add(Stage.Simulation, new PhysicsSystem(world, in settings));
            Log.Info($"Sandbox: [physics] enabled — gravity {settings.Gravity}, ground y={settings.GroundY:F2}, " +
                     $"fixed dt {settings.FixedDt:F4}s.");
        }

        RecipeInput.WireFreeFly(ctx);

        Log.Info($"Sandbox: [scene] model at world origin {worldOrigin}, eye at {camera.Position}.");
    }
}
