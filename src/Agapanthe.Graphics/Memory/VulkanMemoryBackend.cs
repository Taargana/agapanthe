using Agapanthe.Core;
using Silk.NET.Vulkan;

namespace Agapanthe.Graphics.Memory;

/// <summary>
/// Production <see cref="IMemoryBackend"/> (spec §3.5): hands the free-list allocator large opaque
/// blocks backed by <c>vkAllocateMemory</c>. Host-visible blocks are persistently mapped for their
/// whole lifetime (uniforms/staging never re-map), device-local blocks are not CPU-mappable.
/// </summary>
/// <remarks>
/// <para>
/// A block's <see cref="MemoryBlock.Id"/> is the raw 64-bit <c>VkDeviceMemory</c> handle: it is
/// unique for the device's lifetime and lets <see cref="FreeBlock"/> reconstruct the handle without
/// a side table. Whether a block is mapped round-trips through <see cref="MemoryBlock.MappedPointer"/>
/// (non-zero ⇒ mapped), so no separate mapped-flag dictionary is needed; <see cref="_liveBlocks"/>
/// exists only to validate frees and to back the owner's finalizer leak check.
/// </para>
/// <para><b>Not thread-safe</b> — driven exclusively by a single <see cref="GpuAllocator"/> (phase-1 single-threaded).</para>
/// </remarks>
internal sealed unsafe class VulkanMemoryBackend : IMemoryBackend
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly MemoryPropertyFlags[] _memoryTypeFlags;
    private readonly HashSet<ulong> _liveBlocks = new();

    public VulkanMemoryBackend(Vk vk, PhysicalDevice physicalDevice, Device device)
    {
        ArgumentNullException.ThrowIfNull(vk);
        _vk = vk;
        _device = device;

        // Snapshot the per-type property flags into a plain array: indexing the inline
        // MemoryTypes buffer on a stored struct field would force a defensive copy each call.
        _vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out var memProps);
        _memoryTypeFlags = new MemoryPropertyFlags[memProps.MemoryTypeCount];
        for (var i = 0u; i < memProps.MemoryTypeCount; i++)
        {
            _memoryTypeFlags[i] = memProps.MemoryTypes[(int)i].PropertyFlags;
        }
    }

    /// <summary>True while any block obtained here has not been freed (owner-finalizer leak check).</summary>
    public bool HasLiveBlocks => _liveBlocks.Count > 0;

    /// <summary>Vulkan property flags of a memory type (diagnostics/labels).</summary>
    public MemoryPropertyFlags GetMemoryTypeFlags(uint memoryTypeIndex) => _memoryTypeFlags[memoryTypeIndex];

    public MemoryBlock AllocateBlock(uint memoryTypeIndex, ulong size)
    {
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = size,
            MemoryTypeIndex = memoryTypeIndex,
        };
        DeviceMemory memory;
        VkCheck.ThrowIfFailed(_vk.AllocateMemory(_device, &allocInfo, null, &memory), "vkAllocateMemory");
        ResourceTracker.Register("VkDeviceMemory");

        var mapped = nint.Zero;
        if ((_memoryTypeFlags[memoryTypeIndex] & MemoryPropertyFlags.HostVisibleBit) != 0)
        {
            try
            {
                void* ptr;
                // Persistent map of the whole block; individual suballocations offset from this base.
                VkCheck.ThrowIfFailed(_vk.MapMemory(_device, memory, 0, size, 0, &ptr), "vkMapMemory");
                mapped = (nint)ptr;
            }
            catch
            {
                _vk.FreeMemory(_device, memory, null);
                ResourceTracker.Unregister("VkDeviceMemory");
                throw;
            }
        }

        var id = memory.Handle;
        _liveBlocks.Add(id);
        return new MemoryBlock(id, mapped);
    }

    public void FreeBlock(MemoryBlock block)
    {
        if (!_liveBlocks.Remove(block.Id))
        {
            throw new InvalidOperationException(
                $"FreeBlock of an unknown or already-freed VkDeviceMemory (id {block.Id}).");
        }

        var memory = new DeviceMemory(block.Id);
        if (block.MappedPointer != nint.Zero)
        {
            _vk.UnmapMemory(_device, memory);
        }

        _vk.FreeMemory(_device, memory, null);
        ResourceTracker.Unregister("VkDeviceMemory");
    }
}
