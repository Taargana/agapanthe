using System.Numerics;
using Agapanthe.App;
using Agapanthe.Assets.Scene;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Scene;
using Agapanthe.World;
using Silk.NET.Input;

namespace Agapanthe.Platform.App.Systems;

/// <summary>
/// Contenu-3c-3 — the <see cref="ISceneSystemFactory"/> for <see cref="SceneSystemKind.DriveControl"/>: resolves
/// the body <see cref="MaterializeResult.SpawnedEntities"/>[<see cref="SceneSystem.ControlledEntityIndex"/>]
/// names (a materializer ordering bug if null/missing — <see cref="SceneCompiler"/> rejects a
/// <c>drive_control</c> system paired with a <c>[restore]</c> block at cook time, and <see cref="SceneRecipe"/>
/// separately rejects the runtime <c>AGAPANTHE_LOAD</c> combination it can't see — so this index should always
/// name a spawned entity by the time this runs), then wires exactly the declarative
/// <see cref="InputMap"/>/<see cref="SimulationHost.SampleInput"/>/<see cref="SimulationHost.ApplyCommand"/>
/// steering the pre-Contenu-3c-3 <c>DriveSceneRecipe</c> used to wire by hand: a continuous WASD/Space/C
/// axis-vector move and an <c>X</c>-key brake edge, scaled by <see cref="SceneSystem.MoveSpeed"/>.
/// <para>
/// The factory itself is stateless (one instance is safely shared by every scene load — the same
/// registry-entry contract <c>ProbeDropSystemFactory</c>/<c>LandingChallengeSystemFactory</c> hold) — the
/// brake-edge latch and the <c>KeyPressed</c> subscription both live on the returned
/// <see cref="DriveControlSystem"/> instance instead of a factory field (audit finding, Contenu-3c-3: a
/// factory field would accumulate a new subscription and leak stale latched state across a hypothetical
/// future <c>Create</c> call on the same instance, e.g. an in-process scene reload).
/// </para>
/// <para>
/// Slice-2: moved here from <c>samples/Sandbox/Systems</c> (made <c>public</c>, was <c>internal</c>) so a
/// second windowed app (<c>samples/TopDown</c>) can register the same <see cref="SceneSystemKind.DriveControl"/>
/// factory instead of duplicating it — no behavior change, only visibility and namespace.
/// </para>
/// </summary>
public sealed class DriveControlSystemFactory : ISceneSystemFactory
{
    private const byte DriveMoveCommandKind = 2;
    private const byte DriveBrakeCommandKind = 3;
    private const int DriveBrakeBit = 0;

    public SceneSystemKind Kind => SceneSystemKind.DriveControl;

    public Stage Stage => Stage.Input;

    public ISystem Create(SceneSystem spec, SimSceneContext sim, PresentationSceneContext presentation, MaterializeResult result)
    {
        if (spec.ControlledEntityIndex < 0 || spec.ControlledEntityIndex >= result.SpawnedEntities.Count
            || result.SpawnedEntities[spec.ControlledEntityIndex] is not { } steerable)
        {
            throw new InvalidOperationException(
                $"a drive_control system's controlled_entity_index ({spec.ControlledEntityIndex}) does not name a "
                + "spawned body — SceneCompiler/SceneRecipe should have rejected this before this factory ran.");
        }

        var world = sim.World;
        var moveSpeed = spec.MoveSpeed;
        var system = new DriveControlSystem();

        var driveMap = new InputMap();
        driveMap.BindAxisVector(DriveMoveCommandKind, axisX: 0, axisY: 1, axisZ: 2);
        driveMap.BindButton(DriveBrakeBit, DriveBrakeCommandKind, ButtonTrigger.OnPress);
        sim.Simulation.InputMap = driveMap;

        sim.Simulation.ApplyCommand = (in SimCommand cmd) =>
        {
            if (!world.IsAlive(steerable))
            {
                return;
            }

            switch (cmd.Kind)
            {
                case DriveMoveCommandKind:
                    world.SetBodyVelocity(steerable, cmd.Vector.ToVector3(Double3.Zero) * moveSpeed);
                    break;
                case DriveBrakeCommandKind:
                    world.SetBodyVelocity(steerable, Vector3.Zero);
                    break;
            }
        };

        var window = presentation.Window;
        sim.Simulation.SampleInput = () =>
        {
            var snap = default(InputSnapshot);
            snap.Axes[0] = (window.IsKeyDown(Key.D) ? 1f : 0f)
                - (window.IsKeyDown(Key.A) || window.IsKeyDown(Key.Q) ? 1f : 0f);
            snap.Axes[1] = (window.IsKeyDown(Key.Space) ? 1f : 0f) - (window.IsKeyDown(Key.C) ? 1f : 0f);
            snap.Axes[2] = (window.IsKeyDown(Key.S) ? 1f : 0f)
                - (window.IsKeyDown(Key.W) || window.IsKeyDown(Key.Z) ? 1f : 0f);
            snap.Pressed = system.PendingBrake;
            system.PendingBrake = 0;
            return snap;
        };

        window.KeyPressed += key =>
        {
            if (key == Key.X)
            {
                system.PendingBrake |= 1UL << DriveBrakeBit;
            }
        };

        // No window.Updated subscription: the camera is fixed — WASD steers the body, not the view.
        return system;
    }

    // All the actual work happens through the InputMap/SampleInput/ApplyCommand callbacks wired above (driven by
    // SimulationHost's own input phase) and the KeyPressed brake-edge subscription — the pre-3c-3 hand-coded
    // recipe never needed a per-tick ISystem either; Execute is a no-op that exists only to satisfy
    // ISceneSystemFactory.Create's return contract and give SceneRecipe something to sim.AddSystem. PendingBrake
    // lives here (not on the factory) so each Create call gets its own latch, tied to the KeyPressed subscription
    // that same call adds.
    private sealed class DriveControlSystem : ISystem
    {
        public ulong PendingBrake;

        public void Execute(in TickContext ctx)
        {
        }
    }
}
