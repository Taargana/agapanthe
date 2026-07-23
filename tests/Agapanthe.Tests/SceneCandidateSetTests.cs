using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Tests;

/// <summary>
/// The persistent scene-candidate set (P3-M6 AW-003): a structural rebuild turns a material-major sorted item
/// list into the candidate array + the two batch tables (scene material-major, shadow mesh-major over casters),
/// assigning each candidate its batch ids and flags. Getting the shadow histogram / prefix sum wrong would make
/// the GPU shadow cull compact into the wrong region — silently, with no validation error.
/// </summary>
public sealed class SceneCandidateSetTests
{
    private static RenderItem Item(int mesh, int material, uint order, bool castsShadow)
        => new(
            Matrix4x4.CreateTranslation(order, 0, 0),
            new MeshHandle(mesh, 1),
            new MaterialHandle(material, 1),
            RenderItem.ComposeSortKey(material, mesh, order),
            new Vector4(order, 0, 0, 1),
            castsShadow ? SceneCandidate.FlagCastsShadow : 0u);

    [Fact]
    public void Rebuild_BuildsMaterialMajorSceneBatches_AndAssignsSceneBatchId()
    {
        // Material-major sorted: (m0,mesh0)x2, (m0,mesh1)x1, (m1,mesh0)x3.
        var items = new[]
        {
            Item(0, 0, 0, true), Item(0, 0, 1, true),
            Item(1, 0, 2, true),
            Item(0, 1, 3, true), Item(0, 1, 4, true), Item(0, 1, 5, true),
        };

        var set = new SceneCandidateSet();
        set.Rebuild(items);

        Assert.True(set.Structural);
        Assert.Equal(6, set.Count);
        Assert.Equal(3, set.SceneBatches.Length);
        Assert.Equal((0u, 2u), (set.SceneBatches[0].Offset, set.SceneBatches[0].Count));
        Assert.Equal((2u, 1u), (set.SceneBatches[1].Offset, set.SceneBatches[1].Count));
        Assert.Equal((3u, 3u), (set.SceneBatches[2].Offset, set.SceneBatches[2].Count));

        var ids = new uint[6];
        for (var i = 0; i < 6; i++)
        {
            ids[i] = set.Candidates[i].SceneBatchId;
        }

        Assert.Equal(new uint[] { 0, 0, 1, 2, 2, 2 }, ids);
    }

    [Fact]
    public void Rebuild_BuildsMeshMajorShadowBatches_OverCastersOnly_WithPrefixSumBase()
    {
        // Material-major, mesh-sorted within: mesh0 {cast, cast, NON-cast}, mesh1 {cast, cast, cast}.
        var items = new[]
        {
            Item(0, 0, 0, true), Item(0, 0, 1, true), Item(0, 0, 2, castsShadow: false),
            Item(1, 0, 3, true), Item(1, 0, 4, true), Item(1, 0, 5, true),
        };

        var set = new SceneCandidateSet();
        set.Rebuild(items);

        // Two mesh-batches; mesh0 has 2 casters (the non-caster is excluded), mesh1 has 3. Bases are the prefix sum.
        Assert.Equal(2, set.ShadowBatches.Length);
        Assert.Equal((0u, 2u), (set.ShadowBatches[0].Offset, set.ShadowBatches[0].Count)); // mesh0: base 0, 2 casters
        Assert.Equal((2u, 3u), (set.ShadowBatches[1].Offset, set.ShadowBatches[1].Count)); // mesh1: base 2, 3 casters
        Assert.Equal(5, set.TotalCasters);

        // Casters carry their mesh-batch index; the non-caster keeps its flag clear.
        Assert.Equal(0u, set.Candidates[0].ShadowBatchId);
        Assert.Equal(0u, set.Candidates[1].ShadowBatchId);
        Assert.Equal(0u, set.Candidates[2].Flags & SceneCandidate.FlagCastsShadow);
        Assert.Equal(1u, set.Candidates[3].ShadowBatchId);
        Assert.Equal(1u, set.Candidates[5].ShadowBatchId);
    }

    [Fact]
    public void EnqueueDirty_AppendsPatches_AndMarksNonStructural()
    {
        var set = new SceneCandidateSet();
        set.Rebuild(new[] { Item(0, 0, 0, true), Item(0, 0, 1, true) });
        var versionAfterRebuild = set.StructuralVersion;

        set.EnqueueDirty(1, Matrix4x4.CreateTranslation(9, 0, 0), new Vector4(9, 0, 0, 1));

        Assert.False(set.Structural);
        Assert.Equal(1, set.Dirty.Length);
        Assert.Equal(1, set.Dirty[0].Slot);
        Assert.Equal(versionAfterRebuild, set.StructuralVersion); // a dirty patch never bumps the structural version
    }

    [Fact]
    public void Rebuild_BumpsVersion_AndSubsumesDirty()
    {
        var set = new SceneCandidateSet();
        set.Rebuild(new[] { Item(0, 0, 0, true) });
        var v0 = set.StructuralVersion;
        set.EnqueueDirty(0, Matrix4x4.Identity, Vector4.Zero);

        set.Rebuild(new[] { Item(0, 0, 0, true), Item(0, 0, 1, true) });

        Assert.True(set.Structural);
        Assert.Equal(v0 + 1, set.StructuralVersion);
        Assert.Equal(0, set.Dirty.Length); // the rebuild rewrote everything → pending patches are moot
    }

    [Fact]
    public void Rebuild_IsZeroAlloc_OnSteadyCapacity()
    {
        var items = new RenderItem[256];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = Item(i % 3, i % 2, (uint)i, castsShadow: i % 4 != 0);
        }

        var set = new SceneCandidateSet();
        set.Rebuild(items); // warm up: grows every backing array once

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var f = 0; f < 8; f++)
        {
            set.Rebuild(items);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
