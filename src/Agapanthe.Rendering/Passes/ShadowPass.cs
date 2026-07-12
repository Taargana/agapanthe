using Agapanthe.Graphics;

namespace Agapanthe.Rendering.Passes;

/// <summary>
/// Pass 0 (M6, decision 7): the directional shadow depth pass. Depth-only (no fragment shader, no color
/// attachment), no descriptor set — <c>lightViewProj</c> (offset 0) and each mesh's world transform (offset
/// 64) travel as 128 bytes of push constants. Slope-scaled depth bias fights acne. The pipeline is invariant
/// to swapchain resize; the shadow map image itself stays owned by the <see cref="Renderer"/>.
/// </summary>
internal sealed class ShadowPass : ReloadablePass
{
    // Position-only view over the mesh vertex layout: keeps the full 60-byte stride (the mesh vertex buffers
    // are bound unchanged) but declares ONLY location 0, so shadow.vert consumes every declared attribute —
    // no "attribute not consumed by vertex shader" validation warning.
    private static readonly VertexLayout ShadowVertexLayout = new(
        stride: 60,
        attributes: [new VertexAttribute(Location: 0, Offset: 0, PixelFormat.R32G32B32Sfloat)]);

    private readonly PixelFormat _depthFormat;
    private readonly float _depthBiasConstant;
    private readonly float _depthBiasSlope;

    public ShadowPass(
        GraphicsDevice device, string shaderDirectory, ShaderCompiler compiler,
        PixelFormat depthFormat, float depthBiasConstant, float depthBiasSlope)
        : base(device, shaderDirectory, [("shadow.vert", ShaderStage.Vertex)])
    {
        _depthFormat = depthFormat;
        _depthBiasConstant = depthBiasConstant;
        _depthBiasSlope = depthBiasSlope;
        Build(compiler);
    }

    protected override GraphicsPipeline CreatePipeline(ShaderModule[] modules) =>
        new(Device, new GraphicsPipelineDesc
        {
            VertexShader = modules[0],
            FragmentShader = null,
            VertexLayout = ShadowVertexLayout,
            SetLayouts = [],
            PushConstants = [new PushConstantRange(0, 128, ShaderStages.Vertex)],
            ColorFormat = PixelFormat.Undefined,
            DepthFormat = _depthFormat,
            DepthTest = true,
            DepthBiasConstant = _depthBiasConstant,
            DepthBiasSlope = _depthBiasSlope,
            Cull = CullMode.Back,
            FrontFace = FrontFace.CounterClockwise,
        });
}
