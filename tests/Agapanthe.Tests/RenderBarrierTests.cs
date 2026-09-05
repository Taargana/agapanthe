using Agapanthe.Engine;
using Agapanthe.Engine.Render;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0a — the structural barrier around the Render stage, and the tick/render scheduler split.
/// <para>
/// <b>These close a real hole.</b> Before this file, the Render stage was called by no test in the suite:
/// <see cref="SchedulerTests"/> covers the three tick stages and their three barriers, and stopped there. So the
/// fourth barrier — the one the render scheduler invokes after its systems — was entirely unguarded, in the very
/// milestone that moves that code into a separate type in a separate assembly.
/// </para>
/// <para>
/// The fourth barrier is <b>behaviour, not bookkeeping</b>: it is what makes a structural command enqueued during
/// the Render stage materialise at the end of that stage rather than surviving into the next frame. A headless host
/// never runs it. That asymmetry is precisely what <see cref="RenderStageNeutralityTests"/> reasons about, so it is
/// pinned rather than assumed.
/// </para>
/// <para>
/// <b>No GPU is needed to construct a <see cref="RenderContext"/>.</b> <c>default(CommandList)</c>, <c>null!</c> and
/// <c>default(SwapchainTarget)</c> suffice: a test render system never dereferences the handles, no pointer type is
/// ever named, and <c>Agapanthe.Tests</c> therefore needs no <c>AllowUnsafeBlocks</c>.
/// </para>
/// </summary>
public sealed class RenderBarrierTests
{
    private static RenderContext Context(long tickIndex = 0)
        => new(new TickContext(1f / 60f, tickIndex), default, null!, default);

    [Fact]
    public void Render_ClosesWithTheStructuralBarrier()
    {
        var log = new List<string>();
        var render = new RenderSystemScheduler(() => log.Add("|"));
        render.Add(new RecordingRenderSystem(log, "render"));

        render.Render(Context());

        // The barrier runs AFTER the render systems, exactly as it does after each tick stage.
        Assert.Equal(new[] { "render", "|" }, log);
    }

    [Fact]
    public void ADrawnFrame_RunsFourBarriers_ASkippedFrameRunsThree()
    {
        var barriers = 0;
        void Barrier() => barriers++;

        // The two halves share ONE barrier — in production both take world.FlushStructuralChanges.
        var tick = new SystemScheduler(Barrier);
        var render = new RenderSystemScheduler(Barrier);

        // A frame the renderer skips (out-of-date swapchain, minimize): Tick still runs, Render does not.
        tick.Tick(1f / 60f);
        Assert.Equal(3, barriers);

        // A drawn frame adds the Render stage and its barrier.
        barriers = 0;
        tick.Tick(1f / 60f);
        render.Render(Context(tick.TickIndex));
        Assert.Equal(4, barriers);
    }

    [Fact]
    public void AStructuralCommandFromARenderSystem_IsAppliedAtTheEndOfTheRenderStage()
    {
        var log = new List<string>();
        var render = new RenderSystemScheduler(() => log.Add("flush"));
        render.Add(new LambdaRenderSystem(() => log.Add("enqueue")));

        render.Render(Context());

        // This is the ONE place where the headless and windowed orderings genuinely differ: the command is
        // materialised within the same frame, not carried over to the next one. If the post-Render barrier is ever
        // dropped, "enqueue" would be followed by nothing and the change would linger a frame — a class of bug that
        // shows up as a one-frame lag in a client and never at all in a server.
        Assert.Equal(new[] { "enqueue", "flush" }, log);
    }

    [Fact]
    public void Render_WithNoRenderSystems_StillRunsTheBarrier()
    {
        var barriers = 0;
        var render = new RenderSystemScheduler(() => barriers++);

        // No systems, but the frame still closes.
        render.Render(Context());

        Assert.Equal(1, barriers);
    }

    [Fact]
    public void Render_RunsSystemsInRegistrationOrder()
    {
        var log = new List<string>();
        var render = new RenderSystemScheduler();
        render.Add(new RecordingRenderSystem(log, "a"));
        render.Add(new RecordingRenderSystem(log, "b"));
        render.Add(new RecordingRenderSystem(log, "c"));

        render.Render(Context());

        Assert.Equal(new[] { "a", "b", "c" }, log);
    }

    [Fact]
    public void Render_DoesNotAdvanceTheTickIndex()
    {
        var tick = new SystemScheduler();
        var render = new RenderSystemScheduler();

        tick.Tick(1f / 60f);
        render.Render(Context(tick.TickIndex));
        render.Render(Context(tick.TickIndex));

        // A tick is not a frame: the counter belongs to the simulation, and drawing twice does not simulate twice.
        Assert.Equal(1, tick.TickIndex);
    }

    [Fact]
    public void Add_AfterTheFirstRender_Throws()
    {
        var render = new RenderSystemScheduler();
        render.Render(Context());

        // Same reasoning as the tick scheduler's freeze: registering mid-run would mutate the list being iterated.
        Assert.Throws<InvalidOperationException>(() => render.Add(new RecordingRenderSystem([], "late")));
    }

    [Fact]
    public void ARenderBeforeAnyTick_NoLongerFreezesSimulationRegistration()
    {
        var tick = new SystemScheduler();
        var render = new RenderSystemScheduler();

        render.Render(Context());

        // DECLARED BEHAVIOUR CHANGE (MP-0a). Before the split, _frozen was one flag shared by both halves and
        // SystemScheduler.Render set it, so a Render with no prior Tick froze ISystem registration too. The two
        // schedulers now hold separate flags. A real host cannot reach this state — Tick always precedes DrawFrame
        // (Program.cs:764-766) — so the change is accepted rather than worked around, and pinned here so that it is
        // a decision on the record instead of a drift someone discovers later.
        tick.Add(Stage.Simulation, new LambdaSystem(_ => { }));
        Assert.Equal(1, tick.CountIn(Stage.Simulation));

        // The render side is frozen, though: its own list HAS been iterated.
        Assert.Throws<InvalidOperationException>(() => render.Add(new RecordingRenderSystem([], "late")));
    }

    [Fact]
    public void Add_AfterATickButBeforeAnyRender_StillSucceeds()
    {
        var tick = new SystemScheduler();
        var render = new RenderSystemScheduler();

        tick.Tick(1f / 60f);

        // SECOND DECLARED BEHAVIOUR CHANGE (MP-0a, found by audit). Before the split, Add(IRenderSystem) consulted
        // the SHARED _frozen flag, which Tick set — so registering a render system after the first tick threw. The
        // two schedulers now freeze independently, and the render side only freezes on its own first Render.
        // Harmless in a real host (Tick and Render are sequential on one thread, so no list is ever mutated while
        // iterated), but it is a relaxed guard and it is recorded here rather than left to be discovered.
        render.Add(new RecordingRenderSystem([], "after tick"));
        Assert.Equal(1, render.Count);
    }

    [Fact]
    public void TickScheduler_CountIn_RenderStage_Throws()
    {
        var tick = new SystemScheduler();

        // It holds no render systems at all now. Throwing beats returning 0, which would be a plausible-looking lie.
        Assert.Throws<ArgumentException>(() => tick.CountIn(Stage.Render));
    }

    [Fact]
    public void RenderScheduler_CountsItsSystems()
    {
        var render = new RenderSystemScheduler();
        Assert.Equal(0, render.Count);

        render.Add(new RecordingRenderSystem([], "a"));
        render.Add(new RecordingRenderSystem([], "b"));

        Assert.Equal(2, render.Count);
    }

    private sealed class RecordingRenderSystem(List<string> log, string name) : IRenderSystem
    {
        public void Render(in RenderContext ctx) => log.Add(name);
    }

    private sealed class LambdaRenderSystem(Action body) : IRenderSystem
    {
        public void Render(in RenderContext ctx) => body();
    }

    private sealed class LambdaSystem(Action<TickContext> body) : ISystem
    {
        public void Execute(in TickContext ctx) => body(ctx);
    }
}
