using Agapanthe.Rendering;
using Agapanthe.Ui;

namespace Agapanthe.Engine;

/// <summary>
/// Draws the frame's UI over the finished scene (UI-1). Owns the <see cref="UiDrawList"/> that application systems
/// append to during the tick, and hands it to the renderer during Render.
/// <para>
/// <b>Why the list lives here.</b> Application logic runs in the tick stages (Input, Simulation, PostSimulation),
/// where no command list exists; the command list only exists in Render. Buffering plain structs between the two is
/// what lets gameplay code say "draw this text" without ever touching a GPU type.
/// </para>
/// <para>
/// Register it <b>after</b> <see cref="FrameOrchestrator.CreateDefault"/> so it runs after the scene view system,
/// i.e. after the tonemap has resolved the frame — the UI is an overlay on the finished image. The scheduler freezes
/// its order at the first tick, so registration must happen before the first frame.
/// </para>
/// </summary>
public sealed class UiRenderSystem : ISystem, IRenderSystem
{
    private readonly Renderer _renderer;

    public UiRenderSystem(Renderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _renderer = renderer;
    }

    /// <summary>
    /// The list to append this frame's quads to. Cleared at the start of every tick, so callers always draw into a
    /// fresh list and never have to remember to clear it themselves.
    /// </summary>
    public UiDrawList DrawList { get; } = new();

    /// <summary>
    /// Clears last frame's quads. Registered in <see cref="Stage.Input"/> — the FIRST tick stage — so every later
    /// stage in the same frame appends to an empty list, and whatever they add survives until Render.
    /// </summary>
    public void Execute(in TickContext ctx) => DrawList.Clear();

    /// <summary>Submits the accumulated quads. A no-op when nothing was drawn or no font is loaded.</summary>
    public void Render(in RenderContext ctx)
        => _renderer.DrawUi(ctx.Cmd, ctx.Frame, ctx.Target, DrawList.Quads);
}
