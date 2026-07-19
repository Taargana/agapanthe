namespace Agapanthe.Graphics;

/// <summary>How a <see cref="GpuBuffer"/> is used by the GPU. Combinable.</summary>
[Flags]
public enum BufferUsage
{
    None = 0,
    Vertex = 1 << 0,
    Index = 1 << 1,
    Uniform = 1 << 2,
    Storage = 1 << 3,

    /// <summary>Source of <c>vkCmdDrawIndexedIndirect</c> draw commands (P3-M4). Combine with
    /// <see cref="Storage"/> when a compute shader writes the command's <c>instanceCount</c>.</summary>
    Indirect = 1 << 4,
}
