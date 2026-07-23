using System.Runtime.InteropServices;
using Agapanthe.Core;

namespace Agapanthe.Rendering;

/// <summary>
/// The double-buffer sync bookkeeping for the persistent candidate buffer (P3-M6, spec §5) — GPU-free so the
/// invariant that actually matters can be unit-tested without a device.
/// <para>
/// There are <c>FramesInFlight</c> physical GPU copies of the candidate buffer. Each frame consumes one
/// (<c>frameSlot = frame % F</c>) while the CPU is free to change the others, so a copy must be brought up to the
/// authoritative CPU mirror <b>before</b> its cull reads it. This class decides, per frame, whether the consumed
/// copy needs a <b>full rewrite</b> (it predates the last structural rebuild — its slot assignment is stale, which
/// would make the batch table compact into the wrong region) or just a <b>replay</b> of the slots that changed in
/// the last <c>F</c> frames.
/// </para>
/// <para>
/// A structural rebuild bumps the version and clears the replay set: every copy is then behind and full-rewrites on
/// its next turn (no per-slot replay can fix a reassignment). An incremental patch arms a slot's countdown to
/// <c>F</c>; each of the next <c>F</c> frames replays it into one copy, so all copies converge. The active-slot list
/// keeps this O(changed), not O(count): a static frame plans nothing.
/// </para>
/// </summary>
internal sealed class CopySyncState
{
    private readonly int _framesInFlight;
    private readonly uint[] _copyVersion;      // per copy: the structural version it currently holds
    private int[] _countdown = [];             // per slot: frames still owing this change (0 = up to date everywhere)
    private readonly List<int> _active = new(); // slots with countdown > 0 (so a static frame iterates nothing)
    private readonly List<int> _replay = new(); // this frame's slots to write into the consumed copy

    public CopySyncState(int framesInFlight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(framesInFlight, 1);
        _framesInFlight = framesInFlight;
        _copyVersion = new uint[framesInFlight];
        // Sentinel: no copy has seen a real version yet (the first Rebuild bumps to 1), so the first use of each copy
        // full-rewrites. uint.MaxValue can never collide with a real version at these counts.
        Array.Fill(_copyVersion, uint.MaxValue);
    }

    /// <summary>The slots to write into the consumed copy this frame when <see cref="PlanFrame"/> returned
    /// <c>false</c> (a replay). Empty on a full rewrite or a static frame.</summary>
    public ReadOnlySpan<int> ReplaySlots => CollectionsMarshal.AsSpan(_replay);

    /// <summary>
    /// Plans the sync for <paramref name="frameSlot"/>'s copy. Call ONCE per frame, before writing the copy. Returns
    /// <c>true</c> if the copy must be fully rewritten from the mirror; <c>false</c> if the caller should write only
    /// <see cref="ReplaySlots"/>. <paramref name="dirty"/> is this frame's patch list (ignored when structural).
    /// </summary>
    public bool PlanFrame(bool structural, uint structuralVersion, ReadOnlySpan<SlotPatch> dirty, int count, int frameSlot)
    {
        EnsureCountdown(count);

        if (structural)
        {
            // A rebuild reassigned slots; no per-slot replay can reconcile that. Drop every pending countdown — the
            // full rewrites below carry the whole (new) mirror to each copy in turn.
            for (var i = 0; i < _active.Count; i++)
            {
                _countdown[_active[i]] = 0;
            }

            _active.Clear();
        }
        else
        {
            for (var i = 0; i < dirty.Length; i++)
            {
                var slot = dirty[i].Slot;
                if (_countdown[slot] == 0)
                {
                    _active.Add(slot); // newly changed → becomes active
                }

                _countdown[slot] = _framesInFlight; // (re)arm: owed to every copy again
            }
        }

        _replay.Clear();

        if (_copyVersion[frameSlot] != structuralVersion)
        {
            _copyVersion[frameSlot] = structuralVersion;
            return true; // full rewrite: this copy predates the current structural version
        }

        // Same version: replay the active slots into this copy, decrementing each; a slot that reaches 0 has now
        // reached every copy and leaves the active list (swap-compact, capacity kept → 0 alloc).
        var w = 0;
        for (var r = 0; r < _active.Count; r++)
        {
            var slot = _active[r];
            _replay.Add(slot);
            if (--_countdown[slot] > 0)
            {
                _active[w++] = slot;
            }
        }

        _active.RemoveRange(w, _active.Count - w);
        return false;
    }

    private void EnsureCountdown(int count)
    {
        if (_countdown.Length < count)
        {
            Array.Resize(ref _countdown, Math.Max(count, _countdown.Length == 0 ? 1024 : _countdown.Length * 2));
        }
    }
}
