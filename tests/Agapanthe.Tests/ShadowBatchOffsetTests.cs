using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

/// <summary>
/// The CSM atlas's instance-offset arithmetic (P3-M5). The four cascades' caster lists are CONCATENATED into one
/// instance buffer, and each mesh run's draw carries <c>cascadeBase + runStart</c> as a vertex push constant. Get
/// that arithmetic wrong and the depth pass reads another cascade's matrices — silently, with no validation error
/// and no test failure anywhere else; only a human squinting at a shadow would notice.
/// <para>
/// This is the zone that already produced a bug during implementation (args uploaded per cascade, overwriting
/// regions earlier draws still pointed at), and the audit flagged it as the riskiest code with no coverage.
/// </para>
/// </summary>
public sealed class ShadowBatchOffsetTests
{
    private static RenderItem Caster(int mesh, uint order)
        => new(
            Matrix4x4.CreateTranslation(order, 0, 0), new MeshHandle(mesh, 1), new MaterialHandle(0, 1),
            RenderItem.ComposeShadowSortKey(mesh, 0, order));

    // Builds a mesh-major list of `count` items alternating between two meshes, already sorted the way
    // CollectShadowCasters leaves it (all of mesh 0, then all of mesh 1).
    private static RenderList List(int mesh0Count, int mesh1Count, uint orderBase)
    {
        var list = new RenderList();
        for (var i = 0; i < mesh0Count; i++)
        {
            list.Add(Caster(0, orderBase + (uint)i));
        }

        for (var i = 0; i < mesh1Count; i++)
        {
            list.Add(Caster(1, orderBase + (uint)(mesh0Count + i)));
        }

        return list;
    }

    [Fact]
    public void BuildShadowBatches_SplitsRunsByMesh()
    {
        var batches = new Renderer.Batch[8];
        var list = List(3, 2, 0);

        var count = Renderer.BuildShadowBatches(list.Items, ref batches);

        Assert.Equal(2, count);
        Assert.Equal(0u, batches[0].Offset);
        Assert.Equal(3u, batches[0].Count);
        Assert.Equal(3u, batches[1].Offset);
        Assert.Equal(2u, batches[1].Count);
    }

    [Fact]
    public void BuildShadowBatches_AppendsAtWriteIndexAndFoldsInTheCascadeBase()
    {
        // The real call shape: cascade 1's runs are appended after cascade 0's batches, and their offsets must be
        // absolute in the SHARED instance buffer (cascade base + run start), not local to the cascade's list.
        var batches = new Renderer.Batch[8];
        var cascade0 = List(2, 1, 0);   // 3 casters at buffer [0..2]
        var cascade1 = List(1, 2, 10);  // 3 casters at buffer [3..5]

        var n0 = Renderer.BuildShadowBatches(cascade0.Items, ref batches);
        var n1 = Renderer.BuildShadowBatches(cascade1.Items, ref batches, n0, (uint)cascade0.Count);

        Assert.Equal(2, n0);
        Assert.Equal(2, n1);

        // Cascade 0 keeps absolute offsets 0 and 2.
        Assert.Equal(0u, batches[0].Offset);
        Assert.Equal(2u, batches[1].Offset);

        // Cascade 1's runs start at 3 (its base) and 4 (base + its own run start of 1) — NOT 0 and 1.
        Assert.Equal(3u, batches[2].Offset);
        Assert.Equal(1u, batches[2].Count);
        Assert.Equal(4u, batches[3].Offset);
        Assert.Equal(2u, batches[3].Count);

        // Earlier cascades' batches survive the append (the array may have been resized in between).
        Assert.Equal(2u, batches[0].Count);
    }

    [Fact]
    public void BuildShadowBatches_OffsetsIndexTheConcatenatedBuffer()
    {
        // The invariant that actually matters: for every batch, the instance buffer entry at batches[b].Offset is
        // the FIRST item of that run. Reproduces the concatenation RecordShadowPass performs, then checks that
        // following each batch's offset into it lands on the right RenderItem.
        var lists = new[] { List(2, 2, 0), List(1, 1, 100), List(3, 0, 200) };

        var concat = new List<RenderItem>();
        var batches = new Renderer.Batch[16];
        var total = 0;
        foreach (var list in lists)
        {
            var basis = (uint)concat.Count;
            total += Renderer.BuildShadowBatches(list.Items, ref batches, total, basis);
            concat.AddRange(list.Items.ToArray());
        }

        // (2 mesh0, 2 mesh1) -> 2 runs; (1, 1) -> 2 runs; (3, 0) -> 1 run. Five runs across the three cascades.
        Assert.Equal(5, total);

        for (var b = 0; b < total; b++)
        {
            ref readonly var batch = ref batches[b];
            Assert.True(
                batch.Offset + batch.Count <= (uint)concat.Count,
                $"batch {b} runs past the concatenated buffer ({batch.Offset}+{batch.Count} > {concat.Count})");

            // Every item in the run shares the batch's mesh — i.e. the offset really points at that run.
            for (var i = 0u; i < batch.Count; i++)
            {
                Assert.Equal(batch.Mesh, concat[(int)(batch.Offset + i)].Mesh);
            }
        }
    }

    [Fact]
    public void BuildShadowBatches_EmptyCascadeAppendsNothing()
    {
        var batches = new Renderer.Batch[4];
        var first = Renderer.BuildShadowBatches(List(1, 0, 0).Items, ref batches);
        var second = Renderer.BuildShadowBatches(new RenderList().Items, ref batches, first, 1);

        Assert.Equal(1, first);
        Assert.Equal(0, second); // an empty cascade contributes no batch, and does not disturb the previous ones
        Assert.Equal(0u, batches[0].Offset);
    }
}
