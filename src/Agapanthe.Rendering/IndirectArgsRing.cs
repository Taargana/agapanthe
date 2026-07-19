using System.Runtime.CompilerServices;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// One host-visible indirect-args buffer per frame in flight (P3-M4 W0): the CPU writes one
/// <see cref="DrawIndexedIndirectCommand"/> per batch, and the scene/shadow pass issues each batch's draw with
/// <c>vkCmdDrawIndexedIndirect</c> instead of a direct <c>DrawIndexed</c>. The buffer is
/// <see cref="BufferUsage.Indirect"/> (+ <see cref="BufferUsage.Storage"/> so a later compute cull can patch
/// each command's <c>instanceCount</c> in place — W1).
/// <para>
/// Same sizing discipline as <see cref="InstanceBufferRing"/>: grow by doubling, shrink after a grace period,
/// zero allocation in steady state. The write is a straight copy into mapped, host-coherent memory.
/// </para>
/// </summary>
internal sealed class IndirectArgsRing : IDisposable
{
    private const int InitialCapacity = 64;
    private const int ShrinkGraceFrames = 60;

    private readonly GraphicsDevice _device;
    private readonly GpuBuffer?[] _buffers = new GpuBuffer?[GraphicsDevice.FramesInFlight];
    private readonly int[] _lowWaterFrames = new int[GraphicsDevice.FramesInFlight];
    private bool _disposed;

    public IndirectArgsRing(GraphicsDevice device) => _device = device;

    /// <summary>
    /// Copies <paramref name="commands"/> into this slot's buffer (resizing if needed) and returns it, ready to
    /// bind as the indirect-args source. The copy is a straight write into mapped host-coherent memory: no
    /// scratch, no allocation.
    /// </summary>
    public GpuBuffer Upload(int slot, ReadOnlySpan<DrawIndexedIndirectCommand> commands)
    {
        var buffer = Ensure(slot, commands.Length);
        var dst = buffer.MappedSpan<DrawIndexedIndirectCommand>(commands.Length);
        commands.CopyTo(dst);
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
        var stride = (ulong)Unsafe.SizeOf<DrawIndexedIndirectCommand>();
        var needed = Math.Max(count, 1);
        var existing = _buffers[slot];
        var capacity = existing is null ? 0 : (int)(existing.SizeBytes / stride);

        var newCapacity = capacity;
        if (existing is null || capacity < needed)
        {
            _lowWaterFrames[slot] = 0;
            newCapacity = capacity == 0 ? InitialCapacity : capacity * 2;
            if (newCapacity < needed)
            {
                newCapacity = needed;
            }
        }
        else if (needed <= capacity / 4 && capacity > InitialCapacity)
        {
            if (++_lowWaterFrames[slot] < ShrinkGraceFrames)
            {
                return existing!;
            }

            _lowWaterFrames[slot] = 0;
            newCapacity = Math.Max(capacity / 2, InitialCapacity);
        }
        else
        {
            _lowWaterFrames[slot] = 0;
            return existing!;
        }

        existing?.Dispose(); // deferred N+2 by the deletion queue
        var buffer = new GpuBuffer(_device, (ulong)newCapacity * stride, BufferUsage.Storage | BufferUsage.Indirect);
        _buffers[slot] = buffer;
        return buffer;
    }
}
