using System.Runtime.CompilerServices;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// One host-visible <see cref="BufferUsage.Storage"/> buffer per frame in flight, holding an array of blittable
/// <typeparamref name="T"/> (P3-M4 W1): the CPU uploads the compute-cull inputs (per-candidate transform+sphere,
/// per-batch base offset) each frame and the compute shader reads them. Same grow-by-doubling, zero-alloc-in-
/// steady-state discipline as <see cref="InstanceBufferRing"/> / <see cref="IndirectArgsRing"/>.
/// </summary>
internal sealed class StorageBufferRing<T> : IDisposable
    where T : unmanaged
{
    private const int InitialCapacity = 256;

    private readonly GraphicsDevice _device;
    private readonly GpuBuffer?[] _buffers = new GpuBuffer?[GraphicsDevice.FramesInFlight];
    private bool _disposed;

    public StorageBufferRing(GraphicsDevice device) => _device = device;

    /// <summary>Copies <paramref name="items"/> into this slot's buffer (resizing if needed) and returns it.</summary>
    public GpuBuffer Upload(int slot, ReadOnlySpan<T> items)
    {
        var buffer = Ensure(slot, items.Length);
        items.CopyTo(buffer.MappedSpan<T>(items.Length));
        return buffer;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = 0; i < _buffers.Length; i++)
        {
            _buffers[i]?.Dispose();
            _buffers[i] = null;
        }
    }

    private GpuBuffer Ensure(int slot, int count)
    {
        var stride = (ulong)Unsafe.SizeOf<T>();
        var needed = Math.Max(count, 1);
        var existing = _buffers[slot];
        var capacity = existing is null ? 0 : (int)(existing.SizeBytes / stride);
        if (existing is not null && capacity >= needed)
        {
            return existing;
        }

        var newCapacity = capacity == 0 ? InitialCapacity : capacity * 2;
        if (newCapacity < needed)
        {
            newCapacity = needed;
        }

        existing?.Dispose(); // deferred N+2 by the deletion queue
        var buffer = new GpuBuffer(_device, (ulong)newCapacity * stride, BufferUsage.Storage);
        _buffers[slot] = buffer;
        return buffer;
    }
}
