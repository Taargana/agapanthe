namespace Agapanthe.Engine;

/// <summary>
/// What a simulation system receives (P3-M2, decision D1). Deliberately carries <b>no GPU type</b>: a system in
/// <see cref="Stage.Input"/>, <see cref="Stage.Simulation"/> or <see cref="Stage.PostSimulation"/> must be runnable
/// with no device, no swapchain and no window — that is the whole reason <c>World</c> is GPU-free, and a single
/// shared context carrying a <c>CommandList</c> would have thrown it away.
/// <para>
/// Since MP-0a that is enforced by the build graph rather than by discipline: the render-side context lives in
/// <c>Agapanthe.Engine.Render</c>, and this assembly references neither <c>Rendering</c> nor <c>Graphics</c>.
/// </para>
/// </summary>
public readonly struct TickContext
{
    public TickContext(float deltaSeconds, long tickIndex)
    {
        DeltaSeconds = deltaSeconds;
        TickIndex = tickIndex;
    }

    /// <summary>Seconds since the previous tick.</summary>
    public float DeltaSeconds { get; }

    /// <summary>Monotonic tick counter, from 0. Ticks are NOT frames: a frame can be skipped, a tick never is.</summary>
    public long TickIndex { get; }
}

/// <summary>
/// A simulation system: one unit of per-tick work, registered into a <see cref="Stage"/> and run by the
/// <see cref="SystemScheduler"/> in a guaranteed order.
/// </summary>
/// <remarks>
/// The virtual call is paid <b>once per system per tick</b> (a handful per frame), never per entity — the inner loops
/// stay the chunk-iterating, zero-alloc code they already are. Implementations are classes, so nothing here is the
/// generic-over-struct shape that NativeAOT struggles with.
/// <para>
/// <b><see cref="Stage.Simulation"/> may run systems in parallel (Job-1).</b> <see cref="SystemScheduler"/> groups
/// registered systems into waves — systems in the same wave declare no <see cref="Reads"/>/<see cref="Writes"/>
/// conflict and may execute on different threads concurrently; waves themselves always run one after another, in
/// order. A system with <see cref="RequiresExclusiveExecution"/> (the default) never shares a wave with anything
/// else, so an existing implementation that declares nothing stays exactly as sequential as it always was.
/// <see cref="Stage.Input"/> and <see cref="Stage.PostSimulation"/> remain fully sequential — only
/// <see cref="Stage.Simulation"/> is ever parallelized, and only for the systems that opt in.
/// </para>
/// </remarks>
public interface ISystem
{
    /// <summary>
    /// Component types this system reads. Declared for <see cref="Stage.Simulation"/>'s wave-based conflict
    /// detection (Job-1) — empty by default, meaning "declares nothing," never "touches nothing." Safe only in
    /// combination with the <see cref="RequiresExclusiveExecution"/> default of <c>true</c>.
    /// </summary>
    IReadOnlyList<Type> Reads => Array.Empty<Type>();

    /// <summary>Component types this system writes. See <see cref="Reads"/>.</summary>
    IReadOnlyList<Type> Writes => Array.Empty<Type>();

    /// <summary>
    /// When <c>true</c> (the default), this system never runs in the same <see cref="Stage.Simulation"/> wave as any
    /// other system — a coarse, safe-by-default escape hatch for shared mutable state invisible to
    /// <see cref="Reads"/>/<see cref="Writes"/> (e.g. broadphase scratch buffers). An existing, unaudited
    /// implementation must stay exactly as sequential as it is today; parallel eligibility is an explicit opt-in.
    /// <para>
    /// <b>Opting out (<c>false</c>) is not just about declaring components.</b> A world's structural command queue
    /// (backing <c>Spawn</c>/<c>Despawn</c>/<c>SetParent</c>/the deferred spawn variants) is shared, mutable state
    /// exactly like the broadphase scratch above, but it is untyped — no <see cref="Reads"/>/<see cref="Writes"/>
    /// entry can represent it. The SAME is true of <c>SimCommandQueue.Enqueue</c>/<c>DrainUpTo</c> — the same
    /// sanctioned-thread wiring that reaches <c>GameWorld</c> reaches it too (<c>SimulationHost</c> configures both
    /// together), but the queue itself is no more thread-safe for concurrent writers than the world's own queue is.
    /// A system that calls any of these from <see cref="Execute"/> must keep this <c>true</c> unless it can prove no
    /// other system in the same wave ever touches the same queue concurrently — until a future sub-milestone models
    /// these queues as declarable resources (Job-1 out-of-scope), setting it <c>false</c> anyway is a genuine,
    /// undetected data race, not merely an over-conservative choice.
    /// </para>
    /// </summary>
    bool RequiresExclusiveExecution => true;

    void Execute(in TickContext ctx);
}
