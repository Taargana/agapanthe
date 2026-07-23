using System.Numerics;

namespace Agapanthe.Core;

/// <summary>
/// A run of consecutive instances sharing a draw signature: <c>(material, mesh)</c> for the scene pass (material-
/// major), or <c>(mesh)</c> alone for the shadow pass (mesh-major, <see cref="MaterialHandle.Invalid"/>). GPU-free.
/// </summary>
public readonly struct RenderBatch(MeshHandle mesh, MaterialHandle material, uint offset, uint count)
{
    public readonly MeshHandle Mesh = mesh;
    public readonly MaterialHandle Material = material;

    /// <summary>Scene: the run's start index in the candidate buffer. Shadow: this mesh-batch's base
    /// (<c>meshBatchBase</c>) — the GPU adds <c>cascade × totalCasters</c> to reach its per-cascade region.</summary>
    public readonly uint Offset = offset;
    public readonly uint Count = count;
}

/// <summary>One per-entity transform patch (P3-M6 incremental path): a moved drawable rewrites only its slot's
/// model + sphere; its batch ids and flags are stable between structural rebuilds and never re-sent.</summary>
public readonly struct SlotPatch(int slot, in Matrix4x4 model, in Vector4 sphere)
{
    public readonly int Slot = slot;
    public readonly Matrix4x4 Model = model;
    public readonly Vector4 Sphere = sphere;
}

/// <summary>
/// The persistent scene-candidate data the World maintains and the Renderer consumes (P3-M6, spec §4.4). GPU-free
/// (blittable math, owned by the Renderer like <see cref="RenderList"/>, filled by the World). It holds the sorted
/// candidate array (the CPU mirror of the persistent GPU buffer), the two batch tables, and a dirty patch queue.
/// <para>
/// A <b>structural rebuild</b> (<see cref="Rebuild"/>) replaces the whole array + both tables and bumps
/// <see cref="StructuralVersion"/>; a per-frame <b>incremental</b> pass appends <see cref="SlotPatch"/>es via
/// <see cref="EnqueueDirty"/>. The Renderer's per-copy sync (spec §5) reads <see cref="StructuralVersion"/> to
/// decide full-rewrite vs replay. Every buffer is reused (grown, never re-allocated per frame) → 0 alloc steady
/// state.
/// </para>
/// </summary>
public sealed class SceneCandidateSet
{
    private SceneCandidate[] _candidates = [];
    private int _count;
    private RenderBatch[] _sceneBatches = [];
    private int _sceneBatchCount;
    private RenderBatch[] _shadowBatches = [];
    private int _shadowBatchCount;
    private int _totalCasters;

    // Reused scratch for the shadow mesh histogram (first-seen distinct caster meshes + their counts). Distinct
    // meshes are few, so a linear scan over these is cheaper than a Dictionary — and never allocates.
    private MeshHandle[] _shadowMeshes = [];
    private uint[] _shadowCounts = [];

    private SlotPatch[] _dirty = [];
    private int _dirtyCount;

    /// <summary><c>true</c> after a <see cref="Rebuild"/>, <c>false</c> after an <see cref="EnqueueDirty"/> —
    /// the Renderer's cue to full-rewrite the consumed copy vs replay dirty slots.</summary>
    public bool Structural { get; private set; }

    /// <summary>Bumped on every <see cref="Rebuild"/> (never on a dirty patch): the version each GPU copy syncs to
    /// before use, which keeps the batch table and the copy's slot assignment coupled (spec §5).</summary>
    public uint StructuralVersion { get; private set; }

    public int Count => _count;
    public int TotalCasters => _totalCasters;
    public ReadOnlySpan<SceneCandidate> Candidates => _candidates.AsSpan(0, _count);
    public ReadOnlySpan<RenderBatch> SceneBatches => _sceneBatches.AsSpan(0, _sceneBatchCount);
    public ReadOnlySpan<RenderBatch> ShadowBatches => _shadowBatches.AsSpan(0, _shadowBatchCount);
    public ReadOnlySpan<SlotPatch> Dirty => _dirty.AsSpan(0, _dirtyCount);

    /// <summary>The candidate at <paramref name="slot"/> as a mutable reference — the Renderer applies a
    /// <see cref="SlotPatch"/> to the CPU mirror through here during its per-copy sync (spec §5).</summary>
    public ref SceneCandidate CandidateAt(int slot) => ref _candidates[slot];

    /// <summary>
    /// Structural rebuild (spec §6): rewrites the candidate array and both batch tables from the material-major
    /// <b>sorted</b> render items (the caller sorts; this does not), assigning each candidate its
    /// <see cref="SceneCandidate.SceneBatchId"/> / <see cref="SceneCandidate.ShadowBatchId"/> / flags, then bumps
    /// <see cref="StructuralVersion"/> and drops the (now subsumed) dirty queue.
    /// </summary>
    public void Rebuild(ReadOnlySpan<RenderItem> sorted)
    {
        _count = sorted.Length;
        EnsureCandidateCapacity(_count);
        for (var i = 0; i < _count; i++)
        {
            _candidates[i] = new SceneCandidate
            {
                Model = sorted[i].WorldTransform,
                Sphere = sorted[i].CameraRelativeSphere,
                Flags = sorted[i].Flags,
            };
        }

        BuildSceneBatches(sorted);
        BuildShadowBatches(sorted);

        StructuralVersion++;
        _dirtyCount = 0;
        Structural = true;
    }

    /// <summary>Incremental path: queue a moved drawable's slot for a model/sphere patch (spec §6). Marks the set
    /// non-structural. The Renderer drains <see cref="Dirty"/> into the mirror + GPU copies during its sync.</summary>
    public void EnqueueDirty(int slot, in Matrix4x4 model, in Vector4 sphere)
    {
        if (_dirtyCount == _dirty.Length)
        {
            Array.Resize(ref _dirty, _dirty.Length == 0 ? 64 : _dirty.Length * 2);
        }

        _dirty[_dirtyCount++] = new SlotPatch(slot, model, sphere);
        Structural = false;
    }

    /// <summary>Begins an incremental frame: drops the dirty queue (without releasing its backing array) and marks
    /// the set non-structural — so <see cref="Structural"/> reads <c>false</c> even on a static frame that queues no
    /// patch at all (the Renderer's cue to replay rather than full-rewrite).</summary>
    public void ClearDirty()
    {
        _dirtyCount = 0;
        Structural = false;
    }

    // Material-major runs: consecutive items sharing (material, mesh) → one batch; each candidate's SceneBatchId is
    // its run index. Identical grouping to the old Renderer.BuildBatches, now producing the persistent table.
    private void BuildSceneBatches(ReadOnlySpan<RenderItem> sorted)
    {
        _sceneBatchCount = 0;
        var start = 0;
        while (start < sorted.Length)
        {
            var material = sorted[start].Material;
            var mesh = sorted[start].Mesh;
            var end = start + 1;
            while (end < sorted.Length && sorted[end].Material == material && sorted[end].Mesh == mesh)
            {
                end++;
            }

            AppendSceneBatch(new RenderBatch(mesh, material, (uint)start, (uint)(end - start)));
            var id = (uint)(_sceneBatchCount - 1);
            for (var k = start; k < end; k++)
            {
                _candidates[k].SceneBatchId = id;
            }

            start = end;
        }
    }

    // Mesh-major over CASTERS only (flags bit 0): the depth pass binds no material, and a caster may land in several
    // cascades, so its region is keyed by mesh. meshBatchBase = prefix sum of per-mesh caster counts; each caster's
    // ShadowBatchId is its mesh's first-seen index. Non-casters get no region (the shadow cull skips them by flag).
    private void BuildShadowBatches(ReadOnlySpan<RenderItem> sorted)
    {
        var distinct = 0;
        for (var i = 0; i < sorted.Length; i++)
        {
            if ((_candidates[i].Flags & SceneCandidate.FlagCastsShadow) == 0)
            {
                continue;
            }

            var b = IndexOfShadowMesh(sorted[i].Mesh, distinct);
            if (b < 0)
            {
                EnsureShadowScratch(distinct + 1);
                _shadowMeshes[distinct] = sorted[i].Mesh;
                _shadowCounts[distinct] = 1;
                distinct++;
            }
            else
            {
                _shadowCounts[b]++;
            }
        }

        EnsureShadowBatchCapacity(distinct);
        var running = 0u;
        for (var b = 0; b < distinct; b++)
        {
            _shadowBatches[b] = new RenderBatch(_shadowMeshes[b], MaterialHandle.Invalid, running, _shadowCounts[b]);
            running += _shadowCounts[b];
        }

        _shadowBatchCount = distinct;
        _totalCasters = (int)running;

        for (var i = 0; i < sorted.Length; i++)
        {
            if ((_candidates[i].Flags & SceneCandidate.FlagCastsShadow) != 0)
            {
                _candidates[i].ShadowBatchId = (uint)IndexOfShadowMesh(sorted[i].Mesh, distinct);
            }
        }
    }

    private int IndexOfShadowMesh(MeshHandle mesh, int distinct)
    {
        for (var b = 0; b < distinct; b++)
        {
            if (_shadowMeshes[b] == mesh)
            {
                return b;
            }
        }

        return -1;
    }

    private void AppendSceneBatch(in RenderBatch batch)
    {
        if (_sceneBatchCount == _sceneBatches.Length)
        {
            Array.Resize(ref _sceneBatches, _sceneBatches.Length == 0 ? 16 : _sceneBatches.Length * 2);
        }

        _sceneBatches[_sceneBatchCount++] = batch;
    }

    private void EnsureCandidateCapacity(int needed)
    {
        if (_candidates.Length < needed)
        {
            Array.Resize(ref _candidates, Math.Max(needed, _candidates.Length == 0 ? 1024 : _candidates.Length * 2));
        }
    }

    private void EnsureShadowScratch(int needed)
    {
        if (_shadowMeshes.Length < needed)
        {
            var cap = Math.Max(needed, _shadowMeshes.Length == 0 ? 16 : _shadowMeshes.Length * 2);
            Array.Resize(ref _shadowMeshes, cap);
            Array.Resize(ref _shadowCounts, cap);
        }
    }

    private void EnsureShadowBatchCapacity(int needed)
    {
        if (_shadowBatches.Length < needed)
        {
            Array.Resize(ref _shadowBatches, Math.Max(needed, _shadowBatches.Length == 0 ? 16 : _shadowBatches.Length * 2));
        }
    }
}
