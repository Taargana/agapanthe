using Agapanthe.Graphics;

namespace Agapanthe.Tests;

public class DeletionQueueTests
{
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
}
