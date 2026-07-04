using System.Runtime.CompilerServices;
using Agapanthe.Core;
using Silk.NET.Vulkan;

// Test seam (spec §3.5): the tests drive GpuAllocator over a mock IMemoryBackend without a GPU
// through the internal constructor below.
[assembly: InternalsVisibleTo("Agapanthe.Tests")]

namespace Agapanthe.Graphics.Memory;

/// <summary>
/// GPU memory requirements for one allocation, mirroring the fields of <c>VkMemoryRequirements</c>
/// without leaking any Vulkan type. Resource owners fill this from
/// <c>vkGet{Buffer,Image}MemoryRequirements</c>.
/// </summary>
/// <param name="Size">Number of bytes the resource needs.</param>
/// <param name="Alignment">Required start alignment (power of two); <c>0</c> is treated as <c>1</c>.</param>
/// <param name="MemoryTypeBits">Bitmask of memory types the resource may bind to.</param>
public readonly record struct MemoryRequirementsInfo(ulong Size, ulong Alignment, uint MemoryTypeBits);

/// <summary>
/// A slice of GPU memory owned by the <see cref="GpuAllocator"/>. Public callers see the byte
/// <see cref="Offset"/> within its device memory, the <see cref="Size"/>, the CPU
/// <see cref="MappedPointer"/> (<see cref="nint.Zero"/> for device-local) and the requested
/// <see cref="Domain"/>. The Vulkan handle needed to bind a resource and the routing data needed to
/// free it are internal — no Vulkan type is exposed.
/// </summary>
public readonly struct GpuAllocation
{
    private readonly Suballocation _suballocation;

    internal GpuAllocation(Suballocation suballocation, uint memoryTypeIndex, MemoryDomain domain)
    {
        _suballocation = suballocation;
        MemoryTypeIndex = memoryTypeIndex;
        Domain = domain;
    }

    /// <summary>Byte offset of this allocation inside its (possibly shared) device memory block.</summary>
    public ulong Offset => _suballocation.Offset;

    /// <summary>Byte size of this allocation (exactly the requested size).</summary>
    public ulong Size => _suballocation.Size;

    /// <summary>
    /// CPU address of this allocation when it lives in host-visible memory, otherwise
    /// <see cref="nint.Zero"/>. The backing block is persistently mapped, so this is valid for the
    /// allocation's whole lifetime.
    /// </summary>
    public nint MappedPointer => _suballocation.MappedPointer;

    /// <summary>The domain this allocation was requested for.</summary>
    public MemoryDomain Domain { get; }

    /// <summary>Memory type index — routes <see cref="GpuAllocator.Free"/> back to the right sub-allocator.</summary>
    internal uint MemoryTypeIndex { get; }

    /// <summary>The opaque suballocation handed back to the free-list on release.</summary>
    internal Suballocation Suballocation => _suballocation;

    /// <summary>
    /// The <c>VkDeviceMemory</c> to bind against (block id is the raw handle). Callers bind at
    /// <see cref="Offset"/>. <see cref="Silk.NET.Vulkan.DeviceMemory"/> stays internal — no Vulkan
    /// type crosses the public surface.
    /// </summary>
    internal DeviceMemory DeviceMemory => new(_suballocation.Block.Id);
}

/// <summary>
/// The engine's GPU memory allocator (spec §3.5): suballocates <c>vkAllocateMemory</c> blocks per
/// Vulkan memory type via a <see cref="FreeListAllocator"/> created lazily per type, over a
/// <see cref="VulkanMemoryBackend"/>. Owned by <see cref="GraphicsDevice"/>. Resolves a
/// <see cref="MemoryDomain"/> plus the resource's type bits to a concrete memory type using the
/// device's <c>FindMemoryType</c> (DeviceLocal → DEVICE_LOCAL; HostVisible → HOST_VISIBLE|HOST_COHERENT).
/// </summary>
/// <remarks>
/// <b>Not thread-safe</b> (phase-1 rendering is single-threaded). The underlying blocks are retained
/// until <see cref="Dispose"/> (reclamation/defragmentation is deferred to phase 2).
/// </remarks>
public sealed class GpuAllocator : IDisposable
{
    private readonly IMemoryBackend _backend;
    private readonly VulkanMemoryBackend? _vulkanBackend;
    private readonly Func<uint, MemoryDomain, uint> _resolveMemoryType;
    private readonly Dictionary<uint, FreeListAllocator> _allocators = new();
    private bool _disposed;

    /// <summary>Production constructor: allocates real <c>VkDeviceMemory</c> through the device.</summary>
    internal GpuAllocator(GraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var backend = new VulkanMemoryBackend(device.Api, device.PhysicalDevice, device.Device);
        _backend = backend;
        _vulkanBackend = backend;
        // Captured once at construction (not on the allocation hot path): maps domain → required
        // property flags and delegates the type search to the physical device.
        _resolveMemoryType = (bits, domain) => device.FindMemoryType(bits, DomainToFlags(domain));
    }

    /// <summary>
    /// Test constructor (spec §3.5): drives the same routing/stats logic over a mock backend with an
    /// injected type resolver, so it runs without a GPU.
    /// </summary>
    internal GpuAllocator(IMemoryBackend backend, Func<uint, MemoryDomain, uint> resolveMemoryType)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(resolveMemoryType);
        _backend = backend;
        _vulkanBackend = backend as VulkanMemoryBackend;
        _resolveMemoryType = resolveMemoryType;
    }

    ~GpuAllocator()
    {
        // No native handle is destroyed here (spec §3.2.2); a live block at finalization means the
        // owner was never disposed → report the leak (ResourceTracker already counts each block too).
        if (!_disposed && (_vulkanBackend?.HasLiveBlocks ?? false))
        {
            ResourceTracker.ReportFinalizerLeak(nameof(GpuAllocator));
        }
    }

    /// <summary>
    /// Reserves memory for a resource. Resolves the memory type from <paramref name="domain"/> and
    /// <see cref="MemoryRequirementsInfo.MemoryTypeBits"/>, then suballocates from that type's
    /// free-list (a dedicated block is used for oversized requests, handled by the free-list).
    /// </summary>
    /// <exception cref="ArgumentException"><see cref="MemoryRequirementsInfo.Size"/> is zero.</exception>
    /// <exception cref="GraphicsException">No memory type satisfies the domain and type bits.</exception>
    public GpuAllocation Allocate(in MemoryRequirementsInfo requirements, MemoryDomain domain)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (requirements.Size == 0)
        {
            throw new ArgumentException("Allocation size must be positive.", nameof(requirements));
        }

        var memoryTypeIndex = _resolveMemoryType(requirements.MemoryTypeBits, domain);
        var allocator = GetOrCreateAllocator(memoryTypeIndex);
        var alignment = requirements.Alignment == 0 ? 1UL : requirements.Alignment;
        var sub = allocator.Allocate(requirements.Size, alignment);
        return new GpuAllocation(sub, memoryTypeIndex, domain);
    }

    /// <summary>
    /// Releases an allocation <b>immediately</b>. Callers that free a resource still potentially in
    /// flight must defer this behind the device <c>DeletionQueue</c> (done resource-side in M3-04/05,
    /// which carries the suballocation offset in the deletion payload); the blocks themselves survive
    /// until <see cref="Dispose"/>, so replaying a deferred free here is safe.
    /// </summary>
    /// <exception cref="InvalidOperationException">The allocation did not originate from this allocator.</exception>
    public void Free(in GpuAllocation allocation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_allocators.TryGetValue(allocation.MemoryTypeIndex, out var allocator))
        {
            throw new InvalidOperationException(
                $"Free of an allocation from unknown memory type {allocation.MemoryTypeIndex}.");
        }

        allocator.Free(allocation.Suballocation);
    }

    /// <summary>Snapshot of per-memory-type statistics (one entry per type ever used).</summary>
    public IReadOnlyList<AllocationStats> GetStats()
    {
        var stats = new List<AllocationStats>(_allocators.Count);
        foreach (var allocator in _allocators.Values)
        {
            stats.Add(allocator.GetStats());
        }

        return stats;
    }

    /// <summary>
    /// Logs a human-readable memory report (spec §6 M3: "stats mémoire visibles"): per memory type
    /// the MiB allocated/used, block and allocation counts and fragmentation, plus a total. Logs a
    /// clear "nothing allocated" line when no type has been used yet.
    /// </summary>
    public void LogStats()
    {
        if (_allocators.Count == 0)
        {
            Log.Info("GpuAllocator: no GPU memory allocated (no resource has requested any yet).");
            return;
        }

        Log.Info("GpuAllocator memory stats:");
        ulong totalAllocated = 0;
        ulong totalUsed = 0;
        var totalBlocks = 0;
        foreach (var (memoryTypeIndex, allocator) in _allocators)
        {
            var stats = allocator.GetStats();
            totalAllocated += stats.AllocatedBytes;
            totalUsed += stats.UsedBytes;
            totalBlocks += stats.BlockCount;
            var label = _vulkanBackend is null ? "?" : DescribeFlags(_vulkanBackend.GetMemoryTypeFlags(memoryTypeIndex));
            Log.Info(
                $"  type {stats.MemoryTypeIndex} [{label}]: {ToMiB(stats.AllocatedBytes)} allocated / " +
                $"{ToMiB(stats.UsedBytes)} used, {stats.BlockCount} block(s), {stats.AllocationCount} alloc(s), " +
                $"frag {stats.Fragmentation:0.00}");
        }

        Log.Info($"  total: {ToMiB(totalAllocated)} allocated / {ToMiB(totalUsed)} used across {totalBlocks} block(s).");
    }

    /// <summary>Frees every backing block. Only valid after the GPU is idle and the DeletionQueue is drained.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var allocator in _allocators.Values)
        {
            allocator.Dispose(); // frees this type's blocks through the backend (vkFreeMemory)
        }

        _allocators.Clear();
        GC.SuppressFinalize(this);
    }

    private FreeListAllocator GetOrCreateAllocator(uint memoryTypeIndex)
    {
        if (!_allocators.TryGetValue(memoryTypeIndex, out var allocator))
        {
            allocator = new FreeListAllocator(_backend, memoryTypeIndex);
            _allocators.Add(memoryTypeIndex, allocator);
        }

        return allocator;
    }

    private static MemoryPropertyFlags DomainToFlags(MemoryDomain domain) => domain switch
    {
        MemoryDomain.DeviceLocal => MemoryPropertyFlags.DeviceLocalBit,
        MemoryDomain.HostVisible => MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
        _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, "Unknown memory domain."),
    };

    private static string ToMiB(ulong bytes) => $"{bytes / (1024.0 * 1024.0):0.00} MiB";

    private static string DescribeFlags(MemoryPropertyFlags flags)
    {
        var parts = new List<string>(4);
        if ((flags & MemoryPropertyFlags.DeviceLocalBit) != 0)
        {
            parts.Add("DeviceLocal");
        }

        if ((flags & MemoryPropertyFlags.HostVisibleBit) != 0)
        {
            parts.Add("HostVisible");
        }

        if ((flags & MemoryPropertyFlags.HostCoherentBit) != 0)
        {
            parts.Add("HostCoherent");
        }

        if ((flags & MemoryPropertyFlags.HostCachedBit) != 0)
        {
            parts.Add("HostCached");
        }

        return parts.Count == 0 ? "none" : string.Join('|', parts);
    }
}
