using System.Numerics;
using Agapanthe.App;
using Agapanthe.Assets;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.World;
using Silk.NET.Input;

namespace Sandbox;

/// <summary>
/// AGAPANTHE_SCENE=drive — the MP-0d interactive demo: one steerable zero-gravity physics body, a fixed camera,
/// and input routed entirely through <see cref="SimCommand"/>s (a declarative axis binding for WASD/Space/C, an
/// <c>X</c>-key brake edge). Reuses the loaded model's mesh so the body renders.
/// </summary>
internal sealed class DriveSceneRecipe : ISceneRecipe
{
    private const byte DriveMoveCommandKind = 2;
    private const byte DriveBrakeCommandKind = 3;
    private const int DriveBrakeBit = 0;
    private const float DriveMoveSpeed = 6f;

    private ulong _pendingBrake; // KeyPressed sets the edge; the next SampleInput consumes and clears it

    public string Name => "drive";

    public void Build(SceneContext ctx)
    {
        var device = ctx.Device;
        var registry = ctx.Registry;
        var world = ctx.World;
        var renderer = ctx.Renderer;
        var camera = ctx.Camera;
        var controller = ctx.Controller;
        var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");

        if (Environment.GetEnvironmentVariable("AGAPANTHE_LOAD") is { Length: > 0 })
        {
            Log.Warn("Sandbox: AGAPANTHE_LOAD is set but the 'drive' scene does not restore snapshots — it is ignored.");
        }

        var modelPath = ModelContent.ResolveModelPath(ctx.Args, modelsDir)
            ?? throw new InvalidOperationException($"Sandbox: model '{ctx.Args[0]}' not found (under '{modelsDir}').");
        var model = GltfLoader.Load(modelPath);
        ModelContent.LogModelStats(model, modelPath);

        var worldOrigin = SandboxEnv.ParseDouble3(Environment.GetEnvironmentVariable("AGAPANTHE_WORLD_ORIGIN"));
        var (_, specs) = registry.Load(device, model, renderer.MaterialSetLayout, worldOrigin);

        var s0 = specs[0];
        var bodyRadius = MathF.Max(s0.BoundsRadius * MathHelpers.MaxStretch(s0.RotationScale), 0.25f);
        var steerable = world.SpawnBody(
            new ImportedEntitySpec(
                s0.Mesh, s0.Material, worldOrigin, s0.RotationScale, s0.BoundsCenter, s0.BoundsRadius, s0.Order),
            Vector3.Zero, inverseMass: 1f, restitution: 0f, radius: bodyRadius);
        Log.Info("Sandbox: [scene] drive — one steerable zero-gravity body (WASD move, Space/C up-down, X brake).");

        var sceneBounds = world.AggregateBounds();
        SandboxCameras.FrameCamera(camera, controller, renderer, in sceneBounds);

        var groundSpawned = false;
        if (Environment.GetEnvironmentVariable("AGAPANTHE_GROUND") is not "0" && !sceneBounds.IsEmpty)
        {
            var extent = sceneBounds.Max - sceneBounds.Min;
            var span = Math.Max(Math.Max(extent.X, extent.Z), 1e-3);
            var groundSize = (float)Math.Max((span * 2.5) + (extent.Y * 8.0), 40.0);
            var groundY = sceneBounds.Min.Y - (extent.Y * 0.02) - 0.01;
            var groundOrigin = new Double3(
                (sceneBounds.Min.X + sceneBounds.Max.X) * 0.5, groundY, (sceneBounds.Min.Z + sceneBounds.Max.Z) * 0.5);
            var (_, groundSpecs) = registry.Load(
                device, ModelContent.BuildGroundModel(groundSize), renderer.MaterialSetLayout, groundOrigin);
            foreach (var spec in groundSpecs)
            {
                world.SpawnImported(in spec, castsShadow: false);
            }

            sceneBounds = world.AggregateBounds();
            groundSpawned = true;
        }

        SandboxCameras.SetupLights(renderer.Lights, in sceneBounds, multiInstance: false);

        if (Environment.GetEnvironmentVariable("AGAPANTHE_HDRI") is { Length: > 0 } hdri && File.Exists(hdri))
        {
            renderer.SetEnvironment(HdrImageLoader.Load(hdri));
        }
        else if (groundSpawned)
        {
            renderer.SetEnvironment(ModelContent.BuildSkyEnvironment(renderer.Lights.Directional.Direction));
        }
        else
        {
            var iblHdrPath = Path.Combine(modelsDir, "studio_small_1k.hdr");
            if (File.Exists(iblHdrPath))
            {
                renderer.SetEnvironment(HdrImageLoader.Load(iblHdrPath));
            }
        }

        // Zero-gravity physics + the declarative InputMap + an ApplyCommand steering the one body.
        var driveSettings = new PhysicsSettings(
            Vector3.Zero, groundY: -100_000f, fixedDt: ctx.Simulation.Settings.FixedDeltaSeconds);
        ctx.Orchestrator.Add(Stage.Simulation, new PhysicsSystem(world, in driveSettings));

        var driveMap = new InputMap();
        driveMap.BindAxisVector(DriveMoveCommandKind, axisX: 0, axisY: 1, axisZ: 2);
        driveMap.BindButton(DriveBrakeBit, DriveBrakeCommandKind, ButtonTrigger.OnPress);
        ctx.Simulation.InputMap = driveMap;

        ctx.Simulation.ApplyCommand = (in SimCommand cmd) =>
        {
            if (!world.IsAlive(steerable))
            {
                return;
            }

            switch (cmd.Kind)
            {
                case DriveMoveCommandKind:
                    world.SetBodyVelocity(steerable, cmd.Vector.ToVector3(Double3.Zero) * DriveMoveSpeed);
                    break;
                case DriveBrakeCommandKind:
                    world.SetBodyVelocity(steerable, Vector3.Zero);
                    break;
            }
        };

        var window = ctx.Window;
        ctx.Simulation.SampleInput = () =>
        {
            var snap = default(InputSnapshot);
            snap.Axes[0] = (window.IsKeyDown(Key.D) ? 1f : 0f)
                - (window.IsKeyDown(Key.A) || window.IsKeyDown(Key.Q) ? 1f : 0f);
            snap.Axes[1] = (window.IsKeyDown(Key.Space) ? 1f : 0f) - (window.IsKeyDown(Key.C) ? 1f : 0f);
            snap.Axes[2] = (window.IsKeyDown(Key.S) ? 1f : 0f)
                - (window.IsKeyDown(Key.W) || window.IsKeyDown(Key.Z) ? 1f : 0f);
            snap.Pressed = _pendingBrake;
            _pendingBrake = 0;
            return snap;
        };

        window.KeyPressed += key =>
        {
            if (key == Key.X)
            {
                _pendingBrake |= 1UL << DriveBrakeBit;
            }
        };

        // No window.Updated subscription: the camera is fixed — WASD steers the body, not the view.
    }
}
