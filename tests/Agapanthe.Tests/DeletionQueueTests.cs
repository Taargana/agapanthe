using Agapanthe.Graphics;

namespace Agapanthe.Tests;

public class DeletionQueueTests
{
    // ---- Legacy Action overload (shutdown/rare path) still honoured ----

    [Fact]
    public void Enqueue_NotDestroyedUntilFramesInFlightElapsed()
    {
        var queue = new DeletionQueue();
        var destroyed = false;
        // Resource disposed during frame 0.
        queue.Enqueue(() => destroyed = true, frameIndex: 0);

        // Flushing while frame 0 could still be in flight must NOT destroy it.
        queue.Flush(completedFrameIndex: 0);
        Assert.False(destroyed);
        queue.Flush(completedFrameIndex: GraphicsDevice.FramesInFlight - 1);
        Assert.False(destroyed);

        // Once the counter reaches N + FramesInFlight, frame 0 is provably done.
        queue.Flush(completedFrameIndex: GraphicsDevice.FramesInFlight);
        Assert.True(destroyed);
    }

    [Fact]
    public void FlushAll_DestroysEverythingRegardlessOfFrame()
    {
        var queue = new DeletionQueue();
        var count = 0;
        queue.Enqueue(() => count++, frameIndex: 100);
        queue.Enqueue(() => count++, frameIndex: 200);

        queue.FlushAll();
        Assert.Equal(2, count);
    }

    [Fact]
    public void Flush_DestroysInEnqueueOrder()
    {
        var queue = new DeletionQueue();
        var order = new List<int>();
        queue.Enqueue(() => order.Add(1), frameIndex: 0);
        queue.Enqueue(() => order.Add(2), frameIndex: 0);

        queue.Flush(completedFrameIndex: GraphicsDevice.FramesInFlight);
        Assert.Equal(new[] { 1, 2 }, order);
    }

    // ---- Non-capturing struct overload (spec §3.2.5, per-frame hot path) ----
    // The destructor receives the GraphicsDevice, but these unit tests ignore it (no GPU),
    // so passing null! is intentional and never dereferenced.

    [Fact]
    public void EnqueueNonCapturing_NotDestroyedUntilFramesInFlightElapsed()
    {
        var queue = new DeletionQueue();
        var destroyed = false;
        queue.Enqueue(null!, (_, _) => destroyed = true, new DeletionPayload(1), frameIndex: 0);

        queue.Flush(completedFrameIndex: 0);
        Assert.False(destroyed);
        queue.Flush(completedFrameIndex: GraphicsDevice.FramesInFlight - 1);
        Assert.False(destroyed);

        queue.Flush(completedFrameIndex: GraphicsDevice.FramesInFlight);
        Assert.True(destroyed);
    }

    [Fact]
    public void FlushNonCapturing_DestroysInEnqueueOrder_WithPayloadHandles()
    {
        var queue = new DeletionQueue();
        var order = new List<ulong>();
        void Record(GraphicsDevice device, DeletionPayload p) => order.Add(p.Handle0);

        queue.Enqueue(null!, Record, new DeletionPayload(handle0: 10, handle1: 11), frameIndex: 0);
        queue.Enqueue(null!, Record, new DeletionPayload(handle0: 20, handle1: 21), frameIndex: 0);

        queue.Flush(completedFrameIndex: GraphicsDevice.FramesInFlight);
        Assert.Equal(new ulong[] { 10, 20 }, order);
    }

    [Fact]
    public void FlushAllNonCapturing_DrainsEverything_PayloadIntact()
    {
        var queue = new DeletionQueue();
        var seen = new List<(ulong H0, ulong H1)>();
        void Record(GraphicsDevice device, DeletionPayload p) => seen.Add((p.Handle0, p.Handle1));

        queue.Enqueue(null!, Record, new DeletionPayload(handle0: 1, handle1: 2), frameIndex: 100);
        queue.Enqueue(null!, Record, new DeletionPayload(handle0: 3, handle1: 4), frameIndex: 200);

        queue.FlushAll();
        Assert.Equal(new[] { (1UL, 2UL), (3UL, 4UL) }, seen);
    }

    [Fact]
    public void MixedPaths_PreserveGlobalFifoOrder()
    {
        var queue = new DeletionQueue();
        var order = new List<int>();
        queue.Enqueue(() => order.Add(1), frameIndex: 0);
        queue.Enqueue(null!, (_, p) => order.Add((int)p.Handle0), new DeletionPayload(2), frameIndex: 0);
        queue.Enqueue(() => order.Add(3), frameIndex: 0);

        queue.FlushAll();
        Assert.Equal(new[] { 1, 2, 3 }, order);
    }

    // A cached static delegate — allocated exactly once — is the contract that makes the
    // non-capturing enqueue allocation-free (spec §3.2.5).
    private static readonly List<ulong> ZeroAllocSink = new();
    private static readonly Action<GraphicsDevice, DeletionPayload> CachedDestructor =
        static (_, p) => ZeroAllocSink.Add(p.Handle0);

    [Fact]
    public void EnqueueNonCapturing_IsAllocationFree_AfterCapacityWarmup()
    {
        var queue = new DeletionQueue();
        const int count = 1024;

        // Warm up: grow the internal queue array to its steady-state capacity and quick-JIT the
        // enqueue path, so the measured loop reflects only per-call allocation (must be zero).
        for (var i = 0; i < count; i++)
        {
            queue.Enqueue(null!, CachedDestructor, new DeletionPayload((ulong)i), frameIndex: long.MaxValue);
        }

        queue.FlushAll(); // drains entries but keeps the array capacity

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < count; i++)
        {
            queue.Enqueue(null!, CachedDestructor, new DeletionPayload((ulong)i), frameIndex: long.MaxValue);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"Expected zero allocation per Enqueue, observed {allocated} bytes over {count} calls.");
    }
}
