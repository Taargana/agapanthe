using Agapanthe.Engine.Render;
using Agapanthe.Rendering;

namespace Agapanthe.App;

/// <summary>
/// Composites the game's <see cref="IUiHost"/> (if any) over the frame every tick, after the UI overlay.
/// Deliberately generic — holds only <see cref="IUiHost"/>, never a concrete UI engine type, so this file (and the
/// <see cref="Renderer.DrawTexture"/> it calls) never needs to change when the UI engine behind <see cref="IGame"/>
/// changes.
/// </summary>
internal sealed class UiHostRenderSystem(Renderer renderer, IUiHost? host) : IRenderSystem
{
    public void Render(in RenderContext ctx)
    {
        if (host?.CurrentFrame is { } frame)
        {
            renderer.DrawTexture(ctx.Cmd, ctx.Frame, ctx.Target, frame);
        }
    }
}
