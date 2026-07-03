namespace Agapanthe.Graphics;

/// <summary>How a <see cref="GpuBuffer"/> is used by the GPU. Combinable.</summary>
[Flags]
public enum BufferUsage
{
    None = 0,
    Vertex = 1 << 0,
    Index = 1 << 1,
    Uniform = 1 << 2,
}
