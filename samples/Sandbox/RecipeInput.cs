using Agapanthe.App;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Rendering;
using Silk.NET.Input;

namespace Sandbox;

/// <summary>
/// Input plumbing shared by the scene recipes: the free-fly camera <c>Updated</c> handler (the engine's camera
/// binding — the same for every free-fly scene) and the <c>B</c>-key probe-spawn command. This is generic glue
/// that the <c>Agapanthe.App</c> layer will eventually absorb (a <c>SceneContext.EnableFreeFlyCamera()</c>); until
/// then it lives here so <c>ModelSceneRecipe</c> and the planet recipes do not each inline it.
/// </summary>
internal static class RecipeInput
{
    /// <summary>Opaque SimCommand.Kind bytes the Sandbox defines for itself (the engine never interprets them).</summary>
    public const byte SpawnProbeCommandKind = 1;

    /// <summary>Drives <see cref="SceneContext.Controller"/> from the window's per-frame <c>Updated</c> event — the
    /// one place <c>MouseDelta</c> is valid (it is zeroed right after <c>Updated</c>). 0-alloc: <see cref="CameraInput"/>
    /// is a <c>readonly struct</c> passed <c>in</c>.</summary>
    public static void WireFreeFly(SceneContext ctx)
    {
        var window = ctx.Window;
        var camera = ctx.Camera;
        var controller = ctx.Controller;
        var inputDebug = Environment.GetEnvironmentVariable("AGAPANTHE_INPUT_DEBUG") is { Length: > 0 };

        window.Updated += dt =>
        {
            if (!window.MouseCaptured)
            {
                controller.ResetLook();
                return;
            }

            var input = new CameraInput(
                moveForward: window.IsKeyDown(Key.W) || window.IsKeyDown(Key.Z) || window.IsKeyDown(Key.Up),
                moveBackward: window.IsKeyDown(Key.S) || window.IsKeyDown(Key.Down),
                moveLeft: window.IsKeyDown(Key.A) || window.IsKeyDown(Key.Q) || window.IsKeyDown(Key.Left),
                moveRight: window.IsKeyDown(Key.D) || window.IsKeyDown(Key.Right),
                moveUp: window.IsKeyDown(Key.Space),
                moveDown: window.IsKeyDown(Key.ControlLeft) || window.IsKeyDown(Key.C),
                lookDelta: window.MouseDelta,
                sprint: window.IsKeyDown(Key.ShiftLeft));

            if (inputDebug)
            {
                var before = camera.Position;
                controller.Update(camera, (float)dt, in input);
                var delta = camera.Position - before;
                if (input.MoveForward || input.MoveBackward || input.MoveLeft || input.MoveRight
                    || input.MoveUp || input.MoveDown)
                {
                    Log.Info(
                        $"[input] fwd={input.MoveForward} back={input.MoveBackward} left={input.MoveLeft} "
                        + $"right={input.MoveRight} | delta=({delta.X:F3}, {delta.Y:F3}, {delta.Z:F3})");
                }

                return;
            }

            controller.Update(camera, (float)dt, in input);
        };
    }

    /// <summary>Wires the <c>B</c> key to enqueue a probe-spawn command stamped for the next tick, carrying
    /// <c>camera.Position</c> — client context the declarative InputMap cannot supply. Drained inside Tick on the
    /// sim owner thread, then routed by the recipe's <c>ApplyCommand</c>.</summary>
    public static void WireProbeKey(SceneContext ctx)
    {
        var sim = ctx.Simulation;
        var camera = ctx.Camera;
        ctx.Window.KeyPressed += key =>
        {
            if (key == Key.B)
            {
                sim.Commands.Enqueue(new SimCommand(sim.TickIndex, SpawnProbeCommandKind, default, camera.Position, 0f, 0u));
            }
        };
    }
}
