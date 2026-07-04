using Agapanthe.Graphics.Memory;

namespace Agapanthe.Tests;

/// <summary>
/// Drives <see cref="GpuAllocator"/> over a mock <see cref="IMemoryBackend"/> and an injected memory
/// type resolver (spec §3.5 test seam) — exercises domain→type routing, per-type sub-allocators, free
/// routing, stats aggregation and disposal, all without a GPU.
/// </summary>
public class GpuAllocatorTests
{
    // Fixed, distinct memory type per domain — mirrors what FindMemoryType would return on hardware.
    private const uint DeviceLocalType = 0;
    private const uint HostVisibleType = 1;

    private static uint ResolveByDomain(uint memoryTypeBits, MemoryDomain domain) =>
        domain == MemoryDomain.DeviceLocal ? DeviceLocalType : HostVisibleType;

    /// <summary>In-memory backend: sequential ids, live-block counting, optional fake mapped pointers.</summary>
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

    [Fact]
    public void Allocate_RecordsRequestedDomain_AndReturnsRequestedSize()
    {
        using var allocator = new GpuAllocator(new FakeBackend(), ResolveByDomain);

        var device = allocator.Allocate(Req(256), MemoryDomain.DeviceLocal);
        var host = allocator.Allocate(Req(300), MemoryDomain.HostVisible);

        Assert.Equal(MemoryDomain.DeviceLocal, device.Domain);
        Assert.Equal(256UL, device.Size);
        Assert.Equal(MemoryDomain.HostVisible, host.Domain);
        Assert.Equal(300UL, host.Size);
    }

    [Fact]
    public void SameDomain_SharesOneSubAllocatorAndBlock()
    {
        var backend = new FakeBackend();
        using var allocator = new GpuAllocator(backend, ResolveByDomain);

        var a = allocator.Allocate(Req(256), MemoryDomain.DeviceLocal);
        var b = allocator.Allocate(Req(256), MemoryDomain.DeviceLocal);

        // Two small device-local requests fit the same 64 MiB block → one backend block total.
        Assert.Equal(1, backend.LiveBlocks);
        Assert.NotEqual(a.Offset, b.Offset);
    }

    [Fact]
    public void DifferentDomains_UseSeparateSubAllocators()
    {
        var backend = new FakeBackend();
        using var allocator = new GpuAllocator(backend, ResolveByDomain);

        allocator.Allocate(Req(256), MemoryDomain.DeviceLocal);
        allocator.Allocate(Req(256), MemoryDomain.HostVisible);

        // One block per memory type.
        Assert.Equal(2, backend.LiveBlocks);

        var stats = allocator.GetStats();
        Assert.Equal(2, stats.Count);
        Assert.Contains(stats, s => s.MemoryTypeIndex == DeviceLocalType);
        Assert.Contains(stats, s => s.MemoryTypeIndex == HostVisibleType);
    }

    [Fact]
    public void HostVisibleAllocation_ExposesMappedPointer_DeviceLocalDoesNot()
    {
        using var allocator = new GpuAllocator(new FakeBackend(mapped: true), ResolveByDomain);

        var host = allocator.Allocate(Req(256), MemoryDomain.HostVisible);
        Assert.NotEqual(nint.Zero, host.MappedPointer);

        // A non-mapping backend yields no CPU pointer even for host-visible requests.
        using var unmapped = new GpuAllocator(new FakeBackend(mapped: false), ResolveByDomain);
        var noMap = unmapped.Allocate(Req(256), MemoryDomain.HostVisible);
        Assert.Equal(nint.Zero, noMap.MappedPointer);
    }

    [Fact]
    public void MappedPointer_TracksOffsetWithinBlock()
    {
        using var allocator = new GpuAllocator(new FakeBackend(mapped: true), ResolveByDomain);

        var a = allocator.Allocate(Req(256), MemoryDomain.HostVisible);
        var b = allocator.Allocate(Req(256), MemoryDomain.HostVisible);

        // Same block, so the pointer delta equals the offset delta.
        Assert.Equal((nint)(b.Offset - a.Offset), b.MappedPointer - a.MappedPointer);
    }

    [Fact]
    public void Free_ReturnsSpaceForReuse()
    {
        var backend = new FakeBackend();
        using var allocator = new GpuAllocator(backend, ResolveByDomain);

        var first = allocator.Allocate(Req(256), MemoryDomain.DeviceLocal);
        allocator.Free(first);
        var second = allocator.Allocate(Req(256), MemoryDomain.DeviceLocal);

        Assert.Equal(first.Offset, second.Offset);
        Assert.Equal(1, backend.LiveBlocks); // no new block needed
    }

    [Fact]
    public void GetStats_AggregatesPerType()
    {
        using var allocator = new GpuAllocator(new FakeBackend(), ResolveByDomain);

        allocator.Allocate(Req(256), MemoryDomain.DeviceLocal);
        allocator.Allocate(Req(512), MemoryDomain.HostVisible);
        allocator.Allocate(Req(128), MemoryDomain.HostVisible);

        var stats = allocator.GetStats();
        var device = Assert.Single(stats, s => s.MemoryTypeIndex == DeviceLocalType);
        var host = Assert.Single(stats, s => s.MemoryTypeIndex == HostVisibleType);

        Assert.Equal(256UL, device.UsedBytes);
        Assert.Equal(1, device.AllocationCount);
        Assert.Equal(640UL, host.UsedBytes);
        Assert.Equal(2, host.AllocationCount);
    }

    [Fact]
    public void LogStats_WithNoAllocations_DoesNotThrow()
    {
        using var allocator = new GpuAllocator(new FakeBackend(), ResolveByDomain);
        Assert.Empty(allocator.GetStats());
        allocator.LogStats(); // must handle the empty case cleanly
    }

    [Fact]
    public void Allocate_RejectsZeroSize()
    {
        using var allocator = new GpuAllocator(new FakeBackend(), ResolveByDomain);
        Assert.Throws<ArgumentException>(() => allocator.Allocate(Req(0), MemoryDomain.DeviceLocal));
    }

    [Fact]
    public void Free_FromUnknownMemoryType_Throws()
    {
        using var allocator = new GpuAllocator(new FakeBackend(), ResolveByDomain);
        // A default allocation was never handed out by this allocator (its type has no sub-allocator).
        Assert.Throws<InvalidOperationException>(() => allocator.Free(default));
    }

    [Fact]
    public void Dispose_FreesAllBlocks_AndBlocksFurtherUse()
    {
        var backend = new FakeBackend();
        var allocator = new GpuAllocator(backend, ResolveByDomain);

        allocator.Allocate(Req(256), MemoryDomain.DeviceLocal);
        allocator.Allocate(Req(256), MemoryDomain.HostVisible);
        Assert.Equal(2, backend.LiveBlocks);

        allocator.Dispose();
        Assert.Equal(0, backend.LiveBlocks);

        // Idempotent + no use after dispose.
        allocator.Dispose();
        Assert.Throws<ObjectDisposedException>(() => allocator.Allocate(Req(16), MemoryDomain.DeviceLocal));
    }
}
