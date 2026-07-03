namespace Agapanthe.Graphics;

/// <summary>
/// Deferred GPU resource destruction. Disposing a resource never destroys a handle
/// that a frame in flight may still use: the destroy action is enqueued with the
/// current frame index and executed once that frame is provably complete
/// (completedFrame - FramesInFlight), or all at once at shutdown after WaitIdle.
/// </summary>
public sealed class DeletionQueue
{
    private readonly Queue<(long FrameIndex, Action Destroy)> _pending = new();
    private readonly Lock _lock = new();

    /// <summary>Schedules <paramref name="destroy"/> to run once frame <paramref name="frameIndex"/> is out of flight.</summary>
    public void Enqueue(Action destroy, long frameIndex)
    {
        ArgumentNullException.ThrowIfNull(destroy);
        lock (_lock)
        {
            _pending.Enqueue((frameIndex, destroy));
        }
    }

    /// <summary>Destroys everything enqueued at or before <paramref name="completedFrameIndex"/> - FramesInFlight.</summary>
    public void Flush(long completedFrameIndex)
    {
        var threshold = completedFrameIndex - GraphicsDevice.FramesInFlight;
        lock (_lock)
        {
            while (_pending.Count > 0 && _pending.Peek().FrameIndex <= threshold)
            {
                _pending.Dequeue().Destroy();
            }
        }
    }

    /// <summary>Destroys everything immediately. Only valid after vkDeviceWaitIdle (shutdown path).</summary>
    public void FlushAll()
    {
        lock (_lock)
        {
            while (_pending.Count > 0)
            {
                _pending.Dequeue().Destroy();
            }
        }
    }
}
