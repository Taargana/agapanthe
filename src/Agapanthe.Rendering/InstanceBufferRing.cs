using System.Numerics;
using System.Runtime.CompilerServices;
using Agapanthe.Core;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// One per-instance transform SSBO per frame in flight, plus the compaction that fills it (P3-M1). This is the one
/// place a GPU-free <see cref="RenderList"/> meets the GPU: the World bakes each visible entity's camera-relative
/// matrix into <see cref="RenderItem.WorldTransform"/>, and the pass copies that run of matrices, in sorted order,
/// into the slot's host-visible buffer. The vertex stage then reads <c>transforms[gl_InstanceIndex]</c>, with
/// <c>firstInstance</c> offsetting into the compacted region — so a batch needs no rebind.
/// <para>
/// <b>Sizing.</b> Grows by doubling; shrinks by halving only after the list has stayed below a quarter of capacity
/// for <see cref="ShrinkGraceFrames"/> consecutive frames, so a transient spike (a teleport into a dense region)
/// does not pin its peak footprint forever, and a list that merely oscillates never thrashes. Steady state
/// allocates nothing.
/// </para>
/// <para>
/// <b>Why recreating a slot's buffer mid-record is safe.</b> A slot's buffer is only read while that slot is in
/// flight, and the frame loop has already waited on that slot's fence before we record into it. The replaced buffer
/// still goes through the deferred deletion queue (N+2) — belt and braces.
/// </para>
/// </summary>
internal sealed class InstanceBufferRing : IDisposable
{
    private const int InitialCapacity = 1024;
    private const int ShrinkGraceFrames = 60;

    private readonly GraphicsDevice _device;
    private readonly GpuBuffer?[] _buffers = new GpuBuffer?[GraphicsDevice.FramesInFlight];
    private readonly int[] _lowWaterFrames = new int[GraphicsDevice.FramesInFlight];
    private bool _disposed;

    public InstanceBufferRing(GraphicsDevice device) => _device = device;

    /// <summary>
    /// Copies the items' world transforms into this slot's buffer (resizing it if needed) and returns the buffer to
    /// bind. The copy is a straight write into mapped, host-coherent memory: no scratch, no allocation.
    /// </summary>
    public GpuBuffer Compact(int slot, ReadOnlySpan<RenderItem> items)
    {
        var buffer = Ensure(slot, items.Length);
        var dst = buffer.MappedSpan<Matrix4x4>(items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            dst[i] = items[i].WorldTransform;
        }

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
        var stride = (ulong)Unsafe.SizeOf<Matrix4x4>();
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
        var buffer = new GpuBuffer(_device, (ulong)newCapacity * stride, BufferUsage.Storage);
        _buffers[slot] = buffer;
        return buffer;
    }
}
