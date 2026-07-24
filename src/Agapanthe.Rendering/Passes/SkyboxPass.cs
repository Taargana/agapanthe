using Agapanthe.Graphics;

namespace Agapanthe.Rendering.Passes;

/// <summary>
/// Skybox pass (M7): a vertex-buffer-less fullscreen triangle at the far plane sampling the environment
/// cubemap, drawn INSIDE the scene pass after the meshes (depth test LessOrEqual, no depth write) so it fills
/// only the background. Set 0 = camera UBO (ray reconstruction) + environment cubemap. The set layout is owned
/// by the <see cref="Renderer"/> and borrowed here.
/// </summary>
internal sealed class SkyboxPass : ReloadablePass
{
    private readonly DescriptorSetLayout _setLayout;
    private readonly PixelFormat _colorFormat;
    private readonly PixelFormat _depthFormat;

    public SkyboxPass(
        GraphicsDevice device, string shaderDirectory, ShaderCompiler compiler,
        DescriptorSetLayout setLayout, PixelFormat colorFormat, PixelFormat depthFormat)
        : base(device, shaderDirectory, [("skybox.vert", ShaderStage.Vertex), ("skybox.frag", ShaderStage.Fragment)])
    {
        _setLayout = setLayout;
        _colorFormat = colorFormat;
        _depthFormat = depthFormat;
        Build(compiler);
    }

    protected override GraphicsPipeline CreatePipeline(ShaderModule[] modules) =>
        new(Device, new GraphicsPipelineDesc
        {
            VertexShader = modules[0],
            FragmentShader = modules[1],
            VertexLayout = null, // fullscreen triangle generated from gl_VertexIndex
            SetLayouts = [_setLayout],
            ColorFormat = _colorFormat,
            DepthFormat = _depthFormat,
            // Reversed-Z (P3-M8): the far plane is now z = 0 and the depth clear is 0, so the background triangle
            // (forced to gl_Position.z = 0 in skybox.vert) passes GreaterOrEqual only where no geometry wrote a
            // larger (nearer) depth. Never writes depth.
            DepthTest = true,
            DepthWrite = false,
            DepthCompare = DepthCompare.GreaterOrEqual,
            Cull = CullMode.None,
        });
}
