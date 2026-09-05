using System.Diagnostics;
using Agapanthe.World;

namespace Agapanthe.Engine;

/// <summary>
/// The simulation, and nothing else (MP-0a): a world, a schedule of tick systems, and the frame's self-measurement.
/// <b>It names no rendering type</b>, which is the whole point — this is what a dedicated server runs.
/// <para>
/// Extracted from <c>FrameOrchestrator</c>, which keeps the render half and composes one of these. The engine cap
/// (<c>backlog §4quater</c>) rests on "the same simulation code runs everywhere, only authority changes"; that claim
/// was false in the build graph while the only way to tick a world was through a type that required a
/// <c>Renderer</c>, a <c>ResourceRegistry</c> and a <c>Camera</c>.
/// </para>
/// <para>
/// <b>It owns nothing.</b> The <see cref="GameWorld"/> is borrowed; its lifetime — and the 0-leak teardown order —
/// stays with the application, exactly as it did with the orchestrator.
/// </para>
/// <para>
/// <b>Single-threaded.</b> The scheduler parallelises nothing, and the allocation bracket below depends on it.
/// </para>
/// </summary>
public sealed class SimulationHost
{
    private readonly SystemScheduler _scheduler;

    // Frame self-measurement (UI-2): opened in Tick, closed in EndFrame.
    private long _frameAllocStart;
    private long _frameTimestampStart;
    private int _bracketThreadId;
    private bool _measurementOpen;
    private float _dt;

    // Ticks since the current frame's BeginFrame; latched into LastFrameTickCount by EndFrame (MP-0c, audit arch F3),
    // so LastFrameTickCount always describes the last COMPLETE frame — same lifecycle as LastFrameMs.
    private int _frameTickCount;

    private SimulationHost(GameWorld world)
    {
        // The structural barrier the scheduler runs at the end of every stage IS the world's deferred-change flush
        // (P3-M2 D2): a system enqueues spawns/despawns, the barrier applies them before the next stage iterates.
        _scheduler = new SystemScheduler(world.FlushStructuralChanges);
    }

    /// <summary>
    /// Builds a host with the engine's default simulation systems registered: PostSimulation propagates transforms.
    /// The application adds its own with <see cref="Add"/> BEFORE the first <see cref="Tick"/>.
    /// </summary>
    public static SimulationHost CreateDefault(GameWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var host = new SimulationHost(world);
        host._scheduler.Add(Stage.PostSimulation, new PropagateSystem(world));
        return host;
    }

    /// <summary>Registers a simulation system (Input / Simulation / PostSimulation). See
    /// <see cref="SystemScheduler.Add(Stage, ISystem)"/>: registration order is execution order, frozen at first tick.</summary>
    public void Add(Stage stage, ISystem system) => _scheduler.Add(stage, system);

    /// <summary>Monotonic tick counter (see <see cref="SystemScheduler.TickIndex"/>).</summary>
    public long TickIndex => _scheduler.TickIndex;

    /// <summary>
    /// How many <see cref="Tick"/> calls ran during the last COMPLETE frame — latched by <see cref="EndFrame"/>,
    /// same lifecycle as <see cref="LastFrameMs"/> (audit arch F3). 1 in steady state and in capture mode;
    /// <c>&gt; 1</c> means the frame fell behind and the fixed-step accumulator is catching up; 0 for a frame faster
    /// than one fixed step (the nominal case above the tick rate — the render pass then repeats the previous tick).
    /// The debug overlay is not wired to it (its constructor takes a <c>FrameStats</c>, not this host — deferred to
    /// UI-3), but the Sandbox bench line logs it so a catch-up burst is visible rather than silent (MP-0c F8).
    /// </summary>
    public int LastFrameTickCount { get; private set; }

    /// <summary>
    /// The tick data a render pass should be given for the frame just simulated.
    /// <para>
    /// <b>Its <see cref="TickContext.TickIndex"/> is the LAST tick actually executed</b> (MP-0c). Before this,
    /// <see cref="SystemScheduler.Tick"/> advanced the counter after the stages and this property read it raw, so a
    /// render system saw <c>N+1</c> where the tick systems of the same frame saw <c>N</c> — an off-by-one MP-0a
    /// deliberately preserved and reserved for the time-authority sub-milestone to settle. It is settled here:
    /// <c>Math.Max(0L, TickIndex - 1)</c> reports the index of the tick whose results the render pass is about to
    /// draw, and the clamp covers the boundary before any tick has run (both readings collapse to <c>0</c> there —
    /// disambiguating them is deferred to MP-0d, which will timestamp commands against this counter). Under the
    /// fixed-step accumulator N ticks may run in one frame; this is then the index of the Nth. <b>It is no longer
    /// strictly increasing per frame</b> (audit arch F5): a frame faster than one fixed step — the nominal case
    /// above the tick rate — runs zero ticks, and <see cref="CurrentTick"/> then repeats the previous index with the
    /// previous <c>_dt</c>. Pinned by <c>HeadlessSimulationTests.CurrentTick_ReportsTheLastExecutedTick</c>.
    /// </para>
    /// </summary>
    public TickContext CurrentTick => new(_dt, Math.Max(0L, _scheduler.TickIndex - 1));

    /// <summary>
    /// Runs Input → Simulation → PostSimulation for one frame, each stage closed by the structural barrier.
    /// <b>Always</b> call it, including on a frame the renderer will skip: the simulation does not stop because a
    /// window is being resized (D1.a).
    /// </summary>
    public void Tick(float deltaSeconds)
    {
        _dt = deltaSeconds;
        _scheduler.Tick(deltaSeconds);
        _frameTickCount++;
    }

    /// <summary>
    /// Opens the frame's self-measurement (UI-2). Call it once per frame, before the first <see cref="Tick"/>;
    /// <see cref="EndFrame"/> closes it.
    /// <para>
    /// <b>Separate from <see cref="Tick"/> on purpose, and this is not speculative generality.</b> The bracket used
    /// to open inside <c>Tick</c>, which is exact only while one tick equals one frame. The time-authority
    /// sub-milestone introduces a fixed-step accumulator — N ticks per wall-clock frame — and under it the old
    /// shape would have filed <b>N samples per frame</b> into <see cref="Stats"/>: the belt-and-braces close would
    /// fire on ticks 2..N, each recording a bare tick rather than a frame. The frame-time graph would mix two
    /// populations and, worse, the continuously-displayed 0-alloc gate that UI-2 exists to provide would stop
    /// measuring a frame at all. UI-2 already spent an iteration on exactly this ("the measurement window, not the
    /// counter"); splitting the call now costs nothing and keeps that lesson from being re-learnt.
    /// </para>
    /// </summary>
    public void BeginFrame()
    {
        // Belt and braces: if the previous frame never called EndFrame, close it here rather than silently dropping
        // its cost. Otherwise a caller who forgets the call gets a profiler frozen on one stale sample.
        if (_measurementOpen)
        {
            EndFrame();
        }

        // Both reads are allocation-free.
        _frameAllocStart = GC.GetAllocatedBytesForCurrentThread();
        _frameTimestampStart = Stopwatch.GetTimestamp();
        _bracketThreadId = Environment.CurrentManagedThreadId;
        _measurementOpen = true;
        _frameTickCount = 0;
    }

    /// <summary>
    /// Closes the frame's self-measurement and files it into <see cref="Stats"/>. Call it once per frame, after the
    /// rendering (if any) has been submitted.
    /// <para>
    /// <b>Why after, and not at the end of a render callback.</b> That callback runs INSIDE command-buffer
    /// recording, so closing there would exclude <c>vkEndCommandBuffer</c>, the submit and the present — a blind
    /// spot on exactly the layer where a per-frame allocation is most likely to hide, in the readout whose entire
    /// job is to catch one. Closing here reproduces the bracket the cull-stats bench has always used, and it still
    /// excludes the host's windowing and input pump, which allocates and which the engine does not control.
    /// </para>
    /// <para>
    /// It also covers the frames where the renderer bails out early (out-of-date swapchain, failed acquire) and
    /// never invokes its callback at all: those are precisely the frames that recreate the swapchain, the most
    /// allocating path in the engine.
    /// </para>
    /// </summary>
    public void EndFrame()
    {
        if (!_measurementOpen)
        {
            return;
        }

        _measurementOpen = false;

        // GetAllocatedBytesForCurrentThread is a PER-THREAD counter: opening and closing the bracket on two
        // different threads yields an arbitrary delta, and a negative one reads as a perfectly healthy empty graph.
        // The engine is single-threaded today; the time-authority sub-milestone (tick decoupled from the frame) is
        // what could break this, so the invariant is anchored now while it costs nothing.
        Debug.Assert(
            Environment.CurrentManagedThreadId == _bracketThreadId,
            "Frame measurement must open and close on the same thread — the allocation counter is per-thread.");

        LastFrameAllocatedBytes = Math.Max(0L, GC.GetAllocatedBytesForCurrentThread() - _frameAllocStart);
        LastFrameMs = (float)Stopwatch.GetElapsedTime(_frameTimestampStart).TotalMilliseconds;
        LastFrameTickCount = _frameTickCount;
        Stats.Record(LastFrameMs, LastFrameAllocatedBytes);
    }

    /// <summary>
    /// The engine's own frame metrics, recorded every frame whether or not anything displays them.
    /// <para>
    /// Owned HERE rather than by the debug overlay: metrics are an engine concern, and hanging them off the overlay
    /// made them depend on a cooked font being present on disk — no font, no measurements at all.
    /// </para>
    /// </summary>
    public FrameStats Stats { get; } = new();

    /// <summary>
    /// Managed bytes the ENGINE allocated during the last completed frame — tick plus everything up to
    /// <see cref="EndFrame"/>.
    /// </summary>
    public long LastFrameAllocatedBytes { get; private set; }

    /// <summary>
    /// Wall-clock duration of the last completed frame, same bracket as <see cref="LastFrameAllocatedBytes"/>.
    /// <b>Not a pure CPU cost</b>: in a windowed host the bracket spans the fence wait and the present, so under
    /// vsync this tracks the display period — which is what makes it the right number for an fps readout, and the
    /// wrong one for attributing a spike to engine code.
    /// </summary>
    public float LastFrameMs { get; private set; }

    // Recompute every hierarchical entity's world transform from its Parent chain. Pure simulation: a headless
    // server needs correct world positions as much as a client does.
    private sealed class PropagateSystem(GameWorld world) : ISystem
    {
        public void Execute(in TickContext ctx) => world.PropagateTransforms();
    }
}
