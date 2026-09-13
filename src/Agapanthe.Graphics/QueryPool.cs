using Agapanthe.Core;
using Silk.NET.Vulkan;
using VkQueryPool = Silk.NET.Vulkan.QueryPool;

namespace Agapanthe.Graphics;

/// <summary>
/// A GPU timestamp query pool (UI-3): wraps a <c>VkQueryPool</c> of type <see cref="QueryType.Timestamp"/>,
/// sized once at construction. Follows the module resource pattern (registered in the
/// <see cref="ResourceTracker"/>, finalizer that only reports a leak when a handle was actually
/// acquired, deferred disposal through the device <see cref="DeletionQueue"/> with a non-capturing
/// payload) — same shape as <see cref="Sampler"/>.
/// <para>
/// Not swapchain-size-dependent, so it is created once (device/renderer lifetime) rather than
/// recreated on resize.
/// </para>
/// </summary>
public sealed unsafe class QueryPool : IDisposable
{
    private readonly GraphicsDevice _device;
    private VkQueryPool _handle;
    private bool _disposed;

    /// <summary>Creates a timestamp query pool with <paramref name="queryCount"/> slots.</summary>
    public QueryPool(GraphicsDevice device, uint queryCount)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfZero(queryCount);

        _device = device;
        QueryCount = queryCount;

        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = queryCount,
        };

        VkQueryPool handle;
        VkCheck.ThrowIfFailed(device.Api.CreateQueryPool(device.Device, &info, null, &handle), "vkCreateQueryPool");
        _handle = handle;
        ResourceTracker.Register("VkQueryPool");
    }

    ~QueryPool()
    {
        // Only report when a native handle was actually acquired; ctor argument-validation exceptions
        // reach the finalizer with nothing registered (audit M2, finding 1 — the pattern every module
        // resource in this project follows).
        if (_handle.Handle != 0)
        {
            ResourceTracker.ReportFinalizerLeak(nameof(QueryPool));
        }
    }

    /// <summary>Number of query slots this pool was created with.</summary>
    public uint QueryCount { get; }

    internal VkQueryPool Handle => _handle;

    /// <summary>
    /// Reads <paramref name="count"/> consecutive timestamp query results starting at
    /// <paramref name="firstQuery"/>, NEVER BLOCKING (UI-3, D4 — no <c>WAIT_BIT</c>): each query's raw
    /// 64-bit tick value and its availability flag land in <paramref name="destination"/> (length must be
    /// at least <c>count * 2</c> — value then availability, per query, in order). A query not yet written
    /// by the GPU reports availability <c>0</c> rather than the host waiting for it; <c>vkGetQueryPoolResults</c>
    /// is also permitted to return <c>VK_NOT_READY</c> in that case (not an error, unlike every other
    /// non-success code) — treated identically to availability 0, since the written values are equally
    /// unusable either way.
    /// </summary>
    public void ReadResultsNonBlocking(uint firstQuery, uint count, Span<ulong> destination)
    {
        // Audit finding (csharp-lowlevel, 🟠): the initial version had none of these 4 guards — a
        // disposed pool, an out-of-range query range, a zero count, or (on a 32-bit-unsafe path) a
        // huge count could each reach the driver with an invalid call (VUID-vkGetQueryPoolResults-*),
        // or in the huge-count case, silently overflow an `int`/`nuint` cast into an undersized
        // allocation the driver would then write past. All arithmetic below stays in `ulong`/`uint`
        // until the final, already-range-checked cast.
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((ulong)firstQuery + count, QueryCount);
        ArgumentOutOfRangeException.ThrowIfLessThan((ulong)destination.Length, (ulong)count * 2);

        var stride = (ulong)(2 * sizeof(ulong));
        var dataSize = (nuint)((ulong)count * 2 * (ulong)sizeof(ulong));
        Result result;
        fixed (ulong* p = destination)
        {
            result = _device.Api.GetQueryPoolResults(
                _device.Device, _handle, firstQuery, count, dataSize, p, stride,
                QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit);
        }

        if (result != Result.Success && result != Result.NotReady)
        {
            VkCheck.ThrowIfFailed(result, "vkGetQueryPoolResults");
        }
    }

    /// <summary>
    /// Deferred disposal: the pool is destroyed once the frame that used it leaves flight. The payload
    /// carries only the raw handle and the destructor is a cached static delegate, so this allocates
    /// nothing.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var payload = new DeletionPayload(_handle.Handle);
        _handle = default;
        _device.EnqueueDestroy(DestroyDelegate, in payload);
        GC.SuppressFinalize(this);
    }

    // Allocated once per type: passing this reference on the deferred path costs no allocation.
    private static readonly Action<GraphicsDevice, DeletionPayload> DestroyDelegate = DestroyDeferred;

    private static void DestroyDeferred(GraphicsDevice device, DeletionPayload payload)
    {
        var pool = new VkQueryPool(payload.Handle0);
        if (pool.Handle != 0)
        {
            device.Api.DestroyQueryPool(device.Device, pool, null);
            ResourceTracker.Unregister("VkQueryPool");
        }
    }
}
