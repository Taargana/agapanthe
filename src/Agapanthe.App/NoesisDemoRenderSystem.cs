using Agapanthe.Engine.Render;
using Agapanthe.Graphics;
using Agapanthe.Rendering;

namespace Agapanthe.App;

/// <summary>
/// Composites <see cref="Texture"/> (set externally once the Key.N demo renders it) over the frame every
/// tick, after the UI overlay. Vertical-slice demo only for the Noesis Vulkan RenderDevice spike
/// (branch spike/noesis-probe) — deliberately holds a plain <see cref="GpuImage"/>, not a Noesis type,
/// so this file (and the <see cref="Renderer.DrawTexture"/> it calls) stays generic.
/// </summary>
internal sealed class NoesisDemoRenderSystem(Renderer renderer) : IRenderSystem
{
    public GpuImage? Texture { get; set; }

    public void Render(in RenderContext ctx)
    {
        if (Texture is not null)
        {
            renderer.DrawTexture(ctx.Cmd, ctx.Frame, ctx.Target, Texture);
        }
    }
}
