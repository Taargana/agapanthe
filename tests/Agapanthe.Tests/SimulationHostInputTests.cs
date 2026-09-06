using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0d W2 — the input phase wired into <see cref="SimulationHost.Tick"/>: full sample→translate→drain→apply
/// path, the "no callback = no-op" regression guard (capture stability), the one-edge-per-press contract across a
/// catch-up frame, a dead target absorbed by the app handler, and the discrete direct-enqueue path.
/// </summary>
[Collection("World")]
public sealed class SimulationHostInputTests : IDisposable
{
    private const float Fixed = 1f / 60f;
    private const byte MoveKind = 1;
    private const byte SpawnKind = 2;
    private const float MoveSpeed = 4f;

    private readonly List<GameWorld> _worlds = [];

    public void Dispose()
    {
        foreach (var world in _worlds)
        {
            world.Dispose();
        }
    }

    private (GameWorld World, SimulationHost Host, EntityRef Body) NewSteerableScene()
    {
        var world = new GameWorld();
        _worlds.Add(world);

        var spec = new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), Double3.Zero, Matrix4x4.Identity, Vector3.Zero, 1f, 0u);
        var body = world.SpawnBody(in spec, Vector3.Zero, inverseMass: 1f, restitution: 0f, radius: 0.5f);

        var host = SimulationHost.CreateDefault(world);
        var settings = new PhysicsSettings(Vector3.Zero, groundY: -100_000f, fixedDt: Fixed);
        host.Add(Stage.Simulation, new PhysicsSystem(world, in settings));
        return (world, host, body);
    }

    // The demo-style apply point: a move steers cmd.Target if set (else the scene's known body), and only if it is
    // live — a buffered/replayed command against a despawned entity must no-op, not throw. A spawn kind just counts.
    private static SimCommandHandler DemoApply(GameWorld world, EntityRef body, List<byte> applied)
        => (in SimCommand cmd) =>
        {
            applied.Add(cmd.Kind);
            if (cmd.Kind == MoveKind)
            {
                var target = cmd.Target.IsNone ? body : cmd.Target;
                if (world.IsAlive(target))
                {
                    world.SetBodyVelocity(target, cmd.Vector.ToVector3(Double3.Zero) * MoveSpeed);
                }
            }
        };

    [Fact]
    public void FullPhase_ScriptedInput_DrivesADeterministicPosition()
    {
        static Double3 Run(SimulationHostInputTests self)
        {
            var (world, host, body) = self.NewSteerableScene();
            var map = new InputMap();
            map.BindAxisVector(MoveKind, axisX: 0, axisY: 1, axisZ: 2);
            host.InputMap = map;
            host.ApplyCommand = DemoApply(world, body, []);
            host.SampleInput = () =>
            {
                var s = default(InputSnapshot);
                s.Axes[0] = 1f; // steady +X
                return s;
            };

            for (var i = 0; i < 60; i++)
            {
                host.Tick(Fixed);
            }

            return world.GetWorldPosition(body);
        }

        var a = Run(this);
        var b = Run(this);

        Assert.Equal(a, b);                       // run-to-run determinism
        Assert.True(a.X > 0.9);                   // moved in +X (60 * 1 * MoveSpeed * Fixed ≈ 4)
        Assert.Equal(0.0, a.Y);
        Assert.Equal(0.0, a.Z);
    }

    [Fact]
    public void NoSampleInput_InputPhaseIsSkipped_CommandsStayEmpty()
    {
        var (_, host, _) = NewSteerableScene();
        host.InputMap = new InputMap();
        host.InputMap.BindButton(0, MoveKind, ButtonTrigger.WhileHeld);
        // host.SampleInput left null.

        for (var i = 0; i < 30; i++)
        {
            host.Tick(Fixed);
        }

        Assert.Equal(0, host.Commands.Count);
        Assert.Equal(30, host.TickIndex);
    }

    [Fact]
    public void OneEdgePress_AcrossACatchUpFrameOfThreeTicks_YieldsExactlyOneCommand()
    {
        var (world, host, body) = NewSteerableScene();
        var map = new InputMap();
        map.BindButton(bit: 4, SpawnKind, ButtonTrigger.OnPress);
        host.InputMap = map;

        var applied = new List<byte>();
        host.ApplyCommand = (in SimCommand cmd) => applied.Add(cmd.Kind);

        // The app's callback owns the pending-edge mask: it reports the press once, then clears it.
        var pending = 1UL << 4;
        host.SampleInput = () =>
        {
            var s = new InputSnapshot { Pressed = pending, Held = 1UL << 4 };
            pending = 0;
            return s;
        };

        var accumulator = new FixedTimestepAccumulator(Fixed);
        var ticks = accumulator.AdvanceFrame(host, 3f * Fixed);

        Assert.Equal(3, ticks);
        Assert.Equal(new[] { SpawnKind }, applied.ToArray());
        GC.KeepAlive(world);
        GC.KeepAlive(body);
    }

    [Fact]
    public void DespawnedTarget_DemoHandlerNoOps_TickContinues()
    {
        var (world, host, body) = NewSteerableScene();
        host.ApplyCommand = DemoApply(world, body, []);

        world.Despawn(body);
        host.Tick(Fixed);                     // flush the despawn
        Assert.False(world.IsAlive(body));

        host.Commands.Enqueue(new SimCommand(host.TickIndex, MoveKind, body, new Double3(1, 0, 0), 0f, 0u));
        var ex = Record.Exception(() => host.Tick(Fixed));

        Assert.Null(ex);
        Assert.Equal(2, host.TickIndex);
        Assert.Equal(0, host.Commands.Count);
    }

    [Fact]
    public void QueuedCommandWithNoApplyCommand_IncrementsDiscardedCount_AndDrainsTheQueue()
    {
        var (_, host, _) = NewSteerableScene();
        // host.ApplyCommand left null.

        host.Commands.Enqueue(new SimCommand(host.TickIndex, MoveKind, default, new Double3(1, 0, 0), 0f, 0u));
        host.Commands.Enqueue(new SimCommand(host.TickIndex, SpawnKind, default, Double3.Zero, 0f, 0u));

        host.Tick(Fixed);

        Assert.Equal(2, host.DiscardedCommandCount);
        Assert.Equal(0, host.Commands.Count);
    }

    [Fact]
    public void SetBodyVelocity_OnALiveNonBodyEntity_Throws()
    {
        var (world, _, _) = NewSteerableScene();
        var drawable = world.Spawn(new Double3(5, 0, 0), System.Numerics.Quaternion.Identity, 1f);
        world.FlushStructuralChanges();
        Assert.True(world.IsAlive(drawable));

        Assert.Throws<InvalidOperationException>(() => world.SetBodyVelocity(drawable, Vector3.UnitX));
    }

    [Fact]
    public void DirectEnqueue_KeyBStyle_AppliesOnceAtTheStampedTick_CarryingItsVector()
    {
        var (_, host, _) = NewSteerableScene();

        SimCommand? seen = null;
        var count = 0;
        host.ApplyCommand = (in SimCommand cmd) => { seen = cmd; count++; };

        var cameraPosition = new Double3(10, 20, 30);
        host.Commands.Enqueue(new SimCommand(host.TickIndex, SpawnKind, default, cameraPosition, 0f, 0u));

        host.Tick(Fixed);
        host.Tick(Fixed);

        Assert.Equal(1, count);
        Assert.Equal(SpawnKind, seen!.Value.Kind);
        Assert.Equal(cameraPosition, seen!.Value.Vector);
    }
}
