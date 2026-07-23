using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

/// <summary>
/// The double-buffer sync invariant (P3-M6 AW-006, spec §5), verified GPU-free: the persistent candidate buffer has
/// <c>F</c> copies; one is consumed per frame while the CPU changes the others, so a copy must be synced to the
/// authoritative mirror before its cull reads it. The failure this guards is subtle and silent: a copy that predates
/// a structural rebuild (which reassigns slots) consumed with the new batch table would compact into the wrong region
/// — out-of-bounds writes / ghost instances, no validation error.
/// <para>
/// This harness mirrors exactly what <see cref="PersistentInstanceBuffer"/> does (apply dirty → plan → full rewrite
/// or replay), but into plain arrays standing in for the GPU copies, so the invariant is checked without a device.
/// </para>
/// </summary>
public sealed class CopySyncStateTests
{
    private const int F = 2; // GraphicsDevice.FramesInFlight

    // A stand-in for one GPU copy: a candidate array the harness writes exactly as PersistentInstanceBuffer would.
    private sealed class FakeCopy
    {
        public SceneCandidate[] Data = new SceneCandidate[0];
        public uint Version = uint.MaxValue;
    }

    private static RenderItem Item(int mesh, int material, uint order)
        => new(
            Matrix4x4.CreateTranslation(order, 0, 0), new MeshHandle(mesh, 1), new MaterialHandle(material, 1),
            RenderItem.ComposeSortKey(material, mesh, order), new Vector4(order, 0, 0, 1),
            SceneCandidate.FlagCastsShadow);

    // Runs one frame: applies dirty into the mirror (the set's array), plans, then writes the consumed copy — the
    // same sequence PersistentInstanceBuffer.Sync performs.
    private static void SyncFrame(CopySyncState sync, SceneCandidateSet set, FakeCopy[] copies, int frame)
    {
        var slot = frame % F;
        var dirty = set.Dirty;
        for (var i = 0; i < dirty.Length; i++)
        {
            ref var c = ref set.CandidateAt(dirty[i].Slot);
            c.Model = dirty[i].Model;
            c.Sphere = dirty[i].Sphere;
        }

        var copy = copies[slot];
        var count = set.Count;
        var resized = copy.Data.Length < count;
        if (resized)
        {
            copy.Data = new SceneCandidate[Math.Max(count, 1)];
            copy.Version = uint.MaxValue;
        }

        var full = sync.PlanFrame(set.Structural, set.StructuralVersion, dirty, count, slot)
                   || copy.Version != set.StructuralVersion;

        if (full)
        {
            set.Candidates.CopyTo(copy.Data);
            copy.Version = set.StructuralVersion;
        }
        else
        {
            foreach (var s in sync.ReplaySlots)
            {
                copy.Data[s] = set.Candidates[s];
            }
        }
    }

    // The mirror after all pending patches are folded in — the ground truth every consumed copy must eventually match.
    private static void AssertCopyMatchesMirror(FakeCopy copy, SceneCandidateSet set)
    {
        for (var i = 0; i < set.Count; i++)
        {
            Assert.Equal(set.Candidates[i].Model.Translation, copy.Data[i].Model.Translation);
            Assert.Equal(set.Candidates[i].SceneBatchId, copy.Data[i].SceneBatchId);
        }
    }

    [Fact]
    public void StructuralRebuild_FullyRewritesEachCopy_OverTheNextFFrames_NoStaleSlots()
    {
        var set = new SceneCandidateSet();
        var sync = new CopySyncState(F);
        var copies = new[] { new FakeCopy(), new FakeCopy() };

        // Frame 0: initial structural build of 3 items, consumed by copy 0.
        set.Rebuild(new[] { Item(0, 0, 0), Item(0, 0, 1), Item(0, 0, 2) });
        SyncFrame(sync, set, copies, frame: 0);
        AssertCopyMatchesMirror(copies[0], set);

        // Frame 1: a structural rebuild GROWS the set to 5 (a spawn). Copy 1 still holds the OLD version → it must
        // full-rewrite to the new assignment, never a partial replay against the new batch layout.
        set.Rebuild(new[] { Item(0, 0, 0), Item(0, 0, 1), Item(0, 0, 2), Item(0, 0, 3), Item(0, 0, 4) });
        SyncFrame(sync, set, copies, frame: 1);
        AssertCopyMatchesMirror(copies[1], set); // copy 1 up to date this frame

        // Frame 2: copy 0 is consumed again; it predates the rebuild, so it too must full-rewrite (no stale slot 0..2
        // with the new count). This is the OOB/ghost case the invariant prevents.
        SyncFrame(sync, set, copies, frame: 2);
        AssertCopyMatchesMirror(copies[0], set);
        Assert.Equal(5, copies[0].Data.Length >= 5 ? 5 : copies[0].Data.Length);
    }

    [Fact]
    public void IncrementalPatch_ConvergesInEveryCopy_AfterFFrames()
    {
        var set = new SceneCandidateSet();
        var sync = new CopySyncState(F);
        var copies = new[] { new FakeCopy(), new FakeCopy() };

        set.Rebuild(new[] { Item(0, 0, 0), Item(0, 0, 1) });
        SyncFrame(sync, set, copies, 0); // structural → copy 0 full
        set.ClearDirty();
        SyncFrame(sync, set, copies, 1); // structural still pending for copy 1 → copy 1 full
        AssertCopyMatchesMirror(copies[1], set);

        // Move slot 1 on an incremental frame. It must land in BOTH copies within F frames.
        set.ClearDirty();
        set.EnqueueDirty(1, Matrix4x4.CreateTranslation(99, 0, 0), new Vector4(99, 0, 0, 1));
        SyncFrame(sync, set, copies, 2); // patched into copy 0
        Assert.Equal(new Vector3(99, 0, 0), copies[0].Data[1].Model.Translation);

        set.ClearDirty(); // no new dirty this frame; the countdown still owes copy 1
        SyncFrame(sync, set, copies, 3);
        Assert.Equal(new Vector3(99, 0, 0), copies[1].Data[1].Model.Translation); // converged in copy 1 too
    }

    [Fact]
    public void StaticFrame_PlansNoReplay()
    {
        var set = new SceneCandidateSet();
        var sync = new CopySyncState(F);
        var copies = new[] { new FakeCopy(), new FakeCopy() };
        set.Rebuild(new[] { Item(0, 0, 0), Item(0, 0, 1) });
        SyncFrame(sync, set, copies, 0);
        set.ClearDirty();
        SyncFrame(sync, set, copies, 1);

        // Both copies now hold the structural version. A static frame (no dirty) replays nothing and rewrites nothing.
        set.ClearDirty();
        var full = sync.PlanFrame(set.Structural, set.StructuralVersion, set.Dirty, set.Count, frameSlot: 0);
        Assert.False(full);
        Assert.Equal(0, sync.ReplaySlots.Length);
    }

    [Fact]
    public void PlanFrame_IsZeroAlloc_InSteadyState()
    {
        // The Rendering-side 0-alloc gate (audit 🟡-1): PlanFrame's List<int> _active/_replay must not allocate per
        // frame once warmed. This is the deterministic counterpart to the World-side zero-alloc test — the bench's
        // occasional sub-KB blip is runtime noise, not this path, and only a deterministic assert can prove it.
        const int slots = 512;
        var set = new SceneCandidateSet();
        var items = new RenderItem[slots];
        for (var i = 0; i < slots; i++)
        {
            items[i] = Item(i % 4, i % 2, (uint)i);
        }

        set.Rebuild(items);
        var sync = new CopySyncState(F);

        // Warm up: the first frames grow _active/_replay to their steady size, then they stabilise (Clear keeps
        // capacity). Re-arm the same dirty slots every frame so _active stays saturated — the worst case.
        for (var f = 0; f < 8; f++)
        {
            set.ClearDirty();
            for (var s = 0; s < slots; s++)
            {
                set.EnqueueDirty(s, Matrix4x4.Identity, Vector4.Zero);
            }

            sync.PlanFrame(set.Structural, set.StructuralVersion, set.Dirty, set.Count, f % F);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var f = 0; f < 100; f++)
        {
            set.ClearDirty();
            for (var s = 0; s < slots; s++)
            {
                set.EnqueueDirty(s, Matrix4x4.Identity, Vector4.Zero);
            }

            sync.PlanFrame(set.Structural, set.StructuralVersion, set.Dirty, set.Count, f % F);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
