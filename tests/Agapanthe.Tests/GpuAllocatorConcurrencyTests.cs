using System.Collections.Concurrent;
using Agapanthe.Graphics.Memory;

namespace Agapanthe.Tests;

/// <summary>
/// GraphicsDevice thread-safety spec, D3: <see cref="GpuAllocator"/> gets a real lock on every method
/// touching shared state. Drives it over the same GPU-free mock backend as <see cref="GpuAllocatorTests"/>,
/// but with two threads sharing <b>one common memory-type index</b> (round-3 finding: that shared-type
/// contention on a single, genuinely non-thread-safe <see cref="FreeListAllocator"/> — not the
/// <c>Dictionary</c>'s own one-or-two-insertion growth — is what actually discriminates locked from
/// unlocked). Verified by mutation (temporarily removing the lock reproduces a failure here) before this
/// task was closed; the lock is present in the code this test now runs against.
/// </summary>
public class GpuAllocatorConcurrencyTests
{
    private const uint SharedMemoryType = 0;

    private static uint ResolveToSharedType(uint memoryTypeBits, MemoryDomain domain) => SharedMemoryType;

    /// <summary>Same shape as <see cref="GpuAllocatorTests"/>'s FakeBackend — sequential ids, live-block counting.</summary>
    private sealed class FakeBackend : IMemoryBackend
    {
        private ulong _nextId = 1;

        public MemoryBlock AllocateBlock(uint memoryTypeIndex, ulong size) => new(_nextId++, nint.Zero);

        public void FreeBlock(MemoryBlock block)
        {
        }
    }

    [Fact]
    public void ConcurrentAllocateFreeGetStats_OnASharedMemoryType_NeverDoubleIssuesAnOffset_AndSettlesToZero()
    {
        var backend = new FakeBackend();
        using var allocator = new GpuAllocator(backend, ResolveToSharedType);

        const int iterationsPerThread = 5000;
        const ulong allocSize = 64;
        var occupied = new ConcurrentDictionary<ulong, byte>();
        var failures = new ConcurrentBag<string>();

        void Worker()
        {
            for (var i = 0; i < iterationsPerThread; i++)
            {
                try
                {
                    var alloc = allocator.Allocate(new MemoryRequirementsInfo(allocSize, 1, 0xFFFF_FFFF), MemoryDomain.DeviceLocal);
                    if (!occupied.TryAdd(alloc.Offset, 0))
                    {
                        failures.Add($"offset {alloc.Offset} handed out to two live allocations at once");
                        continue;
                    }

                    // D3 also covers GetStats — touch it concurrently with the other thread's Allocate/Free.
                    _ = allocator.GetStats();

                    // Bookkeeping order matters: remove from `occupied` BEFORE the real Free() call, not
                    // after. Freeing first would open a window where the allocator has already made this
                    // offset available for reuse (correctly) while this test's own dictionary still claims
                    // it live — a legitimate concurrent re-issue would then look like a false "double
                    // issue" failure, a race in the harness rather than in GpuAllocator itself.
                    occupied.TryRemove(alloc.Offset, out _);
                    allocator.Free(alloc);
                }
                catch (Exception ex)
                {
                    failures.Add($"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        var t1 = new Thread(Worker);
        var t2 = new Thread(Worker);
        t1.Start();
        t2.Start();
        t1.Join();
        t2.Join();

        Assert.Empty(failures);

        var stats = allocator.GetStats();
        var shared = Assert.Single(stats, s => s.MemoryTypeIndex == SharedMemoryType);
        Assert.Equal(0UL, shared.UsedBytes);
        Assert.Equal(0, shared.AllocationCount);
    }
}
