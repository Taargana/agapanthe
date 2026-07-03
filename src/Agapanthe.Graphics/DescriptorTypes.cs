namespace Agapanthe.Graphics;

/// <summary>Shader stages a descriptor or push constant is visible to. Combinable.</summary>
[Flags]
public enum ShaderStages
{
    None = 0,
    Vertex = 1 << 0,
    Fragment = 1 << 1,
    Compute = 1 << 2,
}

/// <summary>Kind of resource bound at a descriptor slot.</summary>
public enum DescriptorKind
{
    UniformBuffer,
    CombinedImageSampler, // used from M3 (textures)
}

/// <summary>One binding within a descriptor set layout.</summary>
public readonly record struct DescriptorBinding(uint Binding, DescriptorKind Kind, ShaderStages Stages);

/// <summary>A push-constant range: byte offset, byte size, and the stages that read it.</summary>
public readonly record struct PushConstantRange(uint Offset, uint Size, ShaderStages Stages);
