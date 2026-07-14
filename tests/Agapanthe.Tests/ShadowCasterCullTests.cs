using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Rendering;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// The two-pass shadow-caster cull (P3-M2 D3): the bounded wedge (pass 1) breaks the circularity between the light
/// fit and the caster cull, and its upstream cut plane is what stops a far-away entity from wrecking the shadow
/// map's depth precision. These are the assertions that decide whether D3 fixed the latent bug or merely moved it.
/// </summary>
[Collection("World")]
public sealed class ShadowCasterCullTests
{
    private const uint Resolution = 4096;
    private const float ShadowDistance = 100f;
    private const float ShadowCasterDistance = 100f;

    // Sun straight down, so "upstream" (toward the source) is +Y — an easy axis to place a pathological caster on.
    private static readonly Vector3 Sun = new(0f, -1f, 0f);

    private static RenderView ViewAtOrigin()
    {
        var v = MathHelpers.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        var proj = MathHelpers.PerspectiveVulkan(MathF.PI / 3f, 1f, 0.1f, ShadowDistance);
        return new RenderView(Double3.Zero, Vector3.Zero, in v, in proj, MathF.PI / 3f, 1f, 0.1f, ShadowDistance);
    }

    private static ImportedEntitySpec Caster(Double3 position, uint order, int mesh = 0)
        => new(new MeshHandle(mesh, 1), new MaterialHandle(0, 1), position, Matrix4x4.Identity, Vector3.Zero, 1f, order);

    // The bounded wedge the engine builds each frame (D3.a), anchored on the frustum sphere the fit uses.
    private static ExtrudedShadowFrustum Wedge(in RenderView view, in Frustum cameraFrustum)
    {
        var (anchorCenter, anchorRadius) = ShadowFit.FitFrustumSphere(in view, ShadowDistance);
        return ExtrudedShadowFrustum.FromCameraFrustum(
            in cameraFrustum, Sun, anchorCenter, anchorRadius, ShadowCasterDistance);
    }

    [Fact]
    public void FarUpstreamCaster_DoesNotBlowOutTheDepthRange_AndIsCulled()
    {
        // THE test (spec §5, D3.a). A caster 10 000 km upstream of the sun sits inside the UNBOUNDED wedge and, left
        // there, would drive UpstreamExtent — and thus the shadow map's depth range — to ~1e7 m, collapsing its
        // precision. The bounded wedge must (i) keep the depth range finite despite that entity being in the scene
        // bounds, and (ii) drop the entity from the caster list. Its lost shadow is the WANTED behaviour.
        using var world = new GameWorld();
        world.SpawnImported(Caster(new Double3(0, 0, -10), 0));        // A: a normal caster, in view
        world.SpawnImported(Caster(new Double3(0, 10_000_000, 0), 1)); // B: 10 000 km straight up = far upstream

        var view = ViewAtOrigin();
        var cameraFrustum = Frustum.FromViewProjection(view.View * view.Projection);
        var wedge = Wedge(in view, in cameraFrustum);

        var render = new RenderList();
        var shadow = new RenderList();
        world.CollectRenderLists(render, shadow, in view, in cameraFrustum, in wedge, out var casterBounds);

        var sceneBounds = world.AggregateBounds(); // huge: it includes B at 1e7

        // (i) The depth range is fitted to the CASTER bounds (D3.b), which the wedge cut kept small — NOT to the
        // scene bounds, which B blew up to 1e7. The contrast is the whole point: same scene, two orders of magnitude.
        _ = ShadowFit.ComputeLightViewProj(
            in view, in sceneBounds, in casterBounds, Sun, ShadowDistance, Resolution, out var eyeDistance);
        _ = ShadowFit.ComputeLightViewProj(
            in view, in sceneBounds, in sceneBounds, Sun, ShadowDistance, Resolution, out var eyeDistanceIfUnbounded);

        Assert.True(eyeDistanceIfUnbounded > 1e6f,
            $"the old behaviour (fit on scene bounds) should blow up; got {eyeDistanceIfUnbounded}");
        Assert.True(eyeDistance < ShadowCasterDistance * 10f,
            $"the depth range must stay bounded by the shadow-caster distance; got {eyeDistance}");

        // (ii) B is absent from the final caster list; A survives both passes.
        var lightFrustum = Frustum.FromViewProjection(ShadowFit.ComputeLightViewProj(
            in view, in sceneBounds, in casterBounds, Sun, ShadowDistance, Resolution, out _));
        world.CompactShadowCasters(shadow, in lightFrustum);

        Assert.All(shadow.Items.ToArray(), item =>
            Assert.True(item.WorldTransform.M42 < 1e6f, "the far-upstream caster must not be in the final shadow list"));
        Assert.True(shadow.Count >= 1, "the in-view caster must remain a shadow caster");
    }

    [Fact]
    public void InViewCaster_SurvivesBothPasses()
    {
        // The complement of the test above: a normal caster is NOT dropped by the two-pass cull (no false negative).
        using var world = new GameWorld();
        world.SpawnImported(Caster(new Double3(0, 0, -10), 0));

        var view = ViewAtOrigin();
        var cameraFrustum = Frustum.FromViewProjection(view.View * view.Projection);
        var wedge = Wedge(in view, in cameraFrustum);

        var render = new RenderList();
        var shadow = new RenderList();
        world.CollectRenderLists(render, shadow, in view, in cameraFrustum, in wedge, out var casterBounds);
        Assert.Equal(1, shadow.Count); // in the wedge

        var sceneBounds = world.AggregateBounds();
        var lightFrustum = Frustum.FromViewProjection(ShadowFit.ComputeLightViewProj(
            in view, in sceneBounds, in casterBounds, Sun, ShadowDistance, Resolution, out _));
        world.CompactShadowCasters(shadow, in lightFrustum);

        Assert.Equal(1, shadow.Count); // still there after the light-volume compaction
    }

    [Fact]
    public void FinalCasterList_IsSortedMeshMajor_SoInstancedRunsAreContiguous()
    {
        // D3.c: the compaction (pass 2) happens BEFORE the sort, so the mesh-major run order the instanced depth
        // draw relies on (P3-M1) survives. Interleave two meshes at spawn; the final list must come out grouped.
        using var world = new GameWorld();
        for (var i = 0u; i < 8; i++)
        {
            // A tight cluster safely inside the frustum, meshes interleaved 0,1,0,1… at spawn so a mesh-major sort
            // has real work to do.
            world.SpawnImported(Caster(new Double3((i * 0.4) - 1.4, 0, -6), order: i, mesh: (int)(i % 2)));
        }

        var view = ViewAtOrigin();
        var cameraFrustum = Frustum.FromViewProjection(view.View * view.Projection);
        var wedge = Wedge(in view, in cameraFrustum);

        var render = new RenderList();
        var shadow = new RenderList();
        world.CollectRenderLists(render, shadow, in view, in cameraFrustum, in wedge, out var casterBounds);

        var sceneBounds = world.AggregateBounds();
        var lightFrustum = Frustum.FromViewProjection(ShadowFit.ComputeLightViewProj(
            in view, in sceneBounds, in casterBounds, Sun, ShadowDistance, Resolution, out _));
        world.CompactShadowCasters(shadow, in lightFrustum);

        // Mesh index is non-decreasing across the sorted list: all of mesh 0, then all of mesh 1 (one run each).
        var items = shadow.Items;
        Assert.Equal(8, items.Length);
        for (var i = 1; i < items.Length; i++)
        {
            Assert.True(items[i].Mesh.Index >= items[i - 1].Mesh.Index,
                "the shadow list must be mesh-major (contiguous instanced runs) after pass 2's sort");
        }
    }
}
