namespace Agapanthe.Graphics.Memory;

/// <summary>
/// High-level intent of a GPU allocation, chosen by resource owners
/// (<c>GpuBuffer</c>/<c>GpuImage</c>) and later mapped to a concrete Vulkan memory type.
/// The free-list allocator itself works per <c>memoryTypeIndex</c>; this enum lives
/// beside it (architect decision, session 2) and is resolved to an index higher up.
/// </summary>
public enum MemoryDomain
{
    /// <summary>Fast GPU-only memory (vertex/index/render targets). Not CPU-mappable.</summary>
    DeviceLocal,

    /// <summary>CPU-visible memory (uniforms, staging) — persistently mapped by the backend.</summary>
    HostVisible,
}
