using Agapanthe.Graphics;

namespace Agapanthe.Rendering.Passes;

/// <summary>
/// Pass 2 (tonemap → swapchain, decision 3): a vertex-buffer-less fullscreen triangle that samples the HDR
/// scene target and resolves it to the sRGB swapchain (exposure + ACES + the format's sRGB OETF). No depth,
/// no cull. Set 0 = the HDR target as a combined image sampler. The set layout is owned by the
/// <see cref="Renderer"/> and borrowed here; the color format is the swapchain's.
/// </summary>
internal sealed class TonemapPass : ReloadablePass
{
    private readonly DescriptorSetLayout _setLayout;
    private readonly PixelFormat _colorFormat;

    public TonemapPass(
        GraphicsDevice device, string shaderDirectory, ShaderCompiler compiler,
        DescriptorSetLayout setLayout, PixelFormat colorFormat)
        : base(device, shaderDirectory, [("tonemap.vert", ShaderStage.Vertex), ("tonemap.frag", ShaderStage.Fragment)])
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
            // No vertex buffer: the vertex shader generates the triangle from gl_VertexIndex (Draw(3)).
            VertexLayout = null,
            SetLayouts = [_setLayout],
            // 4-byte float exposure, fragment stage only.
            PushConstants = [new PushConstantRange(0, 4, ShaderStages.Fragment)],
            // Resolves to the sRGB swapchain; the format's OETF replaces any gamma pow in the shader.
            ColorFormat = _colorFormat,
            // No depth attachment and no depth test: a fullscreen resolve. DepthFormat stays Undefined.
            DepthTest = false,
            // The triangle is a screen-space quad; face orientation is meaningless, so cull nothing.
            Cull = CullMode.None,
        });
}
