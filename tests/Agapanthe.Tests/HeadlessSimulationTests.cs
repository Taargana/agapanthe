using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0a — the headline claim, replayed in process: a simulation runs with <b>no renderer, no device, no window</b>,
/// and it is deterministic.
/// <para>
/// <c>samples/HeadlessSim</c> proves this as a NativeAOT binary, but that binary is published and run by hand. A
/// claim whose only evidence is a manual run is a claim that rots — so the same sequence
/// (<c>GameWorld</c> → <see cref="SimulationHost"/> → <see cref="PhysicsSystem"/> → N ticks → <c>Save</c>) runs
/// here on every CI pass. Nothing in this file references <c>Agapanthe.Rendering</c> or <c>Agapanthe.Graphics</c>,
/// which is the point.
/// </para>
/// </summary>
[Collection("World")]
public sealed class HeadlessSimulationTests
{
    private const float Dt = 1f / 60f;
    private const int Ticks = 120;

    private static readonly PhysicsSettings Settings =
        new(new Vector3(0f, -9.81f, 0f), groundY: 0f, fixedDt: Dt);

    private static byte[] RunHeadless(int ticks)
    {
        using var world = new GameWorld();
        for (var i = 0; i < 4; i++)
        {
            var spec = new ImportedEntitySpec(
                new MeshHandle(0, 1), new MaterialHandle(0, 1),
                new Double3(i * 0.9, 4 + (i * 1.7), 0), Matrix4x4.Identity, Vector3.Zero, 1f, (uint)i);
            world.SpawnBody(in spec, new Vector3(0.05f * i, 0f, 0f), inverseMass: 1f, restitution: 0.4f, radius: 1f);
        }

        var root = world.Spawn(new Double3(40, 8, 0), Quaternion.Identity, 1f);
        world.Spawn(new Double3(1, 0, 0), Quaternion.Identity, 1f, root);
        world.FlushStructuralChanges();

        var host = SimulationHost.CreateDefault(world);
        host.Add(Stage.Simulation, new PhysicsSystem(world, in Settings));

        for (var i = 0; i < ticks; i++)
        {
            host.BeginFrame();
            host.Tick(Dt);
            host.EndFrame();
        }

        using var output = new MemoryStream();
        world.Save(output);
        return output.ToArray();
    }

    [Fact]
    public void ASimulationRunsWithNoRenderer()
    {
        var snapshot = RunHeadless(Ticks);

        Assert.NotEmpty(snapshot);
    }

    [Fact]
    public void TwoHeadlessRunsAreByteIdentical()
    {
        // The determinism the dedicated server will depend on. Cross-process and cross-compiler (JIT vs NativeAOT)
        // determinism is checked by publishing samples/HeadlessSim; this covers the in-process half continuously.
        Assert.Equal(RunHeadless(Ticks), RunHeadless(Ticks));
    }

    [Fact]
    public void TheMeasurementBracketRecordsOneSamplePerFrame()
    {
        using var world = new GameWorld();
        var host = SimulationHost.CreateDefault(world);

        for (var i = 0; i < 10; i++)
        {
            host.BeginFrame();
            host.Tick(Dt);
            host.EndFrame();
        }

        // 9, not 10: FrameStats deliberately drops the first sample (the warm-up frame is noise and would dominate
        // the graph's scale for its whole retention window — see FrameStats.Record).
        Assert.Equal(9, host.Stats.FrameCount);
    }

    /// <summary>
    /// The reason <see cref="SimulationHost.BeginFrame"/> is separate from <see cref="SimulationHost.Tick"/>: under
    /// the fixed-step accumulator the time-authority sub-milestone will introduce, several ticks share one frame,
    /// and the profiler must still see <b>one</b> sample. With the bracket opening inside <c>Tick</c> this recorded
    /// N samples per frame — silently turning the continuously-displayed 0-alloc gate into a per-tick readout.
    /// </summary>
    [Fact]
    public void SeveralTicksInOneFrameRecordASingleSample()
    {
        using var world = new GameWorld();
        var host = SimulationHost.CreateDefault(world);

        // One priming frame first: FrameStats drops its first sample by design.
        host.BeginFrame();
        host.Tick(Dt);
        host.EndFrame();

        // Now three ticks inside ONE frame — the accumulator shape.
        host.BeginFrame();
        host.Tick(Dt);
        host.Tick(Dt);
        host.Tick(Dt);
        host.EndFrame();

        // ONE sample, not three. Before BeginFrame was split out of Tick, this recorded three.
        Assert.Equal(1, host.Stats.FrameCount);
        Assert.Equal(4, host.TickIndex);
    }

    /// <summary>
    /// MP-0c off-by-one fix (spec decision 4): <see cref="SimulationHost.CurrentTick"/> reports the LAST tick
    /// actually executed — <c>Math.Max(0L, TickIndex - 1)</c> — not the raw post-increment counter a render system
    /// used to see (<c>N+1</c> where the tick systems saw <c>N</c>). This was pinned by no test before.
    /// </summary>
    [Fact]
    public void CurrentTick_ReportsTheLastExecutedTick()
    {
        using var world = new GameWorld();
        var host = SimulationHost.CreateDefault(world);

        // Before any tick has run, both readings collapse to 0 (the clamp).
        Assert.Equal(0, host.CurrentTick.TickIndex);

        host.Tick(Dt);
        Assert.Equal(0, host.CurrentTick.TickIndex); // tick 0 just ran — the render pass draws its results

        host.Tick(Dt);
        host.Tick(Dt);
        Assert.Equal(2, host.CurrentTick.TickIndex); // tick 2 was the last; TickIndex (the counter) is 3
        Assert.Equal(3, host.TickIndex);
    }

    /// <summary>
    /// The pre-increment view a system sees INSIDE its own tick is unchanged by the off-by-one fix: a system running
    /// in tick <c>N</c> observes <see cref="TickContext.TickIndex"/> <c>== N</c>. (The render-facing
    /// <see cref="SimulationHost.CurrentTick"/> is the only thing MP-0c moved — see
    /// <see cref="CurrentTick_ReportsTheLastExecutedTick"/>.)
    /// </summary>
    [Fact]
    public void ASystemInsideTickN_SeesTickIndexN()
    {
        using var world = new GameWorld();
        var host = SimulationHost.CreateDefault(world);
        var seen = new List<long>();
        host.Add(Stage.Simulation, new RecordingTickIndexSystem(seen));

        host.Tick(Dt);
        host.Tick(Dt);
        host.Tick(Dt);

        Assert.Equal(new long[] { 0, 1, 2 }, seen);
    }

    private sealed class RecordingTickIndexSystem(List<long> seen) : ISystem
    {
        public void Execute(in TickContext ctx) => seen.Add(ctx.TickIndex);
    }
}
