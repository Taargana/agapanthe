using Agapanthe.Graphics.Memory;

namespace Agapanthe.Tests;

public class FreeListAllocatorTests
{
    /// <summary>
    /// In-memory <see cref="IMemoryBackend"/>: hands out sequential ids, tracks how many blocks are
    /// live and every size requested, and optionally returns distinct fake mapped pointers.
    /// </summary>
    private sealed class FakeBackend : IMemoryBackend
    {
        private const long PointerStride = 0x1000_0000;
        private ulong _nextId = 1;
        private readonly bool _mapped;

        public FakeBackend(bool mapped = false) => _mapped = mapped;

        public int LiveBlocks { get; private set; }
        public int TotalBlocksAllocated { get; private set; }
        public List<ulong> RequestedSizes { get; } = new();

        public MemoryBlock AllocateBlock(uint memoryTypeIndex, ulong size)
        {
            LiveBlocks++;
            TotalBlocksAllocated++;
            RequestedSizes.Add(size);
            ulong id = _nextId++;
            nint ptr = _mapped ? (nint)(PointerStride * (long)id) : nint.Zero;
            return new MemoryBlock(id, ptr);
        }

        public void FreeBlock(MemoryBlock block) => LiveBlocks--;
    }

    private static bool Overlap(Suballocation a, Suballocation b) =>
        a.Block.Id == b.Block.Id && a.Offset < b.Offset + b.Size && b.Offset < a.Offset + a.Size;

    [Fact]
    public void FirstAllocation_CreatesBlockLazily()
    {
        var backend = new FakeBackend();
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 1024);

        // No block should exist before the first request (spec §3.5: no eager allocation).
        Assert.Equal(0, backend.TotalBlocksAllocated);

        allocator.Allocate(128);
        Assert.Equal(1, backend.TotalBlocksAllocated);
    }

    [Fact]
    public void Allocations_RespectAlignment_WithoutOverlap()
    {
        var backend = new FakeBackend();
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 4096);

        var a = allocator.Allocate(100, alignment: 256);
        var b = allocator.Allocate(100, alignment: 256);
        var c = allocator.Allocate(100, alignment: 256);

        foreach (var s in new[] { a, b, c })
        {
            Assert.Equal(0UL, s.Offset % 256);
        }

        // All in the same block, none overlapping.
        Assert.Equal(a.Block.Id, b.Block.Id);
        Assert.Equal(a.Block.Id, c.Block.Id);
        Assert.False(Overlap(a, b));
        Assert.False(Overlap(a, c));
        Assert.False(Overlap(b, c));
    }

    [Fact]
    public void FreedRegion_IsReusedByLaterAllocation()
    {
        var backend = new FakeBackend();
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 1024);

        var first = allocator.Allocate(256);
        allocator.Free(first);
        var second = allocator.Allocate(256);

        Assert.Equal(first.Block.Id, second.Block.Id);
        Assert.Equal(first.Offset, second.Offset);
        Assert.Equal(1, backend.TotalBlocksAllocated); // no new block needed
    }

    [Fact]
    public void FreeingNeighbours_CoalescesIntoSingleHole()
    {
        var backend = new FakeBackend();
        // blockSize == 3 * 256 so a, b, c fill the block exactly (no trailing free region).
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 768);

        var a = allocator.Allocate(256);
        var b = allocator.Allocate(256);
        var c = allocator.Allocate(256);
        Assert.Equal(a.Block.Id, c.Block.Id);

        // Free the middle, then both neighbours: the three holes must merge into one.
        allocator.Free(b);
        allocator.Free(a);
        allocator.Free(c);

        var stats = allocator.GetStats();
        Assert.Equal(0, stats.AllocationCount);
        Assert.Equal(0UL, stats.UsedBytes);
        Assert.Equal(1, stats.BlockCount);
        Assert.Equal(768UL, stats.LargestFreeRegion); // one contiguous hole, not three of 256
        Assert.Equal(0.0, stats.Fragmentation);

        // Proof of coalescing: a 384-byte request (larger than any single 256 fragment, but within
        // the dedicated threshold) now fits in the same block at offset 0 without a new block.
        var big = allocator.Allocate(384);
        Assert.Equal(a.Block.Id, big.Block.Id);
        Assert.Equal(0UL, big.Offset);
        Assert.Equal(1, backend.TotalBlocksAllocated);
    }

    [Fact]
    public void NonAdjacentFrees_DoNotCoalesce()
    {
        var backend = new FakeBackend();
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 768);

        allocator.Allocate(256);              // a
        var b = allocator.Allocate(256);      // b (middle)
        allocator.Allocate(256);              // c

        allocator.Free(b); // single 256 hole between two used regions

        // A 512 request cannot fit the lone 256 hole → a new block is created.
        var big = allocator.Allocate(512);
        Assert.Equal(2, backend.TotalBlocksAllocated);
        Assert.Equal(0UL, big.Offset);
    }

    [Fact]
    public void FullBlock_TriggersNewBlock()
    {
        var backend = new FakeBackend();
        // threshold = 256; requests of 256 are NOT dedicated (256 is not > 256) and two fill a block.
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 512);

        var a = allocator.Allocate(256);
        var b = allocator.Allocate(256);
        Assert.Equal(a.Block.Id, b.Block.Id);
        Assert.Equal(1, backend.TotalBlocksAllocated);

        var c = allocator.Allocate(256); // block full → new block
        Assert.NotEqual(a.Block.Id, c.Block.Id);
        Assert.Equal(2, backend.TotalBlocksAllocated);
    }

    [Fact]
    public void OversizedRequest_GetsDedicatedExactSizeBlock()
    {
        var backend = new FakeBackend();
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 1024); // threshold 512

        var dedicated = allocator.Allocate(600); // 600 > 512 → dedicated
        Assert.Equal(0UL, dedicated.Offset);
        Assert.Contains(600UL, backend.RequestedSizes); // backend asked for exactly 600, not 1024
        Assert.DoesNotContain(1024UL, backend.RequestedSizes);

        var stats = allocator.GetStats();
        Assert.Equal(1, stats.BlockCount);
        Assert.Equal(600UL, stats.AllocatedBytes);
        Assert.Equal(600UL, stats.UsedBytes);
        Assert.Equal(1, stats.AllocationCount);
    }

    [Fact]
    public void FreeingDedicated_ReleasesWholeBlock()
    {
        var backend = new FakeBackend();
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 1024);

        var dedicated = allocator.Allocate(600);
        Assert.Equal(1, backend.LiveBlocks);

        allocator.Free(dedicated);
        Assert.Equal(0, backend.LiveBlocks);

        var stats = allocator.GetStats();
        Assert.Equal(0, stats.BlockCount);
        Assert.Equal(0UL, stats.AllocatedBytes);
    }

    [Fact]
    public void DoubleFree_Throws()
    {
        var backend = new FakeBackend();
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 1024);

        var a = allocator.Allocate(256);
        allocator.Free(a);
        Assert.Throws<InvalidOperationException>(() => allocator.Free(a));
    }

    [Fact]
    public void FreeUnknownBlock_Throws()
    {
        var backend = new FakeBackend();
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 1024);

        var bogus = new Suballocation(new MemoryBlock(999, nint.Zero), 0, 256);
        Assert.Throws<InvalidOperationException>(() => allocator.Free(bogus));
    }

    [Fact]
    public void FreeInvalidOffset_Throws()
    {
        var backend = new FakeBackend();
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 1024);

        var a = allocator.Allocate(256);
        var wrongOffset = new Suballocation(a.Block, a.Offset + 64, 256);
        Assert.Throws<InvalidOperationException>(() => allocator.Free(wrongOffset));
    }

    [Fact]
    public void Stats_AreCorrectAfterAllocFreeSequence()
    {
        var backend = new FakeBackend();
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 7, blockSize: 1024); // threshold 512

        var a = allocator.Allocate(256); // block 1
        allocator.Allocate(128);         // block 1
        var c = allocator.Allocate(256); // block 1
        allocator.Free(a);               // free 256-hole at offset 0

        var stats = allocator.GetStats();
        Assert.Equal(7u, stats.MemoryTypeIndex);
        Assert.Equal(1024UL, stats.AllocatedBytes);
        Assert.Equal(384UL, stats.UsedBytes);      // 128 + 256 remain live
        Assert.Equal(2, stats.AllocationCount);
        Assert.Equal(1, stats.BlockCount);

        // Free space = 1024 - 384 = 640, split into a 256 hole and a 384 tail hole.
        // Largest single hole is 384, so fragmentation = 1 - 384/640 = 0.4.
        Assert.Equal(384UL, stats.LargestFreeRegion);
        Assert.Equal(0.4, stats.Fragmentation, precision: 10);

        allocator.Free(c);
    }

    [Fact]
    public void MappedPointer_TracksBlockBasePlusOffset()
    {
        var backend = new FakeBackend(mapped: true);
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 1024);

        var a = allocator.Allocate(256);
        var b = allocator.Allocate(256);

        Assert.NotEqual(nint.Zero, a.MappedPointer);
        Assert.Equal(a.Block.MappedPointer + (nint)a.Offset, a.MappedPointer);
        Assert.Equal(b.Block.MappedPointer + (nint)b.Offset, b.MappedPointer);
    }

    [Fact]
    public void DeviceLocalAllocation_HasNoMappedPointer()
    {
        var backend = new FakeBackend(mapped: false);
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 1024);

        var a = allocator.Allocate(256);
        Assert.Equal(nint.Zero, a.MappedPointer);
    }

    [Fact]
    public void Dispose_ReleasesAllBlocks()
    {
        var backend = new FakeBackend();
        var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 512);

        allocator.Allocate(256);
        allocator.Allocate(256);
        allocator.Allocate(256); // forces a second block
        Assert.Equal(2, backend.LiveBlocks);

        allocator.Dispose();
        Assert.Equal(0, backend.LiveBlocks);

        // Dispose is idempotent and blocks the API afterwards.
        allocator.Dispose();
        Assert.Throws<ObjectDisposedException>(() => allocator.Allocate(16));
    }

    [Theory]
    [InlineData(0UL)]
    public void Allocate_RejectsZeroSize(ulong size)
    {
        var backend = new FakeBackend();
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 1024);
        Assert.Throws<ArgumentOutOfRangeException>(() => allocator.Allocate(size));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(3UL)]
    [InlineData(100UL)]
    public void Allocate_RejectsNonPowerOfTwoAlignment(ulong alignment)
    {
        var backend = new FakeBackend();
        using var allocator = new FreeListAllocator(backend, memoryTypeIndex: 0, blockSize: 1024);
        Assert.Throws<ArgumentOutOfRangeException>(() => allocator.Allocate(256, alignment));
    }
}
