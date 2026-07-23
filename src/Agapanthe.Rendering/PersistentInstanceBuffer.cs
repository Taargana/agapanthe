using System.Runtime.CompilerServices;
using Agapanthe.Core;
using Agapanthe.Graphics;
using Agapanthe.Graphics.Memory;

namespace Agapanthe.Rendering;

/// <summary>
/// The persistent scene-candidate SSBO (P3-M6 §4.4/§5; device-local since P3-M7). Per frame in flight there is a
/// PAIR of buffers: a <b>persistent host-visible staging</b> buffer that IS the CPU mirror (the
/// <see cref="SceneCandidateSet"/> folds patches into it via <see cref="Sync"/>), and a <b>device-local</b> buffer
/// the two GPU culls actually read. Each frame <see cref="Sync"/> writes the staging bytes exactly as P3-M6 did,
/// then records an async <see cref="CommandList.CopyBuffer"/> of only the ranges it wrote (a full rewrite = one
/// region; a dirty-slot replay = one region per slot, or a single full copy when the dirty set is large) — so the
/// GPU reads GPU-local memory with no per-frame PCIe round-trip on a discrete card.
/// <para>
/// <b>Why the staging must stay persistent (correctness).</b> P3-M6's replay model assumes a copy RETAINS its
/// unchanged slots between frames and only dirty slots are rewritten. That holds only if the bytes copied FROM are
/// the retained mirror. So the per-copy staging buffer is never reused for anything else: after each
/// <see cref="Sync"/> the staging and the device-local buffer are byte-identical, and copying only the dirty ranges
/// keeps the device-local buffer a faithful mirror (its untouched bytes are already correct from prior frames).
/// </para>
/// <para>
/// <b>Why writing a copy mid-record is safe.</b> A pair is only read by its own frame's compute culls, and the frame
/// loop has already waited on that slot's fence before recording — the same guarantee <see cref="InstanceBufferRing"/>
/// relies on. Grown buffers still go through the N+2 deletion queue.
/// </para>
/// </summary>
internal sealed class PersistentInstanceBuffer : IDisposable
{
    private const int InitialCapacity = 1024;

    private readonly GraphicsDevice _device;
    private readonly GpuBuffer?[] _staging = new GpuBuffer?[GraphicsDevice.FramesInFlight];    // host-visible, the mirror
    private readonly GpuBuffer?[] _deviceLocal = new GpuBuffer?[GraphicsDevice.FramesInFlight]; // GPU-local, read by the culls

    // The structural version each physical copy's device-local BUFFER currently holds. Buffer-identity guard, distinct
    // from CopySyncState's replay bookkeeping: a freshly (re)allocated buffer holds no prior data, so a partial replay
    // into it would leave un-replayed slots as garbage. Today a resize only coincides with a version bump (Count
    // changes only on a structural Rebuild) so CopySyncState forces a full rewrite anyway — but the guard means a
    // future sizing-policy change (shrink, pre-grow) cannot silently reintroduce that hazard.
    private readonly uint[] _bufferVersion = new uint[GraphicsDevice.FramesInFlight];
    private readonly CopySyncState _sync = new(GraphicsDevice.FramesInFlight);

    // Reused copy-region scratch (grown with capacity) so a dirty-range copy never heap-allocates on the hot path.
    private BufferCopyRegion[] _regions = new BufferCopyRegion[InitialCapacity];

    private bool _disposed;

    public PersistentInstanceBuffer(GraphicsDevice device) => _device = device;

    /// <summary>
    /// Brings the pair for <paramref name="frameSlot"/> up to the set's current state and returns the
    /// <b>device-local</b> buffer to bind at the culls' candidate binding. Folds this frame's dirty patches into the
    /// mirror (staging), writes the fresh bytes, records the staging→device-local copy on <paramref name="cmd"/>, and
    /// a transfer→compute barrier so both culls (recorded later) see the updated data.
    /// </summary>
    public GpuBuffer Sync(CommandList cmd, SceneCandidateSet set, int frameSlot)
    {
        ArgumentNullException.ThrowIfNull(set);

        // 1) Fold this frame's patches into the mirror (the set's array), so any copy that full-rewrites later
        //    carries them. Batch ids / flags are untouched (stable between rebuilds) — only model + sphere move.
        var dirty = set.Dirty;
        for (var i = 0; i < dirty.Length; i++)
        {
            ref var c = ref set.CandidateAt(dirty[i].Slot);
            c.Model = dirty[i].Model;
            c.Sphere = dirty[i].Sphere;
        }

        var count = set.Count;
        var stride = (ulong)Unsafe.SizeOf<SceneCandidate>();

        // 2) A resize can never be a partial replay (the new buffers hold no prior data), so force a full rewrite of
        //    THIS pair by pretending it holds no version. Other pairs resize on their own turn.
        var resized = EnsureCapacity(frameSlot, count);
        if (resized)
        {
            _bufferVersion[frameSlot] = uint.MaxValue;
        }

        var full = _sync.PlanFrame(set.Structural, set.StructuralVersion, dirty, count, frameSlot)
                   || _bufferVersion[frameSlot] != set.StructuralVersion;

        var staging = _staging[frameSlot]!;
        var deviceLocal = _deviceLocal[frameSlot]!;
        var dst = staging.MappedSpan<SceneCandidate>(Math.Max(count, 1));
        var mirror = set.Candidates;

        int regionCount;
        if (full)
        {
            mirror.CopyTo(dst);
            _bufferVersion[frameSlot] = set.StructuralVersion;
            _regions[0] = new BufferCopyRegion(0, 0, (ulong)Math.Max(count, 1) * stride);
            regionCount = 1;
        }
        else
        {
            var replay = _sync.ReplaySlots;
            // Fallback: many small regions cost more than one full copy (spec §3.4). Above half the buffer, copy it
            // whole — staging already holds the full mirror, so a full copy is always correct.
            if (replay.Length > count / 2)
            {
                foreach (var slot in replay)
                {
                    dst[slot] = mirror[slot];
                }

                _regions[0] = new BufferCopyRegion(0, 0, (ulong)Math.Max(count, 1) * stride);
                regionCount = 1;
            }
            else
            {
                regionCount = 0;
                foreach (var slot in replay)
                {
                    dst[slot] = mirror[slot];
                    var off = (ulong)slot * stride;
                    _regions[regionCount++] = new BufferCopyRegion(off, off, stride);
                }
            }
        }

        if (regionCount > 0)
        {
            cmd.CopyBuffer(staging, deviceLocal, _regions.AsSpan(0, regionCount));
            // Order the copy before the compute culls that read the device-local buffer. Recorded once here; both
            // culls (scene, shadow) read this same buffer read-only, so a single transfer→compute barrier suffices.
            cmd.BufferBarrier(deviceLocal, BufferSync.TransferWrite, BufferSync.ComputeRead);
        }

        return deviceLocal;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = 0; i < _staging.Length; i++)
        {
            _staging[i]?.Dispose();
            _deviceLocal[i]?.Dispose();
            _staging[i] = null;
            _deviceLocal[i] = null;
        }
    }

    // Grows this pair's buffers (doubling) to hold at least `count` candidates. Returns true if it (re)allocated, so
    // the caller can force a full rewrite of the fresh buffers. Also grows the region scratch. No shrink: the
    // candidate count tracks live drawables, which do not oscillate the way a frustum-culled visible set did.
    private bool EnsureCapacity(int frameSlot, int count)
    {
        var stride = (ulong)Unsafe.SizeOf<SceneCandidate>();
        var needed = Math.Max(count, 1);
        var existing = _deviceLocal[frameSlot];
        var capacity = existing is null ? 0 : (int)(existing.SizeBytes / stride);
        if (existing is not null && capacity >= needed)
        {
            return false;
        }

        var newCapacity = capacity == 0 ? InitialCapacity : capacity * 2;
        if (newCapacity < needed)
        {
            newCapacity = needed;
        }

        _staging[frameSlot]?.Dispose();       // deferred N+2 by the deletion queue
        _deviceLocal[frameSlot]?.Dispose();
        _staging[frameSlot] = new GpuBuffer(_device, (ulong)newCapacity * stride, BufferUsage.TransferSrc);
        _deviceLocal[frameSlot] = new GpuBuffer(
            _device, (ulong)newCapacity * stride, BufferUsage.Storage, MemoryDomain.DeviceLocal);

        if (_regions.Length < newCapacity)
        {
            _regions = new BufferCopyRegion[newCapacity];
        }

        return true;
    }
}
