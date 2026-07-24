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
/// Depth comparison a fragment must pass to be kept. <see cref="LessOrEqual"/> is the standard depth convention
/// (smaller = nearer); <see cref="GreaterOrEqual"/> is reversed-Z (larger = nearer, paired with a float depth
/// buffer cleared to 0) — P3-M8's fix for a planetary near/far range. Kept per-pipeline so the camera passes can
/// go reversed-Z while the shadow pass stays on its own standard-depth convention.
/// </summary>
public enum DepthCompare
{
    LessOrEqual,
    GreaterOrEqual,
}

/// <summary>
/// Everything needed to build a graphics pipeline, decoupled from Vulkan types. Replaces the
/// M1 hard-coded triangle constructor so pipelines can declare vertex input, descriptor set
/// layouts, push constants and depth state (spec §3.4).
/// </summary>
public sealed class GraphicsPipelineDesc
{
    public required ShaderModule VertexShader { get; init; }

    /// <summary>Fragment shader, or <c>null</c> for a depth-only pipeline (shadow map, depth prepass).</summary>
    public ShaderModule? FragmentShader { get; init; }

    /// <summary>Vertex buffer layout, or null for pipelines that generate geometry in-shader.</summary>
    public VertexLayout? VertexLayout { get; init; }

    /// <summary>Descriptor set layouts, in set-index order (set 0 first).</summary>
    public IReadOnlyList<DescriptorSetLayout> SetLayouts { get; init; } = [];

    public IReadOnlyList<PushConstantRange> PushConstants { get; init; } = [];

    /// <summary>Color attachment format, or <see cref="PixelFormat.Undefined"/> (default) for a depth-only pass.</summary>
    public PixelFormat ColorFormat { get; init; } = PixelFormat.Undefined;

    /// <summary>Depth attachment format, or <see cref="PixelFormat.Undefined"/> for no depth.</summary>
    public PixelFormat DepthFormat { get; init; } = PixelFormat.Undefined;

    public bool DepthTest { get; init; }
    public bool DepthWrite { get; init; } = true;

    /// <summary>Depth comparison op. Defaults to <see cref="DepthCompare.LessOrEqual"/> (standard depth) so existing
    /// pipelines are unchanged; camera passes opt into reversed-Z with <see cref="DepthCompare.GreaterOrEqual"/>.</summary>
    public DepthCompare DepthCompare { get; init; } = DepthCompare.LessOrEqual;

    public CullMode Cull { get; init; } = CullMode.Back;
    public FrontFace FrontFace { get; init; } = FrontFace.CounterClockwise;

    /// <summary>
    /// Constant depth-bias factor (<c>depthBiasConstantFactor</c>). Non-zero enables depth bias. Used with
    /// <see cref="DepthBiasSlope"/> for slope-scaled bias to fight shadow acne. <c>0</c> (default) = off.
    /// </summary>
    public float DepthBiasConstant { get; init; }

    /// <summary>
    /// Slope-scaled depth-bias factor (<c>depthBiasSlopeFactor</c>). Non-zero enables depth bias.
    /// <c>0</c> (default) = off.
    /// </summary>
    public float DepthBiasSlope { get; init; }
}
