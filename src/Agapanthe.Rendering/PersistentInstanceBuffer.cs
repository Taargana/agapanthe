using System.Runtime.CompilerServices;
using Agapanthe.Core;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// The persistent scene-candidate SSBO (P3-M6, spec §4.4/§5): <c>FramesInFlight</c> host-visible GPU copies of the
/// candidate buffer, kept in sync with the authoritative CPU mirror — the <see cref="SceneCandidateSet"/>'s own
/// candidate array. Replaces the per-frame <c>StorageBufferRing&lt;SceneCandidate&gt;.Upload</c> of every candidate:
/// only a moved drawable's slot is rewritten, and only into the copies that still owe the change.
/// <para>
/// <b>Why the mirror is the set's array.</b> The set already holds the candidates (batch ids + flags stable between
/// rebuilds, model/sphere patched on the incremental path). <see cref="Sync"/> applies this frame's dirty patches
/// into that array first, so a later full rewrite of another copy carries the change — the set IS the mirror.
/// </para>
/// <para>
/// <b>Why writing a copy mid-record is safe.</b> A copy is only read by its own frame's compute cull, and the frame
/// loop has already waited on that slot's fence before recording — the same guarantee <see cref="InstanceBufferRing"/>
/// relies on. A grown buffer still goes through the N+2 deletion queue.
/// </para>
/// </summary>
internal sealed class PersistentInstanceBuffer : IDisposable
{
    private const int InitialCapacity = 1024;

    private readonly GraphicsDevice _device;
    private readonly GpuBuffer?[] _buffers = new GpuBuffer?[GraphicsDevice.FramesInFlight];

    // The structural version each physical copy's BUFFER currently holds. This is a BUFFER-IDENTITY guard, distinct
    // from CopySyncState's replay bookkeeping: a freshly (re)allocated buffer holds no prior data, so a partial replay
    // into it would leave un-replayed slots as garbage. Today a resize only ever coincides with a version bump (Count
    // changes only on a structural Rebuild), so CopySyncState would force a full rewrite anyway — but keeping the
    // guard here means a future change to the sizing policy (shrink, pre-grow) cannot silently reintroduce that hazard.
    private readonly uint[] _bufferVersion = new uint[GraphicsDevice.FramesInFlight];
    private readonly CopySyncState _sync = new(GraphicsDevice.FramesInFlight);
    private bool _disposed;

    public PersistentInstanceBuffer(GraphicsDevice device) => _device = device;

    /// <summary>
    /// Brings the copy for <paramref name="frameSlot"/> up to the set's current state and returns it to bind at the
    /// cull's candidate binding. Applies this frame's dirty patches into the mirror, then either fully rewrites the
    /// copy (it predates the last structural rebuild — a resized buffer forces this too) or replays only the slots
    /// that changed within the last <c>FramesInFlight</c> frames (spec §5).
    /// </summary>
    public GpuBuffer Sync(SceneCandidateSet set, int frameSlot)
    {
        ArgumentNullException.ThrowIfNull(set);

        // 1) Fold this frame's patches into the mirror (the set's array), so every copy that full-rewrites later
        //    carries them. Batch ids / flags are untouched (stable between rebuilds) — only model + sphere move.
        var dirty = set.Dirty;
        for (var i = 0; i < dirty.Length; i++)
        {
            ref var c = ref set.CandidateAt(dirty[i].Slot);
            c.Model = dirty[i].Model;
            c.Sphere = dirty[i].Sphere;
        }

        var count = set.Count;

        // 2) A resize can never be a partial replay (the new buffer holds no prior data), so force a full rewrite of
        //    THIS copy by pretending it holds no version. Other copies resize on their own turn.
        var resized = EnsureCapacity(frameSlot, count);
        if (resized)
        {
            _bufferVersion[frameSlot] = uint.MaxValue;
        }

        var full = _sync.PlanFrame(set.Structural, set.StructuralVersion, dirty, count, frameSlot)
                   || _bufferVersion[frameSlot] != set.StructuralVersion;

        var buffer = _buffers[frameSlot]!;
        var dst = buffer.MappedSpan<SceneCandidate>(Math.Max(count, 1));

        if (full)
        {
            set.Candidates.CopyTo(dst);
            _bufferVersion[frameSlot] = set.StructuralVersion;
        }
        else
        {
            var mirror = set.Candidates;
            foreach (var slot in _sync.ReplaySlots)
            {
                dst[slot] = mirror[slot];
            }
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

    // Grows this copy's buffer (doubling) to hold at least `count` candidates. Returns true if it (re)allocated, so
    // the caller can force a full rewrite of the fresh buffer. No shrink: the candidate count tracks live drawables,
    // which do not oscillate the way a frustum-culled visible set did.
    private bool EnsureCapacity(int frameSlot, int count)
    {
        var stride = (ulong)Unsafe.SizeOf<SceneCandidate>();
        var needed = Math.Max(count, 1);
        var existing = _buffers[frameSlot];
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

        existing?.Dispose(); // deferred N+2 by the deletion queue
        _buffers[frameSlot] = new GpuBuffer(_device, (ulong)newCapacity * stride, BufferUsage.Storage);
        return true;
    }
}
