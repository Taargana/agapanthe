namespace Agapanthe.Graphics;

/// <summary>One vertex attribute: its shader location, byte offset in the vertex, and format.</summary>
public readonly record struct VertexAttribute(uint Location, uint Offset, PixelFormat Format);

/// <summary>
/// Describes the layout of a single interleaved vertex buffer (binding 0). Consumed by the
/// pipeline to build its vertex input state without exposing Vulkan types to callers.
/// </summary>
public sealed class VertexLayout(uint stride, VertexAttribute[] attributes)
{
    public uint Stride { get; } = stride;
    public IReadOnlyList<VertexAttribute> Attributes { get; } = attributes;
}
