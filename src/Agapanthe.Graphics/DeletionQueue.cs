namespace Agapanthe.Graphics;

/// <summary>
/// Raw handle payload for a deferred destruction (spec §3.2.5). Carries GPU handles by value
/// (Vulkan non-dispatchable handles are 64-bit) so a destroy can be scheduled without capturing
/// any managed state. Field meaning is destructor-defined; e.g. a buffer destroy uses
/// <see cref="Handle0"/> = VkBuffer, <see cref="Handle1"/> = VkDeviceMemory, and (from M3-04)
/// <see cref="Offset"/> = suballocation offset inside a shared block.
/// </summary>
public readonly struct DeletionPayload
{
    /// <summary>Creates a payload from raw 64-bit handles/offsets.</summary>
    public DeletionPayload(ulong handle0, ulong handle1 = 0, ulong handle2 = 0, ulong offset = 0)
    {
        Handle0 = handle0;
        Handle1 = handle1;
        Handle2 = handle2;
        Offset = offset;
    }

    /// <summary>Primary handle (e.g. VkBuffer or VkImage).</summary>
    public ulong Handle0 { get; }

    /// <summary>Secondary handle (e.g. VkDeviceMemory or VkImageView).</summary>
    public ulong Handle1 { get; }

    /// <summary>Tertiary handle (reserved for images: view/sampler).</summary>
    public ulong Handle2 { get; }

    /// <summary>Suballocation offset inside a shared memory block (M3-04). Zero for dedicated allocations.</summary>
    public ulong Offset { get; }
}

/// <summary>
/// Deferred GPU resource destruction. Disposing a resource never destroys a handle
/// that a frame in flight may still use: the destroy is enqueued with the current frame index
/// and executed once that frame is provably complete (completedFrame - FramesInFlight), or all
/// at once at shutdown after WaitIdle.
/// <para>
/// The primary path is non-capturing (spec §3.2.5, zero managed allocation per Dispose): callers
/// hand over a <b>cached static</b> <see cref="Action{GraphicsDevice, DeletionPayload}"/> plus a
/// value-type <see cref="DeletionPayload"/>. The delegate object is allocated once per resource
/// type, so <see cref="Enqueue(GraphicsDevice, Action{GraphicsDevice, DeletionPayload}, in DeletionPayload, long)"/>
/// allocates nothing (the internal queue grows amortized). The <see cref="Enqueue(Action, long)"/>
/// overload allocates a closure and exists only for rare/shutdown teardown — never call it on the hot path.
/// </para>
/// </summary>
public sealed class DeletionQueue
{
    private readonly Queue<Entry> _pending = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Non-capturing deferred destroy (spec §3.2.5). <paramref name="destructor"/> must be a cached
    /// static delegate (allocated once) so nothing is allocated per call; it receives
    /// <paramref name="device"/> and a copy of <paramref name="payload"/> when the frame leaves flight.
    /// </summary>
    public void Enqueue(
        GraphicsDevice device,
        Action<GraphicsDevice, DeletionPayload> destructor,
        in DeletionPayload payload,
        long frameIndex)
    {
        ArgumentNullException.ThrowIfNull(destructor);
        lock (_lock)
        {
            _pending.Enqueue(new Entry(frameIndex, device, destructor, payload));
        }
    }

    /// <summary>
    /// Schedules <paramref name="destroy"/> to run once frame <paramref name="frameIndex"/> is out
    /// of flight. Allocates a closure/delegate — reserved for rare or shutdown-only teardown.
    /// Do not use on the per-frame hot path; prefer the non-capturing overload.
    /// </summary>
    public void Enqueue(Action destroy, long frameIndex)
    {
        ArgumentNullException.ThrowIfNull(destroy);
        lock (_lock)
        {
            _pending.Enqueue(new Entry(frameIndex, destroy));
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
                Execute(_pending.Dequeue());
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
                Execute(_pending.Dequeue());
            }
        }
    }

    private static void Execute(in Entry entry)
    {
        if (entry.LegacyDestroy is not null)
        {
            entry.LegacyDestroy();
        }
        else
        {
            // Device may be null only in isolated unit tests whose destructor ignores it;
            // production always enqueues via GraphicsDevice with a non-null this.
            entry.Destructor!(entry.Device!, entry.Payload);
        }
    }

    private readonly struct Entry
    {
        public Entry(
            long frameIndex,
            GraphicsDevice? device,
            Action<GraphicsDevice, DeletionPayload> destructor,
            in DeletionPayload payload)
        {
            FrameIndex = frameIndex;
            Device = device;
            Destructor = destructor;
            Payload = payload;
            LegacyDestroy = null;
        }

        public Entry(long frameIndex, Action legacyDestroy)
        {
            FrameIndex = frameIndex;
            Device = null;
            Destructor = null;
            Payload = default;
            LegacyDestroy = legacyDestroy;
        }

        public long FrameIndex { get; }

        public GraphicsDevice? Device { get; }

        public Action<GraphicsDevice, DeletionPayload>? Destructor { get; }

        public DeletionPayload Payload { get; }

        public Action? LegacyDestroy { get; }
    }
}
