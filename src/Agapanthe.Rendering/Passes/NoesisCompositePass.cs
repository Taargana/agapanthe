using Agapanthe.Graphics;

namespace Agapanthe.Rendering.Passes;

/// <summary>
/// Composites an arbitrary color texture over the finished frame (vertical slice demo for the Noesis
/// Vulkan RenderDevice spike, branch spike/noesis-probe). Deliberately generic — this project does not
/// reference <c>Agapanthe.Ui.Noesis</c> or any Noesis type; it only knows "sample this texture, blend it
/// on top", the same way <see cref="UiPass"/> knows "draw these quads" without knowing what drew them.
/// <para>
/// Fullscreen triangle, no vertex buffer — same technique as <see cref="TonemapPass"/>; premultiplied
/// blend — same technique as <see cref="UiPass"/>.
/// </para>
/// </summary>
internal sealed class NoesisCompositePass : ReloadablePass
{
    private readonly DescriptorSetLayout _setLayout;
    private readonly PixelFormat _colorFormat;

    public NoesisCompositePass(
        GraphicsDevice device, string shaderDirectory, ShaderCompiler compiler,
        DescriptorSetLayout setLayout, PixelFormat colorFormat)
        : base(device, shaderDirectory, [("noesis_composite.vert", ShaderStage.Vertex), ("noesis_composite.frag", ShaderStage.Fragment)])
    {
        _setLayout = setLayout;
        _colorFormat = colorFormat;
        Build(compiler);
    }

    protected override GraphicsPipeline CreatePipeline(ShaderModule[] modules) =>
        new(Device, new GraphicsPipelineDesc
        {
            VertexShader = modules[0],
            FragmentShader = modules[1],
            VertexLayout = null,
            SetLayouts = [_setLayout],
            ColorFormat = _colorFormat,
            DepthTest = false,
            Cull = CullMode.None,
            Blend = BlendMode.PremultipliedAlpha,
        });
}
