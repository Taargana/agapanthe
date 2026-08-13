using Agapanthe.Graphics;

namespace Agapanthe.Engine.Render;

/// <summary>
/// What a render system receives: the tick data plus the GPU handles for the frame being recorded. Only
/// <see cref="Stage.Render"/> systems see this — hence a separate interface, not a fatter shared context.
/// <para>
/// It lives in <c>Agapanthe.Engine.Render</c> rather than beside <see cref="TickContext"/> because it is the single
/// reason the engine used to reference <c>Agapanthe.Graphics</c> (MP-0a). Moving it here is what let the simulation
/// assembly drop that reference entirely.
/// </para>
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

/// <summary>A system in <see cref="Stage.Render"/>: the only kind that sees GPU types.</summary>
public interface IRenderSystem
{
    void Render(in RenderContext ctx);
}
