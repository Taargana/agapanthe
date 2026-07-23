using System.Numerics;
using System.Runtime.CompilerServices;
using Agapanthe.Graphics;
using Agapanthe.Graphics.Memory;

namespace Agapanthe.Rendering;

/// <summary>
/// One per-instance transform SSBO per frame in flight (P3-M1; device-local since P3-M7). The compute cull writes
/// the surviving instances into it and the vertex stage reads <c>transforms[gl_InstanceIndex + batchOffset]</c> —
/// both on the GPU, no host access — so the buffer is <b>device-local</b> (GPU-local memory, no PCIe round-trip on
/// a discrete GPU). It is never mapped or written by the CPU; <see cref="EnsureCapacity"/> only sizes it.
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
    /// Sizes this slot's buffer to at least <paramref name="count"/> matrices and returns it WITHOUT writing —
    /// the compute cull (not the CPU) fills it with the surviving instances (P3-M4 W1). Device-local (P3-M7).
    /// </summary>
    public GpuBuffer EnsureCapacity(int slot, int count) => Ensure(slot, count);

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
        // Device-local (P3-M7): the compute cull writes it and the vertex stage reads it — no host access, so no
        // staging and no host-visible PCIe surface. On MoltenVK (unified memory) this is identical to host-visible.
        var buffer = new GpuBuffer(_device, (ulong)newCapacity * stride, BufferUsage.Storage, MemoryDomain.DeviceLocal);
        _buffers[slot] = buffer;
        return buffer;
    }
}
