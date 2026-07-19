using Agapanthe.Graphics;

namespace Agapanthe.Rendering.Passes;

/// <summary>
/// Pass 0 (M6, decision 7): the directional shadow depth pass. Depth-only (no fragment shader, no color
/// attachment). <c>lightViewProj</c> travels as a 64-byte push constant (offset 0); per-instance model matrices
/// come from set 0 binding 0 (a storage buffer indexed by gl_InstanceIndex, P3-M1) so casters are drawn
/// instanced. Slope-scaled depth bias fights acne. The pipeline is invariant to swapchain resize; the shadow map
/// image itself stays owned by the <see cref="Renderer"/>.
/// </summary>
internal sealed class ShadowPass : ReloadablePass
{
    // Position-only view over the mesh vertex layout: keeps the full 60-byte stride (the mesh vertex buffers
    // are bound unchanged) but declares ONLY location 0, so shadow.vert consumes every declared attribute —
    // no "attribute not consumed by vertex shader" validation warning.
    private static readonly VertexLayout ShadowVertexLayout = new(
        stride: 60,
        attributes: [new VertexAttribute(Location: 0, Offset: 0, PixelFormat.R32G32B32Sfloat)]);

    private readonly DescriptorSetLayout _instanceSetLayout;
    private readonly PixelFormat _depthFormat;
    private readonly float _depthBiasConstant;
    private readonly float _depthBiasSlope;

    public ShadowPass(
        GraphicsDevice device, string shaderDirectory, ShaderCompiler compiler,
        DescriptorSetLayout instanceSetLayout,
        PixelFormat depthFormat, float depthBiasConstant, float depthBiasSlope)
        : base(device, shaderDirectory, [("shadow.vert", ShaderStage.Vertex)])
    {
        _instanceSetLayout = instanceSetLayout;
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
            // Set 0 = per-instance model matrices (P3-M1); lightViewProj stays a push constant (offset 0, 64 B).
            SetLayouts = [_instanceSetLayout],
            // lightViewProj (offset 0, 64 B) + the per-batch instance-SSBO offset (offset 64, 4 B, P3-M4 W0).
            PushConstants = [new PushConstantRange(0, 68, ShaderStages.Vertex)],
            ColorFormat = PixelFormat.Undefined,
            DepthFormat = _depthFormat,
            DepthTest = true,
            DepthBiasConstant = _depthBiasConstant,
            DepthBiasSlope = _depthBiasSlope,
            Cull = CullMode.Back,
            FrontFace = FrontFace.CounterClockwise,
        });
}
