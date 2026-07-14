using Agapanthe.Graphics;

namespace Agapanthe.Rendering.Passes;

/// <summary>
/// Pass 1 (scene → HDR): the forward PBR mesh pass (mesh.vert/mesh.frag), shadowed and IBL-lit. Renders into
/// the <see cref="Renderer.HdrFormat"/> target with the full 5-attribute vertex layout, set 0 (per-frame
/// camera + lights + shadow/IBL maps) and set 1 (per-material). CCW winding + back-face cull (M5 lesson).
/// The set layouts are owned by the <see cref="Renderer"/> and borrowed here.
/// </summary>
internal sealed class ScenePass : ReloadablePass
{
    private readonly DescriptorSetLayout _frameSetLayout;
    private readonly DescriptorSetLayout _materialSetLayout;
    private readonly PixelFormat _colorFormat;
    private readonly PixelFormat _depthFormat;

    public ScenePass(
        GraphicsDevice device, string shaderDirectory, ShaderCompiler compiler,
        DescriptorSetLayout frameSetLayout, DescriptorSetLayout materialSetLayout,
        PixelFormat colorFormat, PixelFormat depthFormat)
        : base(device, shaderDirectory, [("mesh.vert", ShaderStage.Vertex), ("mesh.frag", ShaderStage.Fragment)])
    {
        _frameSetLayout = frameSetLayout;
        _materialSetLayout = materialSetLayout;
        _colorFormat = colorFormat;
        _depthFormat = depthFormat;
        Build(compiler);
    }

    protected override GraphicsPipeline CreatePipeline(ShaderModule[] modules) =>
        new(Device, new GraphicsPipelineDesc
        {
            VertexShader = modules[0],
            FragmentShader = modules[1],
            // Full 5-attribute layout — mesh.vert consumes every one (see the shader header note).
            VertexLayout = Vertex.Layout,
            SetLayouts = [_frameSetLayout, _materialSetLayout],
            PushConstants =
            [
                // Model matrices now come from the per-instance SSBO (set 0, binding 6); only the fragment
                // debug-view selector remains a push constant, kept at offset 64 (P3-M1).
                new PushConstantRange(64, 4, ShaderStages.Fragment), // debug view selector
            ],
            // Scene renders into the HDR target (decision 2); the tonemap pass owns the sRGB swapchain write.
            ColorFormat = _colorFormat,
            DepthFormat = _depthFormat,
            DepthTest = true,
            // glTF winding is CCW viewed from outside; PerspectiveVulkan bakes the Y-flip so a world-CCW
            // triangle is classified CounterClockwise by Vulkan's front-face formula (M5 debugging).
            FrontFace = FrontFace.CounterClockwise,
            Cull = CullMode.Back,
        });
}
