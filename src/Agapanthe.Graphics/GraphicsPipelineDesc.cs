namespace Agapanthe.Graphics;

/// <summary>Triangle winding treated as front-facing.</summary>
public enum FrontFace
{
    CounterClockwise,
    Clockwise,
}

/// <summary>Which faces to cull.</summary>
public enum CullMode
{
    None,
    Back,
    Front,
}

/// <summary>
/// Everything needed to build a graphics pipeline, decoupled from Vulkan types. Replaces the
/// M1 hard-coded triangle constructor so pipelines can declare vertex input, descriptor set
/// layouts, push constants and depth state (spec §3.4).
/// </summary>
public sealed class GraphicsPipelineDesc
{
    public required ShaderModule VertexShader { get; init; }
    public required ShaderModule FragmentShader { get; init; }

    /// <summary>Vertex buffer layout, or null for pipelines that generate geometry in-shader.</summary>
    public VertexLayout? VertexLayout { get; init; }

    /// <summary>Descriptor set layouts, in set-index order (set 0 first).</summary>
    public IReadOnlyList<DescriptorSetLayout> SetLayouts { get; init; } = [];

    public IReadOnlyList<PushConstantRange> PushConstants { get; init; } = [];

    public required PixelFormat ColorFormat { get; init; }

    /// <summary>Depth attachment format, or <see cref="PixelFormat.Undefined"/> for no depth.</summary>
    public PixelFormat DepthFormat { get; init; } = PixelFormat.Undefined;

    public bool DepthTest { get; init; }
    public bool DepthWrite { get; init; } = true;
    public CullMode Cull { get; init; } = CullMode.Back;
    public FrontFace FrontFace { get; init; } = FrontFace.CounterClockwise;
}
