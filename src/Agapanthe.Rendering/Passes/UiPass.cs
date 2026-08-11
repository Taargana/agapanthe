using Agapanthe.Graphics;

namespace Agapanthe.Rendering.Passes;

/// <summary>
/// The UI overlay pass (UI-1): draws the frame's quads — glyphs and solid fills alike — on top of the already
/// tonemapped swapchain image.
/// <para>
/// Like <see cref="TonemapPass"/> it declares <b>no vertex layout</b>: the vertex shader expands a storage buffer of
/// quads from <c>gl_VertexIndex</c>, so one <c>Draw(quadCount * 6)</c> covers the whole UI. Unlike every pass before
/// it, it enables <b>premultiplied-alpha blending</b> — the reason <see cref="BlendMode"/> exists at all.
/// </para>
/// <para>
/// Set 0 is a single <b>per-frame</b> set: binding 0 = the font atlas as a combined image sampler, binding 1 = the
/// frame's quad buffer. A descriptor set is allocated from one pool, so a persistent binding and a per-frame binding
/// cannot share a set; the atlas handles are stable anyway, so only the (cheap) descriptor write repeats.
/// </para>
/// </summary>
internal sealed class UiPass : ReloadablePass
{
    private readonly DescriptorSetLayout _setLayout;
    private readonly PixelFormat _colorFormat;

    public UiPass(
        GraphicsDevice device, string shaderDirectory, ShaderCompiler compiler,
        DescriptorSetLayout setLayout, PixelFormat colorFormat)
        : base(device, shaderDirectory, [("ui.vert", ShaderStage.Vertex), ("ui.frag", ShaderStage.Fragment)])
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
            // No vertex buffer: six vertices per quad are generated from gl_VertexIndex (see ui.vert).
            VertexLayout = null,
            SetLayouts = [_setLayout],
            // vec2 invScreenSize + float sdfPixelRange, read by BOTH stages (the vertex shader needs the screen
            // size, the fragment shader the SDF range), so the range covers vertex|fragment.
            PushConstants = [new PushConstantRange(0, 12, ShaderStages.Vertex | ShaderStages.Fragment)],
            ColorFormat = _colorFormat,
            // Overlay: no depth attachment, no depth test, and submission order IS draw order (painter's algorithm).
            DepthTest = false,
            Cull = CullMode.None,
            // The shader outputs linear RGB already multiplied by alpha, which is what keeps a bilinearly filtered
            // glyph edge from picking up a halo.
            Blend = BlendMode.PremultipliedAlpha,
        });
}
