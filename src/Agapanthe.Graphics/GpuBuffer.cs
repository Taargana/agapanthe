using System.Runtime.CompilerServices;
using Agapanthe.Core;
using Agapanthe.Graphics.Memory;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Agapanthe.Graphics;

/// <summary>
/// A GPU buffer whose memory is suballocated from the device <see cref="GpuAllocator"/> (spec §3.5)
/// with an explicit <see cref="MemoryDomain"/> (architect decision, session 2):
/// <list type="bullet">
///   <item><see cref="MemoryDomain.HostVisible"/> (default) — the backing block is persistently
///   mapped by the allocator, so <see cref="Write{T}"/> copies straight into
///   <see cref="GpuAllocation.MappedPointer"/> with no per-buffer <c>vkMapMemory</c>.</item>
///   <item><see cref="MemoryDomain.DeviceLocal"/> — GPU-only memory; <see cref="Write{T}"/> throws
///   (data must arrive through the staging upload path, M3-06) and the buffer automatically gains
///   <c>TRANSFER_DST</c> usage as the copy target.</item>
/// </list>
/// The buffer owns its <c>VkBuffer</c> handle and its suballocation, never a whole
/// <c>VkDeviceMemory</c>: the block is owned by the allocator/backend, counted once per block in the
/// <see cref="ResourceTracker"/> as "VkDeviceMemory". Disposal is deferred through the device
/// <see cref="DeletionQueue"/> without capturing any managed state (spec §3.2.5).
/// </summary>
public sealed unsafe class GpuBuffer : IDisposable
{
    private readonly GraphicsDevice _device;
    private Buffer _buffer;
    private GpuAllocation _allocation;
    private void* _mapped;
    private bool _disposed;

    public GpuBuffer(GraphicsDevice device, ulong sizeBytes, BufferUsage usage, MemoryDomain domain = MemoryDomain.HostVisible)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (sizeBytes == 0)
        {
            throw new GraphicsException("GpuBuffer size must be non-zero.");
        }

        _device = device;
        SizeBytes = sizeBytes;
        Domain = domain;
        var vk = device.Api;

        try
        {
            var bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = sizeBytes,
                Usage = ToVkUsage(usage, domain),
                SharingMode = SharingMode.Exclusive,
            };
            Buffer buffer;
            VkCheck.ThrowIfFailed(vk.CreateBuffer(device.Device, &bufferInfo, null, &buffer), "vkCreateBuffer");
            _buffer = buffer;
            ResourceTracker.Register("VkBuffer");

            vk.GetBufferMemoryRequirements(device.Device, _buffer, out var requirements);

            // Memory now comes from the shared allocator (block-level VkDeviceMemory), not a
            // per-buffer vkAllocateMemory. The block is persistently mapped by the backend, so no
            // per-buffer vkMapMemory either.
            _allocation = device.Allocator.Allocate(
                new MemoryRequirementsInfo(requirements.Size, requirements.Alignment, requirements.MemoryTypeBits),
                domain);

            VkCheck.ThrowIfFailed(
                vk.BindBufferMemory(device.Device, _buffer, _allocation.DeviceMemory, _allocation.Offset),
                "vkBindBufferMemory");

            // Host-visible: the CPU address is the block's mapped base plus this suballocation's
            // offset, already computed by the allocator. Device-local has no CPU pointer.
            _mapped = (void*)_allocation.MappedPointer;
            if (domain == MemoryDomain.HostVisible && _mapped is null)
            {
                throw new GraphicsException(
                    "Host-visible allocation is not mapped; the memory backend did not persistently map its block.");
            }
        }
        catch
        {
            DestroyNow();
            GC.SuppressFinalize(this);
            throw;
        }
    }

    ~GpuBuffer()
    {
        // Only report when a native resource was actually acquired; ctor argument-validation
        // exceptions reach the finalizer with nothing registered (audit M2, finding 1). A non-zero
        // block id in the suballocation means the allocator handed us memory.
        if (_buffer.Handle != 0 || _allocation.DeviceMemory.Handle != 0)
        {
            ResourceTracker.ReportFinalizerLeak(nameof(GpuBuffer));
        }
    }

    public ulong SizeBytes { get; }

    /// <summary>The memory domain this buffer was created in.</summary>
    public MemoryDomain Domain { get; }

    internal Buffer Handle => _buffer;

    /// <summary>
    /// Copies <paramref name="data"/> into the persistently mapped host-visible memory. Coherent
    /// memory needs no flush.
    /// </summary>
    /// <exception cref="GraphicsException">The buffer is device-local (use the staging upload path, M3-06).</exception>
    public void Write<T>(ReadOnlySpan<T> data)
        where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Domain != MemoryDomain.HostVisible)
        {
            throw new GraphicsException(
                "Write<T> is only valid on a host-visible buffer; use the staging upload path for device-local memory.");
        }

        // 64-bit multiply: a span of > 2 GiB worth of elements would overflow an int product and report a nonsense
        // size (the check below would still reject it, with an absurd message).
        var bytes = (ulong)((long)data.Length * Unsafe.SizeOf<T>());
        if (bytes > SizeBytes)
        {
            throw new GraphicsException($"Write of {bytes} bytes exceeds buffer size {SizeBytes}.");
        }

        var dst = new Span<byte>(_mapped, (int)bytes);
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(data).CopyTo(dst);
    }

    /// <summary>
    /// A writable <see cref="Span{T}"/> over the first <paramref name="count"/> elements of the
    /// persistently mapped host-visible memory. Lets a caller fill the buffer element-by-element with
    /// zero intermediate copy (e.g. compacting per-instance transforms during command recording,
    /// P3-M1). The span is valid only until the buffer is disposed; do not retain it across frames.
    /// </summary>
    /// <exception cref="GraphicsException">The buffer is device-local, or the requested range exceeds its size.</exception>
    public Span<T> MappedSpan<T>(int count)
        where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Domain != MemoryDomain.HostVisible)
        {
            throw new GraphicsException(
                "MappedSpan<T> is only valid on a host-visible buffer; device-local memory has no CPU pointer.");
        }

        var bytes = (ulong)((long)count * Unsafe.SizeOf<T>());
        if (count < 0 || bytes > SizeBytes)
        {
            throw new GraphicsException($"MappedSpan of {bytes} bytes exceeds buffer size {SizeBytes}.");
        }

        return new Span<T>(_mapped, count);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Non-capturing deferred destroy (spec §3.2.5): the raw VkBuffer handle plus the routing
        // data needed to return the suballocation travel by value in the payload, and the destructor
        // is a cached static delegate, so Dispose allocates nothing. The payload layout matches the
        // DeletionPayload documentation: Handle0 = VkBuffer, Handle1 = VkDeviceMemory (block id),
        // Handle2 = memory type index, Offset = suballocation offset. Size/MappedPointer are not
        // needed to free (the allocator identifies a region by memory-type + block + offset).
        var payload = new DeletionPayload(
            _buffer.Handle,
            _allocation.DeviceMemory.Handle,
            _allocation.MemoryTypeIndex,
            _allocation.Offset);
        _buffer = default;
        _allocation = default;
        _mapped = null;

        _device.EnqueueDestroy(DestroyDelegate, in payload);

        GC.SuppressFinalize(this);
    }

    // Allocated once per type: passing this reference on the hot path costs no allocation.
    private static readonly Action<GraphicsDevice, DeletionPayload> DestroyDelegate = DestroyDeferred;

    private static void DestroyDeferred(GraphicsDevice device, DeletionPayload payload)
    {
        var vk = device.Api;
        var deviceHandle = device.Device;

        // The block memory is owned by the allocator/backend now: no vkUnmapMemory, no vkFreeMemory
        // here. We destroy only the buffer handle and hand the suballocation back to the allocator.
        var buffer = new Buffer(payload.Handle0);
        if (buffer.Handle != 0)
        {
            vk.DestroyBuffer(deviceHandle, buffer, null);
            ResourceTracker.Unregister("VkBuffer");
        }

        if (payload.Handle1 != 0)
        {
            var allocation = ReconstructAllocation(payload);
            device.Allocator.Free(in allocation);
        }
    }

    private void DestroyNow()
    {
        var vk = _device.Api;

        if (_buffer.Handle != 0)
        {
            vk.DestroyBuffer(_device.Device, _buffer, null);
            _buffer = default;
            ResourceTracker.Unregister("VkBuffer");
        }

        // Return the suballocation immediately: a construction failure is never mid-flight, so there
        // is nothing to defer. The block itself lives on inside the allocator.
        if (_allocation.DeviceMemory.Handle != 0)
        {
            _device.Allocator.Free(in _allocation);
            _allocation = default;
        }

        _mapped = null;
    }

    /// <summary>
    /// Rebuilds the <see cref="GpuAllocation"/> needed by <see cref="GpuAllocator.Free"/> from the
    /// deletion payload. The free-list identifies a region purely by (memory-type index, block id,
    /// offset), so <c>Size</c> and <c>MappedPointer</c> are irrelevant here and left at zero.
    /// </summary>
    private static GpuAllocation ReconstructAllocation(in DeletionPayload payload)
    {
        var block = new MemoryBlock(payload.Handle1, nint.Zero);
        var suballocation = new Suballocation(block, payload.Offset, 0);
        // Domain is not consulted by Free; any value is fine.
        return new GpuAllocation(suballocation, (uint)payload.Handle2, MemoryDomain.DeviceLocal);
    }

    private static BufferUsageFlags ToVkUsage(BufferUsage usage, MemoryDomain domain)
    {
        var flags = BufferUsageFlags.None;
        if ((usage & BufferUsage.Vertex) != 0)
        {
            flags |= BufferUsageFlags.VertexBufferBit;
        }

        if ((usage & BufferUsage.Index) != 0)
        {
            flags |= BufferUsageFlags.IndexBufferBit;
        }

        if ((usage & BufferUsage.Uniform) != 0)
        {
            flags |= BufferUsageFlags.UniformBufferBit;
        }

        if ((usage & BufferUsage.Storage) != 0)
        {
            flags |= BufferUsageFlags.StorageBufferBit;
        }

        if ((usage & BufferUsage.Indirect) != 0)
        {
            flags |= BufferUsageFlags.IndirectBufferBit;
        }

        if ((usage & BufferUsage.TransferSrc) != 0)
        {
            flags |= BufferUsageFlags.TransferSrcBit;
        }

        if (flags == BufferUsageFlags.None)
        {
            throw new GraphicsException("BufferUsage must specify at least one usage.");
        }

        // Device-local buffers can only be populated by a copy from a staging buffer (M3-06), so
        // they are always a transfer destination.
        if (domain == MemoryDomain.DeviceLocal)
        {
            flags |= BufferUsageFlags.TransferDstBit;
        }

        return flags;
    }
}
