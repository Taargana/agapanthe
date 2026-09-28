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
/// else, so an existing implementation that declares nothing stays exactly as sequential as it always was. A
/// system that opts OUT of exclusivity may additionally declare <see cref="RequiresOwnerThread"/> (Job-2b) to keep
/// its own execution pinned to the owner thread while sharing a wave with systems that run on workers.
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
    /// <see cref="Reads"/>/<see cref="Writes"/>. An existing, unaudited implementation must stay exactly as
    /// sequential as it is today; parallel eligibility is an explicit opt-in.
    /// <para>
    /// <b>Opting out (<c>false</c>) is not just about declaring components.</b> A world's structural command queue
    /// (backing <c>Spawn</c>/<c>Despawn</c>/<c>SetParent</c>/the deferred spawn variants) is shared, mutable state,
    /// but it is untyped — no <see cref="Reads"/>/<see cref="Writes"/> entry can represent it. The SAME is true of
    /// <c>SimCommandQueue.Enqueue</c>/<c>DrainUpTo</c> — the same sanctioned-thread wiring that reaches
    /// <c>GameWorld</c> reaches it too (<c>SimulationHost</c> configures both together), but the queue itself is not
    /// made safe for concurrent writers by being sanctioned (Job-2 added a runtime guard that DETECTS and throws on
    /// a genuinely overlapping cross-thread call — loud, not silent — but it is a detector, not a scheduling
    /// mechanism: it does not make concurrent access correct, only diagnosable). A system that calls any of these
    /// from <see cref="Execute"/> must keep this <c>true</c> unless it can prove no other system in the same wave
    /// ever touches the same queue concurrently — until a future sub-milestone models these queues as declarable
    /// resources (Job-1 out-of-scope), setting it <c>false</c> anyway risks a data race that Job-2's guard would at
    /// best turn into a thrown exception, not a correct outcome. <b>Declaring <see cref="RequiresOwnerThread"/>
    /// instead does NOT relax this</b> — see that member's remarks for exactly why (Job-2b audit finding).
    /// </para>
    /// <para>
    /// A system whose <see cref="Execute"/> needs <c>GameWorld</c>'s owner-thread-strict surface (physics/query
    /// broadphase scratch, e.g. <c>PhysicsSystem</c>) no longer strictly needs this to stay <c>true</c> — see
    /// <see cref="RequiresOwnerThread"/>, the orthogonal thread-affinity axis Job-2b added specifically for that
    /// case, which keeps a system pinned to the owner thread without monopolizing its whole wave.
    /// </para>
    /// </summary>
    bool RequiresExclusiveExecution => true;

    /// <summary>
    /// When <c>true</c>, this system's <see cref="Execute"/> always runs on the scheduler's OWNER thread — never a
    /// Job-1 worker — even when it shares a <see cref="Stage.Simulation"/> wave with other systems (i.e. when
    /// <see cref="RequiresExclusiveExecution"/> is <c>false</c>). Orthogonal to <see cref="RequiresExclusiveExecution"/>
    /// (Job-2b): that axis is RESOURCE exclusivity (nobody else runs concurrently at all); this one is THREAD
    /// affinity (this system specifically must run here, others may still run elsewhere in the same wave). Default
    /// <c>false</c> — combined with <see cref="RequiresExclusiveExecution"/>'s own default of <c>true</c>, an
    /// existing implementation that declares nothing is completely unaffected: it still always executes inline on
    /// the owner thread, in its own solo wave, exactly as before this member existed.
    /// <para>
    /// Meaningless (harmlessly redundant) when <see cref="RequiresExclusiveExecution"/> is <c>true</c> — a solo
    /// wave already always executes inline on the owner thread. This flag only changes anything once a system ALSO
    /// opts out of exclusivity: it says "I may share a wave, but only ever run me on the thread that called
    /// <see cref="SystemScheduler.Tick"/>, never a worker."
    /// </para>
    /// <para>
    /// <b>Pinning does NOT make the structural command queue (or any other <c>AssertOwnerThreadStrict</c>-guarded
    /// state) safe to touch from a system that shares a wave with a worker (Job-2b audit finding, reproduced
    /// empirically).</b> Before Job-2b, the owner thread only ever blocked in
    /// <c>SystemScheduler.WaitForWaveOrDiagnoseHang</c> while workers ran — it never mutated anything concurrently.
    /// This member breaks that: the owner now runs a pinned system's <see cref="Execute"/> WHILE workers from the
    /// SAME wave are running. If that pinned system called <c>Spawn</c>/<c>Despawn</c>/<c>SetParent</c> (or a
    /// deferred variant), it would race any worker in the same wave calling <c>GameWorld</c>'s lenient,
    /// sanctioned-worker-tolerant surface (today: <c>IsAlive</c>/<c>GetGlobalId</c>) — both read/write the same
    /// <c>_pendingSpawn</c>/<c>_pendingDead</c>/<c>_live</c> state with no synchronization. <c>PhysicsSystem</c> is
    /// the only system that sets this today, and it is safe only because <c>StepPhysics</c> never touches those
    /// three fields — NOT because pinning itself makes them safe. A future pinned, non-exclusive system MUST NOT
    /// mutate anything <c>GameWorld.AssertOwnerThread</c>'s lenient surface can read, until a real runtime guard
    /// exists for this (backlog: Job-3 candidate).
    /// </para>
    /// </summary>
    bool RequiresOwnerThread => false;

    void Execute(in TickContext ctx);
}
