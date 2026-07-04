namespace Agapanthe.Graphics.Memory;

/// <summary>
/// An opaque block of GPU memory handed out by an <see cref="IMemoryBackend"/>.
/// The <see cref="Id"/> is the backend's private identity (the Vulkan backend keys it to a
/// <c>VkDeviceMemory</c>); <see cref="MappedPointer"/> is the base address of the block when it
/// is persistently mapped (host-visible memory), or <see cref="nint.Zero"/> when it is not
/// CPU-mappable (device-local). The free-list allocator treats this value as fully opaque and
/// only ever hands it back to the backend it came from.
/// </summary>
/// <param name="Id">Backend-private block identity (non-zero for a valid block).</param>
/// <param name="MappedPointer">Base pointer of the mapped block, or <see cref="nint.Zero"/>.</param>
public readonly record struct MemoryBlock(ulong Id, nint MappedPointer);

/// <summary>
/// Test seam for GPU memory allocation (spec §3.5). The free-list allocator suballocates within
/// large opaque blocks obtained here; the production implementation calls
/// <c>vkAllocateMemory</c>/<c>vkFreeMemory</c> (M3-03), while unit tests supply an in-memory mock
/// so the free-list logic is exercised without a GPU.
/// </summary>
public interface IMemoryBackend
{
    /// <summary>
    /// Allocates one block of <paramref name="size"/> bytes for the given
    /// <paramref name="memoryTypeIndex"/>. The returned base address is assumed to satisfy any
    /// alignment the caller may request (Vulkan device memory is over-aligned to
    /// <c>bufferImageGranularity</c> and beyond, so offset 0 is aligned to every power of two).
    /// </summary>
    MemoryBlock AllocateBlock(uint memoryTypeIndex, ulong size);

    /// <summary>Releases a block previously returned by <see cref="AllocateBlock"/>.</summary>
    void FreeBlock(MemoryBlock block);
}
