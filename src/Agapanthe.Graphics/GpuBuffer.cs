using System.Runtime.CompilerServices;
using Agapanthe.Core;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Agapanthe.Graphics;

/// <summary>
/// A GPU buffer backed by host-visible, host-coherent memory kept persistently mapped.
/// This is the simple M2 allocation strategy — one VkDeviceMemory per buffer, writable from
/// the CPU without staging. The device-local buffer + suballocating GpuAllocator arrives in M3.
/// Disposal is deferred through the device DeletionQueue (spec §3.2.1).
/// </summary>
public sealed unsafe class GpuBuffer : IDisposable
{
    private readonly GraphicsDevice _device;
    private Buffer _buffer;
    private DeviceMemory _memory;
    private void* _mapped;
    private bool _disposed;

    public GpuBuffer(GraphicsDevice device, ulong sizeBytes, BufferUsage usage)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (sizeBytes == 0)
        {
            throw new GraphicsException("GpuBuffer size must be non-zero.");
        }

        _device = device;
        SizeBytes = sizeBytes;
        var vk = device.Api;

        try
        {
            var bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = sizeBytes,
                Usage = ToVkUsage(usage),
                SharingMode = SharingMode.Exclusive,
            };
            Buffer buffer;
            VkCheck.ThrowIfFailed(vk.CreateBuffer(device.Device, &bufferInfo, null, &buffer), "vkCreateBuffer");
            _buffer = buffer;
            ResourceTracker.Register("VkBuffer");

            vk.GetBufferMemoryRequirements(device.Device, _buffer, out var requirements);
            var memoryType = device.FindMemoryType(
                requirements.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = memoryType,
            };
            DeviceMemory memory;
            VkCheck.ThrowIfFailed(vk.AllocateMemory(device.Device, &allocInfo, null, &memory), "vkAllocateMemory");
            _memory = memory;
            ResourceTracker.Register("VkDeviceMemory");

            VkCheck.ThrowIfFailed(vk.BindBufferMemory(device.Device, _buffer, _memory, 0), "vkBindBufferMemory");

            void* mapped;
            VkCheck.ThrowIfFailed(vk.MapMemory(device.Device, _memory, 0, sizeBytes, 0, &mapped), "vkMapMemory");
            _mapped = mapped;
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
        // Only report when a native handle was actually acquired; ctor argument-validation
        // exceptions reach the finalizer with nothing registered (audit M2, finding 1).
        if (_buffer.Handle != 0 || _memory.Handle != 0)
        {
            ResourceTracker.ReportFinalizerLeak(nameof(GpuBuffer));
        }
    }

    public ulong SizeBytes { get; }

    internal Buffer Handle => _buffer;

    /// <summary>Copies <paramref name="data"/> into the mapped memory. Coherent memory needs no flush.</summary>
    public void Write<T>(ReadOnlySpan<T> data)
        where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bytes = (ulong)(data.Length * Unsafe.SizeOf<T>());
        if (bytes > SizeBytes)
        {
            throw new GraphicsException($"Write of {bytes} bytes exceeds buffer size {SizeBytes}.");
        }

        var dst = new Span<byte>(_mapped, (int)bytes);
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(data).CopyTo(dst);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Non-capturing deferred destroy (spec §3.2.5): raw handles travel by value in the payload
        // and the destructor is a cached static delegate, so Dispose allocates nothing. The destroy
        // runs once this frame leaves flight (spec §3.2.1).
        var payload = new DeletionPayload(_buffer.Handle, _memory.Handle);
        _buffer = default;
        _memory = default;
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
        var buffer = new Buffer(payload.Handle0);
        var memory = new DeviceMemory(payload.Handle1);

        // Identical order/guards to the former inline path: unmap → destroy buffer → free memory.
        if (memory.Handle != 0)
        {
            vk.UnmapMemory(deviceHandle, memory);
        }

        if (buffer.Handle != 0)
        {
            vk.DestroyBuffer(deviceHandle, buffer, null);
            ResourceTracker.Unregister("VkBuffer");
        }

        if (memory.Handle != 0)
        {
            vk.FreeMemory(deviceHandle, memory, null);
            ResourceTracker.Unregister("VkDeviceMemory");
        }
    }

    private void DestroyNow()
    {
        var vk = _device.Api;
        if (_mapped is not null)
        {
            vk.UnmapMemory(_device.Device, _memory);
            _mapped = null;
        }

        if (_buffer.Handle != 0)
        {
            vk.DestroyBuffer(_device.Device, _buffer, null);
            _buffer = default;
            ResourceTracker.Unregister("VkBuffer");
        }

        if (_memory.Handle != 0)
        {
            vk.FreeMemory(_device.Device, _memory, null);
            _memory = default;
            ResourceTracker.Unregister("VkDeviceMemory");
        }
    }

    private static BufferUsageFlags ToVkUsage(BufferUsage usage)
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

        if (flags == BufferUsageFlags.None)
        {
            throw new GraphicsException("BufferUsage must specify at least one usage.");
        }

        return flags;
    }
}
