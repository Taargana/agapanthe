namespace Agapanthe.Engine.Render;

/// <summary>
/// Runs the <see cref="Stage.Render"/> systems for a frame that is actually being recorded, then closes the stage
/// with the world's structural barrier. Split out of <see cref="SystemScheduler"/> by MP-0a, because it is the half
/// that names GPU types.
/// <para>
/// <b>The closing barrier is behaviour, not bookkeeping.</b> A structural command enqueued by a render system is
/// materialised at the end of THIS stage rather than surviving into the next frame. A headless host never runs it —
/// which is precisely the asymmetry <c>RenderStageNeutralityTests</c> exists to police, and
/// <c>RenderBarrierTests</c> to pin.
/// </para>
/// <para>
/// Called from inside the renderer's frame callback, and only from there — which is exactly why it is not part of
/// <see cref="SystemScheduler.Tick"/>: <c>FrameRenderer.DrawFrame</c> silently skips its callback when the swapchain
/// is out of date, and the simulation must not skip with it.
/// </para>
/// </summary>
public sealed class RenderSystemScheduler
{
    private readonly List<IRenderSystem> _systems = new();

    // The same delegate the tick scheduler closes its stages with: the world's deferred-change flush. Held as a
    // field so invoking it costs no allocation; null in tests that have no world.
    private readonly Action? _barrier;

    private bool _frozen;

    public RenderSystemScheduler(Action? structuralBarrier = null) => _barrier = structuralBarrier;

    /// <summary>Render systems registered (diagnostics and tests).</summary>
    public int Count => _systems.Count;

    /// <summary>
    /// Registers a render system. Systems run in <b>registration order</b> — a guarantee, not an accident of the
    /// container.
    /// </summary>
    /// <exception cref="InvalidOperationException">This scheduler has already rendered: adding to a list mid-run
    /// would mutate it while it is being iterated. Compose the schedule before the first frame.</exception>
    public void Add(IRenderSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (_frozen)
        {
            throw new InvalidOperationException(
                "Render systems cannot be registered after the first Render: it would mutate the list while that "
                + "list is being iterated. Compose the schedule before the first frame.");
        }

        _systems.Add(system);
    }

    /// <summary>Runs the render systems in registration order, then the structural barrier.</summary>
    public void Render(in RenderContext ctx)
    {
        _frozen = true;
        for (var i = 0; i < _systems.Count; i++)
        {
            _systems[i].Render(in ctx);
        }

        _barrier?.Invoke();
    }
}
