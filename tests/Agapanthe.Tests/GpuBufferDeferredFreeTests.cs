using Agapanthe.Graphics.Memory;

namespace Agapanthe.Tests;

/// <summary>
/// Validates the deferred-free reconstruction that <c>GpuBuffer.Dispose</c> relies on, without a GPU.
/// When a buffer is disposed, only raw values travel in the (4× ulong) deletion payload: the block
/// id, the memory type index and the suballocation offset. The deferred destructor rebuilds a
/// <see cref="GpuAllocation"/> from exactly those values and calls <see cref="GpuAllocator.Free"/>.
/// These tests prove that reconstruction path frees the right region — i.e. that the free-list
/// identifies a region by (memory-type, block, offset) alone and needs neither the size nor the
/// mapped pointer that the payload cannot carry.
/// </summary>
public class GpuBufferDeferredFreeTests
{
    private const uint DeviceLocalType = 0;
    private const uint HostVisibleType = 1;

    private static uint ResolveByDomain(uint memoryTypeBits, MemoryDomain domain) =>
        domain == MemoryDomain.DeviceLocal ? DeviceLocalType : HostVisibleType;

    private sealed class FakeBackend : IMemoryBackend
    {
        private const long PointerStride = 0x1000_0000;
        private ulong _nextId = 1;
        private readonly bool _mapped;

        public FakeBackend(bool mapped = false) => _mapped = mapped;

        public int LiveBlocks { get; private set; }

        public MemoryBlock AllocateBlock(uint memoryTypeIndex, ulong size)
        {
            LiveBlocks++;
            var id = _nextId++;
            var ptr = _mapped ? (nint)(PointerStride * (long)id) : nint.Zero;
            return new MemoryBlock(id, ptr);
        }

        public void FreeBlock(MemoryBlock block) => LiveBlocks--;
    }

    private static MemoryRequirementsInfo Req(ulong size, ulong alignment = 1, uint bits = 0xFFFF_FFFF) =>
        new(size, alignment, bits);

    // Mirrors GpuBuffer.ReconstructAllocation: rebuild the allocation from only the raw values a
    // DeletionPayload can carry (block id, memory type index, offset) — no size, no mapped pointer.
    private static GpuAllocation ReconstructFromPayload(ulong blockId, uint memoryTypeIndex, ulong offset) =>
        new(new Suballocation(new MemoryBlock(blockId, nint.Zero), offset, 0), memoryTypeIndex, MemoryDomain.DeviceLocal);

    [Fact]
    public void ReconstructedAllocation_FreesTheOriginalRegion_AllowingReuse()
    {
        var backend = new FakeBackend();
        using var allocator = new GpuAllocator(backend, ResolveByDomain);

        var original = allocator.Allocate(Req(256), MemoryDomain.HostVisible);

        // Extract exactly what GpuBuffer stores in the deletion payload.
        var blockId = original.Suballocation.Block.Id;
        var typeIndex = original.MemoryTypeIndex;
        var offset = original.Offset;

        var rebuilt = ReconstructFromPayload(blockId, typeIndex, offset);
        allocator.Free(in rebuilt); // deferred-destroy path: must free the original region

        var reused = allocator.Allocate(Req(256), MemoryDomain.HostVisible);
        Assert.Equal(offset, reused.Offset);          // same slot reclaimed
        Assert.Equal(1, backend.LiveBlocks);          // no new block was needed
    }

    [Fact]
    public void ReconstructedAllocation_FreesDedicatedBlock()
    {
        var backend = new FakeBackend();
        using var allocator = new GpuAllocator(backend, ResolveByDomain);

        // Larger than the default dedicated threshold (32 MiB) → its own exact-size block.
        var original = allocator.Allocate(Req(48UL * 1024 * 1024), MemoryDomain.DeviceLocal);
        Assert.Equal(1, backend.LiveBlocks);

        var rebuilt = ReconstructFromPayload(
            original.Suballocation.Block.Id, original.MemoryTypeIndex, original.Offset);
        allocator.Free(in rebuilt);

        Assert.Equal(0, backend.LiveBlocks); // dedicated block released whole
    }

    [Fact]
    public void ReconstructedAllocation_RoutesToCorrectMemoryType()
    {
        var backend = new FakeBackend();
        using var allocator = new GpuAllocator(backend, ResolveByDomain);

        var device = allocator.Allocate(Req(256), MemoryDomain.DeviceLocal);
        var host = allocator.Allocate(Req(256), MemoryDomain.HostVisible);
        Assert.Equal(2, backend.LiveBlocks);

        // Free both via the reconstruction path; each must return to its own sub-allocator.
        var deviceRebuilt = ReconstructFromPayload(device.Suballocation.Block.Id, device.MemoryTypeIndex, device.Offset);
        var hostRebuilt = ReconstructFromPayload(host.Suballocation.Block.Id, host.MemoryTypeIndex, host.Offset);
        allocator.Free(in deviceRebuilt);
        allocator.Free(in hostRebuilt);

        // Reusing each domain reclaims its freed slot rather than growing.
        var deviceReuse = allocator.Allocate(Req(256), MemoryDomain.DeviceLocal);
        var hostReuse = allocator.Allocate(Req(256), MemoryDomain.HostVisible);
        Assert.Equal(device.Offset, deviceReuse.Offset);
        Assert.Equal(host.Offset, hostReuse.Offset);
        Assert.Equal(2, backend.LiveBlocks);
    }
}
