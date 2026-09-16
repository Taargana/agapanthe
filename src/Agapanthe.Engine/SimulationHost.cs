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
/// <b><see cref="Stage.Simulation"/> may run systems in parallel worker threads (Job-1).</b> The allocation
/// bracket below (<c>BeginFrame</c>/<c>EndFrame</c>) measures only the OWNER thread's
/// <c>GC.GetAllocatedBytesForCurrentThread()</c> — a per-thread counter — so an allocation made by a system
/// running on a worker thread is invisible to <see cref="LastFrameAllocatedBytes"/> and to whatever reads it
/// (<c>FrameStats</c>, the debug overlay). Harmless today: the only real <see cref="Stage.Simulation"/> system
/// anywhere in this codebase (<c>PhysicsSystem</c>) is <c>RequiresExclusiveExecution == true</c>, so no worker
/// pool is ever created in production and the owner thread still does 100% of the work. This becomes a real blind
/// spot the day a second, non-exclusive <see cref="Stage.Simulation"/> system exists — closing it (measuring and
/// folding in worker-thread allocations) is deferred, tracked debt, not silently dropped.
/// </para>
/// </summary>
public sealed class SimulationHost : IDisposable
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

    private readonly SimCommandHandler _discard;

    private SimulationHost(GameWorld world, SimulationSettings settings)
    {
        // The structural barrier the scheduler runs at the end of every stage IS the world's deferred-change flush
        // (P3-M2 D2): a system enqueues spawns/despawns, the barrier applies them before the next stage iterates.
        // The sanctioned-thread callback (Job-1 D2) fires at most once, only if Stage.Simulation ever needs a real
        // worker pool — it wires the SAME thread ids into both the world and the command queue, so a system running
        // on a worker thread can touch either without tripping their Debug-only owner-thread guards.
        _scheduler = new SystemScheduler(world.FlushStructuralChanges, ids =>
        {
            world.SetSanctionedWorkerThreads(ids);
            Commands.SetSanctionedWorkerThreads(ids);
        });
        _discard = Discard; // cached once — `ApplyCommand ?? _discard` then allocates nothing per tick
        Settings = settings;
    }

    /// <summary>
    /// Builds a host with the engine's default simulation systems registered: PostSimulation propagates transforms.
    /// The application adds its own with <see cref="Add"/> BEFORE the first <see cref="Tick"/>.
    /// </summary>
    public static SimulationHost CreateDefault(GameWorld world)
        => CreateDefault(world, SimulationSettings.Default);

    /// <summary>
    /// Builds a default host bound to explicit <paramref name="settings"/> — the composition root (the
    /// <c>Agapanthe.App</c> milestone) that owns the fixed step passes it here so
    /// <see cref="FrameOrchestrator"/> and the application's physics both read one value.
    /// </summary>
    public static SimulationHost CreateDefault(GameWorld world, SimulationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(settings);

        var host = new SimulationHost(world, settings);
        host._scheduler.Add(Stage.PostSimulation, new PropagateSystem(world));
        return host;
    }

    /// <summary>
    /// Shuts down the scheduler's Job-1 worker pool, if one was ever created (a no-op otherwise — see
    /// <see cref="SystemScheduler.Dispose"/>). Does NOT dispose the borrowed <see cref="GameWorld"/> — this host
    /// never owned it. A long-lived production host (the application's whole run) has no strict need to call this;
    /// it exists so a short-lived host (a unit test) does not leak a worker pool past its own lifetime.
    /// </summary>
    public void Dispose() => _scheduler.Dispose();

    /// <summary>
    /// The simulation's protocol constants — today the fixed step (<see cref="SimulationSettings"/>). The single
    /// definition: <c>FrameOrchestrator</c>'s accumulator and the application's <c>PhysicsSettings</c> both derive
    /// from it.
    /// </summary>
    public SimulationSettings Settings { get; }

    /// <summary>Registers a simulation system (Input / Simulation / PostSimulation). See
    /// <see cref="SystemScheduler.Add(Stage, ISystem)"/>: registration order is execution order, frozen at first tick.</summary>
    public void Add(Stage stage, ISystem system) => _scheduler.Add(stage, system);

    // ── MP-0d: input → timestamped commands ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The application's per-tick input source. Invoked once at the top of every <see cref="Tick"/> (so a catch-up
    /// frame running N ticks samples input N times — the callback must serve one edge per physical press across
    /// those calls, see <see cref="InputSnapshot"/>). Null (the default) skips the whole input phase — the engine
    /// adds no input system, <see cref="Stage.Input"/> stays the application's.
    /// </summary>
    public Func<InputSnapshot>? SampleInput { get; set; }

    /// <summary>The declarative input→command map applied to each <see cref="SampleInput"/> result. Null skips the
    /// map (the application may still <see cref="SimCommandQueue.Enqueue"/> onto <see cref="Commands"/> directly —
    /// e.g. a discrete key that needs client context the map cannot supply).</summary>
    public InputMap? InputMap { get; set; }

    /// <summary>
    /// The application's command apply-point: it validates (budget, target liveness) then executes. The
    /// server-authoritative split — the client emits intent, this decides whether to honour it. When null, a
    /// drained command increments <see cref="DiscardedCommandCount"/> and is dropped.
    /// <para>
    /// <b>Assign it (<c>=</c>), never <c>+=</c>.</b> The authority point is single: two handlers each "validating"
    /// the same command is a contradiction. The type is a delegate only because it is the natural shape.
    /// </para>
    /// </summary>
    public SimCommandHandler? ApplyCommand { get; set; }

    /// <summary>
    /// The command buffer drained each tick at <see cref="TickIndex"/>. Composed by the host (it owns nothing else
    /// — this is the one exception, because a dedicated server binds its network receive path here). The
    /// application may enqueue directly for discrete, context-carrying commands.
    /// </summary>
    public SimCommandQueue Commands { get; } = new();

    /// <summary>The input sampled at the top of the current/last <see cref="Tick"/> — diagnostic; a
    /// <see cref="Stage.Input"/> system may read it.</summary>
    public InputSnapshot CurrentInput { get; private set; }

    /// <summary>
    /// How many commands were drained with no <see cref="ApplyCommand"/> set and therefore discarded — a
    /// Release-readable signal, in the shape MP-0c chose for <c>FixedTimestepAccumulator.SanitisedInputCount</c>
    /// (a counter any build / test / telemetry can read beats a Debug-only assert). Non-zero means input is being
    /// produced but nothing consumes it — on a dedicated server, every peer's input dropped.
    /// </summary>
    public long DiscardedCommandCount { get; private set; }

    private void Discard(in SimCommand command) => DiscardedCommandCount++;

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
    /// Runs one tick: <b>MP-0d input phase (sample → translate → drain commands) → <see cref="Stage.Input"/> →
    /// <see cref="Stage.Simulation"/> → <see cref="Stage.PostSimulation"/></b>, each stage closed by the structural
    /// barrier. The input phase is before <see cref="Stage.Input"/> on purpose: a command handler mutates
    /// components (and may spawn) while no query iterates, and a <see cref="Stage.Input"/> system can then read the
    /// result via <see cref="CurrentInput"/>.
    /// <para>
    /// <b>Always</b> call it, including on a frame the renderer will skip: the simulation does not stop because a
    /// window is being resized (D1.a).
    /// </para>
    /// </summary>
    public void Tick(float deltaSeconds)
    {
        // MP-0d: sample input, translate it to commands, and drain everything due for the tick ABOUT to run
        // (_scheduler.TickIndex, incremented after the stages). The drain is before any stage, so ApplyCommand
        // mutates components while no query iterates and Stage.Simulation sees the change the same tick.
        if (SampleInput is not null)
        {
            CurrentInput = SampleInput();
            if (InputMap is not null)
            {
                InputTranslation.Emit(CurrentInput, InputMap, Commands, _scheduler.TickIndex);
            }
        }

        Commands.DrainUpTo(_scheduler.TickIndex, ApplyCommand ?? _discard);

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
