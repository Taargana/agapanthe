using Agapanthe.Graphics;

namespace Agapanthe.Engine;

/// <summary>
/// What a simulation system receives (P3-M2, decision D1). Deliberately carries <b>no GPU type</b>: a system in
/// <see cref="Stage.Input"/>, <see cref="Stage.Simulation"/> or <see cref="Stage.PostSimulation"/> must be runnable
/// with no device, no swapchain and no window — that is the whole reason <c>World</c> is GPU-free, and a single
/// shared context carrying a <c>CommandList</c> would have thrown it away.
/// </summary>
public readonly struct TickContext
{
    public TickContext(float deltaSeconds, long frameIndex)
    {
        DeltaSeconds = deltaSeconds;
        FrameIndex = frameIndex;
    }

    /// <summary>Seconds since the previous tick.</summary>
    public float DeltaSeconds { get; }

    /// <summary>Monotonic tick counter, from 0. Ticks are NOT frames: a frame can be skipped, a tick never is.</summary>
    public long FrameIndex { get; }
}

/// <summary>
/// What a render system receives: the tick data plus the GPU handles for the frame being recorded. Only
/// <see cref="Stage.Render"/> systems see this — hence a separate interface, not a fatter shared context.
/// </summary>
/// <remarks>
/// <see cref="Frame"/> is <c>Agapanthe.Graphics.FrameContext</c>, the per-frame-in-flight descriptor/UBO slot. This
/// type is called <c>RenderContext</c> and not <c>FrameContext</c> on purpose: the name is taken, and two
/// <c>FrameContext</c> in one call chain is how a reader loses an afternoon.
/// </remarks>
public readonly struct RenderContext
{
    public RenderContext(in TickContext tick, CommandList cmd, FrameContext frame, SwapchainTarget target)
    {
        Tick = tick;
        Cmd = cmd;
        Frame = frame;
        Target = target;
    }

    public TickContext Tick { get; }

    /// <summary>The command list being recorded for this frame.</summary>
    public CommandList Cmd { get; }

    /// <summary>The frame-in-flight slot: per-frame descriptor sets and mapped uniform buffers.</summary>
    public FrameContext Frame { get; }

    /// <summary>The swapchain image this frame draws into.</summary>
    public SwapchainTarget Target { get; }
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
/// A system runs on the world's owner thread. The scheduler parallelises <b>nothing</b>: a job system is a separate
/// question, and pretending otherwise here would be a lie that costs a data race.
/// </para>
/// </remarks>
public interface ISystem
{
    void Execute(in TickContext ctx);
}

/// <summary>A system in <see cref="Stage.Render"/>: the only kind that sees GPU types.</summary>
public interface IRenderSystem
{
    void Render(in RenderContext ctx);
}
