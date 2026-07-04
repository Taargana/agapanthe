namespace Agapanthe.Graphics.Memory;

/// <summary>
/// Result of a suballocation: the opaque <see cref="Block"/> it lives in, its aligned
/// <see cref="Offset"/> within that block and its <see cref="Size"/>. For host-visible memory,
/// <see cref="MappedPointer"/> gives the CPU address of this region directly.
/// </summary>
/// <param name="Block">The backing block (hand this value back to <see cref="FreeListAllocator.Free"/>).</param>
/// <param name="Offset">Aligned byte offset within the block.</param>
/// <param name="Size">Byte size of the region (exactly the requested size).</param>
public readonly record struct Suballocation(MemoryBlock Block, ulong Offset, ulong Size)
{
    /// <summary>
    /// CPU address of this region when the block is mapped (host-visible), otherwise
    /// <see cref="nint.Zero"/>.
    /// </summary>
    public nint MappedPointer =>
        Block.MappedPointer == nint.Zero ? nint.Zero : Block.MappedPointer + (nint)Offset;
}

/// <summary>
/// Debug statistics for one <see cref="FreeListAllocator"/> (i.e. one memory type). All figures
/// are computed on demand from the live block list.
/// </summary>
/// <param name="MemoryTypeIndex">The Vulkan memory type this allocator serves.</param>
/// <param name="AllocatedBytes">Total bytes obtained from the backend (sum of block sizes).</param>
/// <param name="UsedBytes">Total bytes handed to callers (sum of live suballocation sizes).</param>
/// <param name="BlockCount">Number of live backend blocks (suballocated + dedicated).</param>
/// <param name="AllocationCount">Number of live suballocations.</param>
/// <param name="LargestFreeRegion">Size of the biggest single free hole across all blocks.</param>
/// <param name="Fragmentation">
/// <c>1 - LargestFreeRegion / (AllocatedBytes - UsedBytes)</c>, in <c>[0, 1]</c>; <c>0</c> when there
/// is no free space. Near 0 means free space is contiguous; near 1 means it is scattered.
/// </param>
public readonly record struct AllocationStats(
    uint MemoryTypeIndex,
    ulong AllocatedBytes,
    ulong UsedBytes,
    int BlockCount,
    int AllocationCount,
    ulong LargestFreeRegion,
    double Fragmentation);

/// <summary>
/// Pure suballocation logic for a single Vulkan memory type (spec §3.5): large backend blocks
/// (default 64 MiB) carved into aligned regions via a per-block free list, with neighbour
/// coalescing on free and dedicated blocks for oversized requests. Contains no Vulkan calls and no
/// Silk.NET references — all block I/O goes through <see cref="IMemoryBackend"/> so the logic is
/// unit-testable without a GPU.
/// </summary>
/// <remarks>
/// <para>
/// The first block is created lazily on the first request; a new block is added only when no
/// existing block can serve a request. Requests larger than <see cref="DedicatedThreshold"/> get
/// their own exact-size block, freed whole on <see cref="Free"/>.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> Phase-1 rendering is single-threaded; external synchronisation is the
/// caller's responsibility if that changes. Empty non-dedicated blocks are retained (block
/// reclamation / defragmentation is deferred to phase 2); everything is released on
/// <see cref="Dispose"/>.
/// </para>
/// </remarks>
public sealed class FreeListAllocator : IDisposable
{
    /// <summary>Default block size handed to the backend when suballocating (64 MiB).</summary>
    public const ulong DefaultBlockSize = 64UL * 1024 * 1024;

    private readonly IMemoryBackend _backend;
    private readonly uint _memoryTypeIndex;
    private readonly List<Block> _blocks = new();
    private readonly Dictionary<ulong, Block> _blocksById = new();
    private bool _disposed;

    /// <summary>Creates an allocator serving a single memory type.</summary>
    /// <param name="backend">Source of opaque memory blocks (Vulkan in prod, mock in tests).</param>
    /// <param name="memoryTypeIndex">The Vulkan memory type this instance serves.</param>
    /// <param name="blockSize">Backend block size for suballocation (default 64 MiB, 64–256 MiB is the intended range).</param>
    /// <param name="dedicatedThreshold">
    /// Requests strictly larger than this get a dedicated exact-size block. Defaults to half the
    /// block size, guaranteeing every suballocated request fits in a fresh block.
    /// </param>
    public FreeListAllocator(
        IMemoryBackend backend,
        uint memoryTypeIndex,
        ulong blockSize = DefaultBlockSize,
        ulong? dedicatedThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (blockSize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be positive.");
        }

        _backend = backend;
        _memoryTypeIndex = memoryTypeIndex;
        BlockSize = blockSize;
        DedicatedThreshold = dedicatedThreshold ?? blockSize / 2;
        if (DedicatedThreshold > blockSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dedicatedThreshold),
                "Dedicated threshold must not exceed block size, otherwise a request could exceed a fresh block.");
        }
    }

    /// <summary>The Vulkan memory type this allocator serves.</summary>
    public uint MemoryTypeIndex => _memoryTypeIndex;

    /// <summary>Backend block size used for suballocation.</summary>
    public ulong BlockSize { get; }

    /// <summary>Requests strictly larger than this receive a dedicated exact-size block.</summary>
    public ulong DedicatedThreshold { get; }

    /// <summary>
    /// Reserves <paramref name="size"/> bytes aligned to <paramref name="alignment"/> (a power of
    /// two, as in <c>VkMemoryRequirements.alignment</c>). Reuses free space in existing blocks
    /// (first-fit); adds a new block, or a dedicated block for oversized requests, as needed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is 0 or <paramref name="alignment"/> is not a power of two.</exception>
    public Suballocation Allocate(ulong size, ulong alignment = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (size == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Allocation size must be positive.");
        }

        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "Alignment must be a power of two.");
        }

        // Oversized request → dedicated exact-size block (freed whole on Free).
        if (size > DedicatedThreshold)
        {
            var dedicated = CreateBlock(size, isDedicated: true);
            // A dedicated block is a single used region covering the whole block.
            dedicated.Regions.Add(new Region(0, size, isFree: false));
            return new Suballocation(dedicated.Handle, 0, size);
        }

        // First-fit across existing suballocatable blocks.
        foreach (var block in _blocks)
        {
            if (!block.IsDedicated && TryAllocateInBlock(block, size, alignment, out var sub))
            {
                return sub;
            }
        }

        // No block could serve it → grow. size <= DedicatedThreshold <= BlockSize, so this fits.
        var fresh = CreateBlock(BlockSize, isDedicated: false);
        fresh.Regions.Add(new Region(0, BlockSize, isFree: true));
        if (!TryAllocateInBlock(fresh, size, alignment, out var freshSub))
        {
            // Unreachable given the invariants above; guards against a future BlockSize change.
            throw new InvalidOperationException("Fresh block could not satisfy a within-threshold allocation.");
        }

        return freshSub;
    }

    /// <summary>
    /// Releases a suballocation. For a dedicated block the whole block is freed; otherwise the
    /// region is marked free and coalesced with any free neighbours.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The allocation is unknown to this allocator, or its region was already freed (double free).
    /// </exception>
    public void Free(Suballocation allocation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_blocksById.TryGetValue(allocation.Block.Id, out var block))
        {
            throw new InvalidOperationException(
                $"Free of an unknown block (id {allocation.Block.Id}); it was never allocated here or already released.");
        }

        if (block.IsDedicated)
        {
            RemoveBlock(block);
            return;
        }

        int index = block.Regions.FindIndex(r => r.Offset == allocation.Offset && !r.IsFree);
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"Free of an invalid or already-freed region at offset {allocation.Offset} in block {allocation.Block.Id}.");
        }

        block.Regions[index].IsFree = true;

        // Coalesce with the following region first (indices before it stay valid), then the preceding one.
        if (index + 1 < block.Regions.Count && block.Regions[index + 1].IsFree)
        {
            block.Regions[index].Size += block.Regions[index + 1].Size;
            block.Regions.RemoveAt(index + 1);
        }

        if (index > 0 && block.Regions[index - 1].IsFree)
        {
            block.Regions[index - 1].Size += block.Regions[index].Size;
            block.Regions.RemoveAt(index);
        }
    }

    /// <summary>Computes current statistics for this memory type (debug/diagnostics).</summary>
    public AllocationStats GetStats()
    {
        ulong allocated = 0;
        ulong used = 0;
        int allocationCount = 0;
        ulong largestFree = 0;

        foreach (var block in _blocks)
        {
            allocated += block.Size;
            foreach (var region in block.Regions)
            {
                if (region.IsFree)
                {
                    if (region.Size > largestFree)
                    {
                        largestFree = region.Size;
                    }
                }
                else
                {
                    used += region.Size;
                    allocationCount++;
                }
            }
        }

        ulong totalFree = allocated - used;
        double fragmentation = totalFree == 0 ? 0.0 : 1.0 - ((double)largestFree / totalFree);

        return new AllocationStats(
            _memoryTypeIndex,
            allocated,
            used,
            _blocks.Count,
            allocationCount,
            largestFree,
            fragmentation);
    }

    /// <summary>Releases every backend block still held. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var block in _blocks)
        {
            _backend.FreeBlock(block.Handle);
        }

        _blocks.Clear();
        _blocksById.Clear();
    }

    private bool TryAllocateInBlock(Block block, ulong size, ulong alignment, out Suballocation result)
    {
        for (int i = 0; i < block.Regions.Count; i++)
        {
            var region = block.Regions[i];
            if (!region.IsFree)
            {
                continue;
            }

            ulong alignedOffset = AlignUp(region.Offset, alignment);
            ulong padding = alignedOffset - region.Offset;
            ulong regionEnd = region.Offset + region.Size;
            if (alignedOffset + size > regionEnd)
            {
                continue;
            }

            // Rewrite this free region as: [leading padding?] + [used] + [trailing remainder?].
            ulong remainderStart = alignedOffset + size;
            ulong remainder = regionEnd - remainderStart;

            block.Regions.RemoveAt(i);
            int insert = i;
            if (padding > 0)
            {
                block.Regions.Insert(insert++, new Region(region.Offset, padding, isFree: true));
            }

            block.Regions.Insert(insert++, new Region(alignedOffset, size, isFree: false));
            if (remainder > 0)
            {
                block.Regions.Insert(insert, new Region(remainderStart, remainder, isFree: true));
            }

            result = new Suballocation(block.Handle, alignedOffset, size);
            return true;
        }

        result = default;
        return false;
    }

    private Block CreateBlock(ulong size, bool isDedicated)
    {
        MemoryBlock handle = _backend.AllocateBlock(_memoryTypeIndex, size);
        var block = new Block(handle, size, isDedicated);
        _blocks.Add(block);
        _blocksById.Add(handle.Id, block);
        return block;
    }

    private void RemoveBlock(Block block)
    {
        _blocks.Remove(block);
        _blocksById.Remove(block.Handle.Id);
        _backend.FreeBlock(block.Handle);
    }

    private static ulong AlignUp(ulong value, ulong alignment) =>
        (value + alignment - 1) & ~(alignment - 1);

    /// <summary>A contiguous span within a block. Regions tile <c>[0, block.Size)</c> with no gaps.</summary>
    private sealed class Region(ulong offset, ulong size, bool isFree)
    {
        public ulong Offset { get; } = offset;
        public ulong Size { get; set; } = size;
        public bool IsFree { get; set; } = isFree;
    }

    private sealed class Block(MemoryBlock handle, ulong size, bool isDedicated)
    {
        public MemoryBlock Handle { get; } = handle;
        public ulong Size { get; } = size;
        public bool IsDedicated { get; } = isDedicated;

        /// <summary>Regions tiling this block, sorted ascending by offset.</summary>
        public List<Region> Regions { get; } = new();
    }
}
