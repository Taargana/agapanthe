using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0c decision 8 — the milestone's real goal is that simulation SPEED does not depend on frame rate, and that is
/// invisible on a static hash. These tests drive a <see cref="FixedTimestepAccumulator"/> with wall-clock deltas
/// chunked differently for the same total time and check the outcome does not move.
/// <para>
/// <b>The primary assertion is the integer tick count</b> (spec F1/F4): because <see cref="PhysicsSystem"/> ignores
/// <c>TickContext.DeltaSeconds</c> (decision 3), two runs that issue the same number of fixed steps produce
/// identical positions <i>by construction</i> — so the position equality is a wiring guard, not the proof. The
/// step-count-sensitivity companion keeps both from passing vacuously on a settled scene.
/// </para>
/// <para>
/// Every profile is built from <see cref="FixedTimestepAccumulator.FixedDeltaSeconds"/>, never <c>n / 60f</c>:
/// <c>3f / 60f</c> and <c>3f * (1f / 60f)</c> differ by 1 ULP, which makes a "3 ticks" profile yield 2 on its first
/// call (the trap the scored review caught).
/// </para>
/// </summary>
[Collection("World")]
public sealed class AccumulatorEquivalenceTests : IDisposable
{
    private const float Fixed = 1f / 60f;

    // Asymmetric overlapping cluster: distinct mass, restitution and velocity per body, so the trajectory keeps
    // evolving over tens of ticks and is sensitive to how many ran (a symmetric settled scene would be tautological).
    private readonly record struct BodyDef(Double3 Position, Vector3 Velocity, float InverseMass, float Restitution);

    private static readonly BodyDef[] Bodies =
    [
        new(new Double3(0, 0, 0), new Vector3(0.6f, -0.3f, 0.1f), InverseMass: 1.0f, Restitution: 0.2f),
        new(new Double3(1.4, 0, 0), new Vector3(-0.4f, 0.5f, -0.2f), InverseMass: 0.5f, Restitution: 0.5f),
        new(new Double3(0, 1.4, 0), new Vector3(0.1f, -0.6f, 0.4f), InverseMass: 2.0f, Restitution: 0.8f),
    ];

    private const float Radius = 1.1f; // every pairwise distance (1.4, 1.4, ~1.98) < 2*Radius: all three pairs overlap

    private static readonly PhysicsSettings Settings =
        new(new Vector3(0f, -1f, 0f), groundY: -1_000f, fixedDt: Fixed);

    private readonly List<GameWorld> _worlds = [];

    public void Dispose()
    {
        foreach (var world in _worlds)
        {
            world.Dispose();
        }
    }

    // Builds the cluster, then drives an accumulator with `wallClockDeltas` (one call per element). Returns each
    // body's final world position and the total number of ticks the accumulator ran.
    private (Double3[] Positions, int TotalTicks) RunViaAccumulator(IReadOnlyList<float> wallClockDeltas)
    {
        var world = new GameWorld();
        _worlds.Add(world);

        var refs = new EntityRef[Bodies.Length];
        for (var i = 0; i < Bodies.Length; i++)
        {
            var def = Bodies[i];
            var spec = new ImportedEntitySpec(
                new MeshHandle(0, 1), new MaterialHandle(0, 1), def.Position, Matrix4x4.Identity, Vector3.Zero, 1f, (uint)i);
            refs[i] = world.SpawnBody(in spec, def.Velocity, def.InverseMass, def.Restitution, Radius);
        }

        var host = SimulationHost.CreateDefault(world);
        host.Add(Stage.Simulation, new PhysicsSystem(world, in Settings));
        var accumulator = new FixedTimestepAccumulator(Fixed);

        var total = 0;
        foreach (var dt in wallClockDeltas)
        {
            total += accumulator.Advance(host, dt);
        }

        var positions = new Double3[Bodies.Length];
        for (var i = 0; i < Bodies.Length; i++)
        {
            positions[i] = world.GetWorldPosition(refs[i]);
        }

        return (positions, total);
    }

    private static float[] Repeat(float value, int count)
    {
        var a = new float[count];
        Array.Fill(a, value);
        return a;
    }

    [Fact]
    public void SameSimulatedTime_DifferentChunking_RunsTheSameTickCount()
    {
        // 20 frames of 3 steps each vs 60 frames of 1 step each — same total simulated time, chunked differently.
        var coarse = RunViaAccumulator(Repeat(3f * Fixed, 20));
        var fine = RunViaAccumulator(Repeat(1f * Fixed, 60));

        Assert.Equal(60, coarse.TotalTicks);
        Assert.Equal(60, fine.TotalTicks);
    }

    [Fact]
    public void SameSimulatedTime_DifferentChunking_ProducesIdenticalPositions()
    {
        var coarse = RunViaAccumulator(Repeat(3f * Fixed, 20));
        var fine = RunViaAccumulator(Repeat(1f * Fixed, 60));

        // Bit-for-bit: both consume the identical sequence of StepPhysics(Fixed) calls; only the wall-clock chunking
        // differs, and the accumulator's job is to make that invisible. (Wiring guard — see the class remark.)
        Assert.Equal(coarse.Positions, fine.Positions);
    }

    [Fact]
    public void FewerTicks_ProduceADifferentState()
    {
        // Keeps the two assertions above non-vacuous: the scene must still be evolving at tick 30 vs 60.
        var thirty = RunViaAccumulator(Repeat(1f * Fixed, 30));
        var sixty = RunViaAccumulator(Repeat(1f * Fixed, 60));

        Assert.Equal(30, thirty.TotalTicks);
        Assert.NotEqual(thirty.Positions, sixty.Positions);
    }

    [Fact]
    public void FrameOrchestratorShape_CatchUpRunsSeveralTicksButRecordsOneFrame()
    {
        // FrameOrchestrator.Tick IS `_accumulator.AdvanceFrame(_simulation, dt)` (audit arch F2 — the wiring is now
        // in a GPU-free method a test can call, instead of a shape this test would have to recopy). EndFrame stays
        // the caller's, exactly as FrameOrchestrator does it. One frame is 4 steps behind; the rest are on time.
        var world = new GameWorld();
        _worlds.Add(world);
        var host = SimulationHost.CreateDefault(world);
        var accumulator = new FixedTimestepAccumulator(Fixed);

        var frameDeltas = new[] { Fixed, Fixed, 4f * Fixed, Fixed, Fixed };
        var perFrameTicks = new int[frameDeltas.Length];
        var totalTicks = 0;

        for (var f = 0; f < frameDeltas.Length; f++)
        {
            totalTicks += accumulator.AdvanceFrame(host, frameDeltas[f]);
            host.EndFrame();
            perFrameTicks[f] = host.LastFrameTickCount; // latched by EndFrame (audit arch F3)
        }

        // FrameStats records ONE sample per frame regardless of catch-up (minus the dropped warm-up sample).
        Assert.Equal(frameDeltas.Length - 1, host.Stats.FrameCount);

        // The slow frame caught up with 4 ticks; every other frame ran exactly 1.
        Assert.Equal(new[] { 1, 1, 4, 1, 1 }, perFrameTicks);
        Assert.Equal(8, totalTicks);
        Assert.Equal(8, host.TickIndex);
    }
}
