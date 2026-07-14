using Agapanthe.Engine;

namespace Agapanthe.Tests;

/// <summary>
/// Pins the frame ORDER (P3-M2, decision D1). These tests exist because the order is the whole product of the
/// scheduler: an engine that runs the systems in the wrong order does not crash, it renders a shadow fitted to last
/// frame's bounds — and no other test would ever catch it.
/// </summary>
public sealed class SchedulerTests
{
    private sealed class RecordingSystem(List<string> log, string name) : ISystem
    {
        public void Execute(in TickContext ctx) => log.Add(name);
    }

    private sealed class RecordingRenderSystem(List<string> log, string name) : IRenderSystem
    {
        public void Render(in RenderContext ctx) => log.Add(name);
    }

    [Fact]
    public void Stages_RunInDeclaredOrder()
    {
        var log = new List<string>();
        var scheduler = new SystemScheduler();

        // Registered back-to-front on purpose: only the stage may decide the order, never the registration sequence.
        scheduler.Add(Stage.PostSimulation, new RecordingSystem(log, "post"));
        scheduler.Add(Stage.Simulation, new RecordingSystem(log, "sim"));
        scheduler.Add(Stage.Input, new RecordingSystem(log, "input"));

        scheduler.Tick(1f / 60f);

        Assert.Equal(new[] { "input", "sim", "post" }, log);
    }

    [Fact]
    public void WithinAStage_RegistrationOrderIsExecutionOrder()
    {
        var log = new List<string>();
        var scheduler = new SystemScheduler();
        scheduler.Add(Stage.Simulation, new RecordingSystem(log, "a"));
        scheduler.Add(Stage.Simulation, new RecordingSystem(log, "b"));
        scheduler.Add(Stage.Simulation, new RecordingSystem(log, "c"));

        scheduler.Tick(1f / 60f);

        Assert.Equal(new[] { "a", "b", "c" }, log);
    }

    [Fact]
    public void StructuralBarrier_ClosesEveryStage()
    {
        var log = new List<string>();
        var scheduler = new SystemScheduler(() => log.Add("|"));
        scheduler.Add(Stage.Input, new RecordingSystem(log, "input"));
        scheduler.Add(Stage.Simulation, new RecordingSystem(log, "sim"));
        scheduler.Add(Stage.PostSimulation, new RecordingSystem(log, "post"));

        scheduler.Tick(1f / 60f);

        // A system must never see the entity storage move under its own iteration: the deferred spawns/despawns are
        // applied between stages, never inside one.
        Assert.Equal(new[] { "input", "|", "sim", "|", "post", "|" }, log);
    }

    [Fact]
    public void Tick_RunsNoRenderSystem()
    {
        var log = new List<string>();
        var scheduler = new SystemScheduler();
        scheduler.Add(new RecordingRenderSystem(log, "render"));
        scheduler.Add(Stage.Simulation, new RecordingSystem(log, "sim"));

        // Tick() is what runs on EVERY frame, including the ones the renderer skips (resize, minimize). It must not
        // drag the render systems with it — they need a command list that does not exist on a skipped frame.
        scheduler.Tick(1f / 60f);

        Assert.Equal(new[] { "sim" }, log);
    }

    [Fact]
    public void FrameIndex_AdvancesWithTicksOnly()
    {
        var scheduler = new SystemScheduler();
        Assert.Equal(0, scheduler.FrameIndex);

        scheduler.Tick(1f / 60f);
        scheduler.Tick(1f / 60f);

        Assert.Equal(2, scheduler.FrameIndex);
    }

    [Fact]
    public void TickContext_CarriesTheDeltaAndTheFrameIndex()
    {
        float? seenDelta = null;
        long? seenFrame = null;
        var scheduler = new SystemScheduler();
        scheduler.Add(Stage.Simulation, new LambdaSystem(ctx =>
        {
            seenDelta = ctx.DeltaSeconds;
            seenFrame = ctx.FrameIndex;
        }));

        scheduler.Tick(1f / 60f);
        scheduler.Tick(0.5f);

        Assert.Equal(0.5f, seenDelta);
        Assert.Equal(1, seenFrame); // the SECOND tick sees index 1: the counter advances after the stages, not before
    }

    [Fact]
    public void Add_AfterTheFirstTick_Throws()
    {
        var scheduler = new SystemScheduler();
        scheduler.Tick(1f / 60f);

        // Registering mid-run would mutate a stage's list while it is being iterated.
        Assert.Throws<InvalidOperationException>(
            () => scheduler.Add(Stage.Simulation, new RecordingSystem([], "late")));
        Assert.Throws<InvalidOperationException>(
            () => scheduler.Add(new RecordingRenderSystem([], "late")));
    }

    [Fact]
    public void Add_RenderStage_WithASimulationSystem_Throws()
    {
        var scheduler = new SystemScheduler();

        // The two interfaces are disjoint on purpose: a render system receives GPU handles a simulation system must
        // never be able to touch. Passing Stage.Render here is a category error, and it is caught as one.
        Assert.Throws<ArgumentException>(() => scheduler.Add(Stage.Render, new RecordingSystem([], "oops")));
    }

    [Fact]
    public void CountIn_ReportsEveryStage()
    {
        var scheduler = new SystemScheduler();
        scheduler.Add(Stage.Simulation, new RecordingSystem([], "a"));
        scheduler.Add(Stage.Simulation, new RecordingSystem([], "b"));
        scheduler.Add(new RecordingRenderSystem([], "r"));

        Assert.Equal(0, scheduler.CountIn(Stage.Input));
        Assert.Equal(2, scheduler.CountIn(Stage.Simulation));
        Assert.Equal(0, scheduler.CountIn(Stage.PostSimulation));
        Assert.Equal(1, scheduler.CountIn(Stage.Render));
    }

    private sealed class LambdaSystem(Action<TickContext> body) : ISystem
    {
        public void Execute(in TickContext ctx) => body(ctx);
    }
}
