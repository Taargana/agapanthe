namespace Agapanthe.Engine;

/// <summary>
/// Runs the registered systems, stage by stage, in a guaranteed order (P3-M2, decision D1). This type is the frame
/// order — the invariant that used to live in an application's closure, where nothing protected it.
/// <para>
/// <b>Simulation and rendering are ticked separately, and that is not cosmetic.</b> The renderer skips a frame
/// whenever the swapchain is out of date (a resize, a minimize): <c>FrameRenderer.DrawFrame</c> silently returns
/// without invoking its callback. Had the stages been driven from inside that callback, every window resize would
/// have skipped the whole simulation — the delta time lost, the deferred despawns stuck in the queue. So:
/// <see cref="Tick"/> always runs (Input → Simulation → PostSimulation), and <see cref="Render"/> runs only when
/// there is a frame to draw.
/// </para>
/// <para>
/// <b>Zero-alloc.</b> One list per stage, allocated at registration; iteration by index; the structural barrier is a
/// delegate captured once at construction. Nothing here allocates per tick.
/// </para>
/// <para>
/// <b>Single-threaded.</b> The scheduler parallelises nothing — a system runs on the world's owner thread, in order.
/// A job system is a separate design; pretending to be one here would buy a data race.
/// </para>
/// </summary>
public sealed class SystemScheduler
{
    // Input / Simulation / PostSimulation. Render is separate: IRenderSystem is a DIFFERENT interface (it carries GPU
    // types), so it cannot live in the same list — the two are disjoint on purpose.
    private const int TickStageCount = 3;

    private readonly List<ISystem>[] _stages;
    private readonly List<IRenderSystem> _renderSystems = new();

    // Applies the deferred structural changes (spawns, despawns, reparenting) at the end of every stage: a system must
    // never see the entity storage move under its own iteration. Held as a field so calling it costs no allocation;
    // null in tests that have no world.
    private readonly Action? _barrier;

    private bool _frozen;
    private long _frameIndex;

    public SystemScheduler(Action? structuralBarrier = null)
    {
        _barrier = structuralBarrier;
        _stages = new List<ISystem>[TickStageCount];
        for (var i = 0; i < TickStageCount; i++)
        {
            _stages[i] = new List<ISystem>();
        }
    }

    /// <summary>Ticks run so far. A tick is not a frame: a frame can be skipped, a tick never is.</summary>
    public long FrameIndex => _frameIndex;

    /// <summary>
    /// Registers a simulation system. Systems in the same stage run in <b>registration order</b> — that is a
    /// guarantee, not an accident of the container.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="stage"/> is <see cref="Stage.Render"/> — use the
    /// <see cref="IRenderSystem"/> overload; the two are different interfaces because they receive different
    /// contexts.</exception>
    /// <exception cref="InvalidOperationException">The scheduler has already run: see <see cref="Add(Stage, ISystem)"/>
    /// remarks.</exception>
    /// <remarks>
    /// Registration is <b>frozen after the first tick</b>. Adding a system mid-run would mutate a list while it is
    /// being iterated — the class of bug one does not want to go looking for at 2 a.m. Compose the schedule up front;
    /// systems that come and go are a data problem (an enabled flag), not a scheduling one.
    /// </remarks>
    public void Add(Stage stage, ISystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        ThrowIfFrozen();
        if (stage == Stage.Render)
        {
            throw new ArgumentException(
                "Stage.Render takes an IRenderSystem (it receives a RenderContext with GPU handles), not an ISystem.",
                nameof(stage));
        }

        _stages[(int)stage].Add(system);
    }

    /// <summary>Registers a render system (<see cref="Stage.Render"/>). Runs in registration order.</summary>
    public void Add(IRenderSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        ThrowIfFrozen();
        _renderSystems.Add(system);
    }

    /// <summary>Systems registered in <paramref name="stage"/> (diagnostics and tests).</summary>
    public int CountIn(Stage stage)
        => stage == Stage.Render ? _renderSystems.Count : _stages[(int)stage].Count;

    /// <summary>
    /// Runs Input → Simulation → PostSimulation, each closed by a structural barrier. <b>Always</b> runs, even on a
    /// frame the renderer will skip — the simulation does not stop because the window is being resized.
    /// </summary>
    public void Tick(float deltaSeconds)
    {
        _frozen = true;
        var ctx = new TickContext(deltaSeconds, _frameIndex);

        for (var stage = 0; stage < TickStageCount; stage++)
        {
            var systems = _stages[stage];
            for (var i = 0; i < systems.Count; i++)
            {
                systems[i].Execute(in ctx);
            }

            _barrier?.Invoke(); // end-of-stage structural barrier
        }

        _frameIndex++;
    }

    /// <summary>
    /// Runs the <see cref="Stage.Render"/> systems for a frame that is actually being recorded. Called from inside the
    /// renderer's frame callback — and only from there, which is exactly why it is not part of <see cref="Tick"/>.
    /// </summary>
    public void Render(in RenderContext ctx)
    {
        _frozen = true;
        for (var i = 0; i < _renderSystems.Count; i++)
        {
            _renderSystems[i].Render(in ctx);
        }

        _barrier?.Invoke();
    }

    private void ThrowIfFrozen()
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                "Systems cannot be registered after the scheduler has run: it would mutate a stage's list while that " +
                "list is being iterated. Compose the schedule before the first tick.");
        }
    }
}
