using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free tests for the M2 systems (spec §3.5): transform propagation (system 1, incl. cycle detection) and
/// world-bounds aggregation (system 2).
/// </summary>
[Collection("World")]
public sealed class WorldSystemsTests
{
    private static Vector3 Translation(Matrix4x4 m) => new(m.M41, m.M42, m.M43);

    // A permissive frustum + view for tests that exercise the transform/aggregation, not the culling: it contains
    // everything near the frame origin, so CollectRenderLists keeps every drawable. (Culling itself is covered by
    // FrustumTests and the render-side captures.)
    private static readonly Frustum Wide = Frustum.FromViewProjection(
        MathHelpers.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
        * MathHelpers.OrthographicVulkan(1000f, 1000f, -500f, 500f));

    private static RenderView ViewAt(Double3 origin)
        => new(origin, Vector3.Zero, Matrix4x4.Identity, Matrix4x4.Identity, 1f, 1f, 0.1f, 1f);

    // A real (narrow) camera frustum at the origin, looking down -Z, 60° FOV, near 0.1 / far 100.
    private static readonly Frustum Camera = Frustum.FromViewProjection(
        MathHelpers.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
        * MathHelpers.PerspectiveVulkan(MathF.PI / 3f, 1f, 0.1f, 100f));

    // A degenerate extruded frustum (zero light direction → every plane dropped) that keeps every caster: for the
    // tests that exercise transform/aggregation/camera-culling, not the shadow-caster tightening (P3-M1, debt #2).
    private static readonly ExtrudedShadowFrustum AllCasters =
        ExtrudedShadowFrustum.FromCameraFrustum(in Wide, Vector3.Zero);

    private static ImportedEntitySpec Drawable(Double3 position, float radius, uint order)
        => new(new MeshHandle(0, 1), new MaterialHandle(0, 1), position, Matrix4x4.Identity,
            Vector3.Zero, radius, order);

    // Runs the collect + the two-pass shadow cull (P3-M2 D3.c): CollectRenderLists fills the render list with ALL
    // scene candidates (the camera cull is a GPU pass since P3-M4 — not tested here) and the shadow list (wedge
    // cull, a superset), then pass 2 compacts the shadow list against the light volume. The shadow-list assertions
    // describe the FINAL caster set — the "light volume ∧ extruded wedge" AND, split across the two passes.
    private static void Cull(
        GameWorld world, RenderList render, RenderList shadow, in RenderView view,
        in Frustum lightFrustum, in ExtrudedShadowFrustum wedge)
    {
        world.CollectRenderLists(render, shadow, in view, in wedge, out _);
        world.CompactShadowCasters(shadow, in lightFrustum);
    }

    [Fact]
    public void Propagate_Root_UsesItsOwnLocalTransform()
    {
        using var world = new GameWorld();
        var root = world.SpawnLocalRoot(new Double3(5, 0, 0), Quaternion.Identity, 1f);

        world.PropagateTransforms();

        Assert.Equal(new Vector3(5, 0, 0), Translation(world.GetWorld(root)));
    }

    [Fact]
    public void Propagate_Child_ComposesWithParent()
    {
        using var world = new GameWorld();
        var root = world.SpawnLocalRoot(new Double3(5, 0, 0), Quaternion.Identity, 1f);
        var child = world.SpawnLocalChild(root, new Double3(0, 2, 0), Quaternion.Identity, 1f);

        world.PropagateTransforms();

        // child local (0,2,0) composed with parent (5,0,0) -> world (5,2,0)
        Assert.Equal(new Vector3(5, 2, 0), Translation(world.GetWorld(child)));
    }

    [Fact]
    public void Propagate_Grandchild_ComposesWholeChain()
    {
        using var world = new GameWorld();
        var root = world.SpawnLocalRoot(new Double3(1, 0, 0), Quaternion.Identity, 1f);
        var child = world.SpawnLocalChild(root, new Double3(0, 1, 0), Quaternion.Identity, 1f);
        var grand = world.SpawnLocalChild(child, new Double3(0, 0, 1), Quaternion.Identity, 1f);

        world.PropagateTransforms();

        Assert.Equal(new Vector3(1, 1, 1), Translation(world.GetWorld(grand)));
    }

    [Fact]
    public void Propagate_ParentScale_ScalesChildOffset()
    {
        using var world = new GameWorld();
        var root = world.SpawnLocalRoot(Double3.Zero, Quaternion.Identity, 2f);
        var child = world.SpawnLocalChild(root, new Double3(3, 0, 0), Quaternion.Identity, 1f);

        world.PropagateTransforms();

        // The child's local offset is scaled by the parent's scale: 3 * 2 = 6.
        Assert.Equal(new Vector3(6, 0, 0), Translation(world.GetWorld(child)));
    }

    [Fact]
    public void Propagate_Cycle_ThrowsWithPath()
    {
        using var world = new GameWorld();
        var a = world.SpawnLocalRoot(Double3.Zero, Quaternion.Identity, 1f);
        var b = world.SpawnLocalChild(a, Double3.Zero, Quaternion.Identity, 1f);
        world.SetParent(a, b); // a -> b -> a : cycle (deferred; the barrier applies it)
        world.FlushStructuralChanges();

        var ex = Assert.Throws<WorldHierarchyException>(world.PropagateTransforms);
        Assert.Contains("Cycle in entity hierarchy", ex.Message);
        Assert.Contains("->", ex.Message); // the offending chain is reported
    }

    [Fact]
    public void Propagate_DoesNotTouchImportedEntities()
    {
        // Imported drawables carry a BAKED transform and no LocalTransform, so system 1 must never recompute
        // it (spec §6 condition a). Recombined at a zero origin, the split (Double3 position + rotation/scale
        // matrix) reproduces the asset's matrix BIT-FOR-BIT — a float widened to double and narrowed back is
        // the identical float.
        using var world = new GameWorld();
        var baked = Matrix4x4.CreateScale(3f) * Matrix4x4.CreateTranslation(7, 8, 9);
        var rotationScale = baked with { M41 = 0f, M42 = 0f, M43 = 0f };
        world.SpawnImported(new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), new Double3(7, 8, 9), rotationScale,
            Vector3.Zero, 1f, 0));

        world.PropagateTransforms();

        var list = new RenderList();
        Cull(world, list, new RenderList(), ViewAt(Double3.Zero), in Wide, in AllCasters);
        Assert.Equal(baked, list.Items[0].WorldTransform); // bit-for-bit unchanged
    }

    [Fact]
    public void CollectRenderLists_SubtractsTheOrigin_SoFarOutIsIdenticalToTheOrigin()
    {
        // THE M3 invariant (spec §3.3): the same scene, rendered from the same relative viewpoint, must produce
        // the SAME model matrices whether it sits at the world origin or 10 000 km away. Absolute float
        // positions cannot do this — at 1e7 m consecutive floats are ~1 m apart, so the geometry would visibly
        // snap to a metre grid. Because the subtraction happens in double, the two agree bit-for-bit.
        var far = new Double3(1e7, 1e7, 1e7);

        using var near = new GameWorld();
        near.SpawnImported(new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), new Double3(1.5, -2.25, 0.125), Matrix4x4.Identity,
            Vector3.Zero, 0f, 0));

        using var farAway = new GameWorld();
        farAway.SpawnImported(new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), far + new Double3(1.5, -2.25, 0.125),
            Matrix4x4.Identity, Vector3.Zero, 0f, 0));

        var atOrigin = new RenderList();
        Cull(near, atOrigin, new RenderList(), ViewAt(Double3.Zero), in Wide, in AllCasters);

        var atFar = new RenderList();
        Cull(farAway, atFar, new RenderList(), ViewAt(far), in Wide, in AllCasters);

        Assert.Equal(atOrigin.Items[0].WorldTransform, atFar.Items[0].WorldTransform);
    }

    [Fact]
    public void Propagate_FarOutRoot_KeepsItsPositionExact()
    {
        // A root's position is accumulated in double against an identity matrix, so it passes through untouched.
        // Narrowed to float first, 10 000 000.5 would have been rounded to 10 000 000 — half a metre of error
        // before a single frame is drawn.
        using var world = new GameWorld();
        var root = world.SpawnLocalRoot(new Double3(10_000_000.5, 0, 0), Quaternion.Identity, 1f);
        var child = world.SpawnLocalChild(root, new Double3(0, 2, 0), Quaternion.Identity, 1f);

        world.PropagateTransforms();

        Assert.Equal(10_000_000.5, world.GetWorldPosition(root).X);
        Assert.Equal(new Double3(10_000_000.5, 2, 0), world.GetWorldPosition(child));

        // And relative to an eye sitting next to it, the child is exactly 2 m up — no float grid in sight.
        var eye = new Double3(10_000_000.5, 0, 0);
        Assert.Equal(new Vector3(0f, 2f, 0f), Translation(world.GetWorld(child, eye)));
    }

    [Fact]
    public void AggregateBounds_UnionsTheWorldSpheres()
    {
        // Each entity's LOCAL sphere is transformed to world (centre = local centre + WorldPosition, since the
        // rotation/scale here is identity) and its enclosing box is unioned into the extent.
        using var world = new GameWorld();
        world.SpawnImported(new ImportedEntitySpec(       // world sphere (0,0,0) r1 -> [-1,1]^3
            new MeshHandle(0, 1), new MaterialHandle(0, 1), Double3.Zero, Matrix4x4.Identity,
            Vector3.Zero, 1f, 0));
        world.SpawnImported(new ImportedEntitySpec(       // world sphere (10,0,0) r2 -> [8,12]x[-2,2]x[-2,2]
            new MeshHandle(1, 1), new MaterialHandle(0, 1), new Double3(10, 0, 0), Matrix4x4.Identity,
            Vector3.Zero, 2f, 1));

        var bounds = world.AggregateBounds();

        Assert.Equal(new Double3(-1, -2, -2), bounds.Min);
        Assert.Equal(new Double3(12, 2, 2), bounds.Max);
    }

    [Fact]
    public void AggregateBounds_GrowsTheRadiusByTheTransformScale()
    {
        // A local sphere of radius 1, on an entity scaled x3, must aggregate to a world sphere of radius 3 — the
        // conservative max-axis-scale growth, so a scaled object is never under-covered.
        using var world = new GameWorld();
        world.SpawnImported(new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), Double3.Zero, Matrix4x4.CreateScale(3f),
            Vector3.Zero, 1f, 0));

        var bounds = world.AggregateBounds();

        Assert.Equal(new Double3(-3, -3, -3), bounds.Min);
        Assert.Equal(new Double3(3, 3, 3), bounds.Max);
    }

    [Fact]
    public void AggregateBounds_TracksAnEntityThatTranslates()
    {
        // Debt #1 (P3-M1): the extent must follow a moving entity. AggregateBounds recomputes from the live
        // WorldPosition every call, so translating the drawable and re-aggregating yields the moved box — this is
        // what makes per-frame aggregation correct (the M4 fold-once path went stale the moment anything moved).
        using var world = new GameWorld();
        world.SpawnImported(new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), Double3.Zero, Matrix4x4.Identity,
            Vector3.Zero, 1f, 0));

        Assert.Equal(new Double3(-1, -1, -1), world.AggregateBounds().Min); // world sphere (0,0,0) r1

        var move = new TestTranslate(new Double3(10, 0, 0));
        world.AnimateDrawables(ref move);
        var bounds = world.AggregateBounds();

        Assert.Equal(new Double3(9, -1, -1), bounds.Min);  // moved sphere (10,0,0) r1 -> [9,11]x[-1,1]x[-1,1]
        Assert.Equal(new Double3(11, 1, 1), bounds.Max);
    }

    [Fact]
    public void CollectRenderLists_EmitsEveryDrawableAsACandidateWithItsSphere()
    {
        // Since P3-M4 the scene is NOT CPU-culled: CollectRenderLists emits EVERY drawable as a candidate, each
        // carrying its camera-relative sphere, and the GPU compute pass does the frustum cull (the cull logic
        // itself is covered by FrustumTests; the GPU==CPU count is a headless render gate). So both drawables are
        // candidates here — front and behind — sorted by key, each with a correct sphere.
        using var world = new GameWorld();
        world.SpawnImported(Drawable(new Double3(0, 0, -10), 1f, 0)); // ahead of the camera
        world.SpawnImported(Drawable(new Double3(0, 0, 10), 2f, 1));  // behind the camera

        var render = new RenderList();
        var shadow = new RenderList();
        Cull(world, render, shadow, ViewAt(Double3.Zero), in Wide, in AllCasters);

        Assert.Equal(2, render.Count);                             // both are candidates now
        Assert.Equal(0ul, render.Items[0].SortKey);               // sorted: order 0 first
        // Each candidate carries its camera-relative sphere (w = radius): the front one at z -10 r 1, the back at
        // z +10 r 2. The eye is at the origin, so camera-relative centre == world position here.
        Assert.Equal(new Vector4(0, 0, -10, 1), render.Items[0].CameraRelativeSphere);
        Assert.Equal(new Vector4(0, 0, 10, 2), render.Items[1].CameraRelativeSphere);
        Assert.Equal(2, shadow.Count);                            // Wide light volume keeps both as casters
    }

    [Fact]
    public void CollectRenderLists_KeepsAnOffScreenCasterWhoseShadowEntersTheView()
    {
        // Anti-popping (spec §3.5, P3-M1 debt #2): a caster OUTSIDE the camera frustum whose shadow still reaches
        // the view must stay in the shadow-caster list. Here the light travels -Z, so a caster behind the camera
        // (at +Z) casts its shadow forward INTO the frustum → it must survive both the light volume AND the
        // extruded-frustum cull. The extruded frustum is the camera frustum swept toward the light, so this
        // upstream caster is inside it.
        using var world = new GameWorld();
        world.SpawnImported(Drawable(new Double3(0, 0, 10), 1f, 0)); // behind the camera, upstream of the light

        var extruded = ExtrudedShadowFrustum.FromCameraFrustum(in Camera, new Vector3(0f, 0f, -1f));
        var render = new RenderList();
        var shadow = new RenderList();
        Cull(world, render, shadow, ViewAt(Double3.Zero), in Wide, in extruded);

        Assert.Equal(1, render.Count);  // a scene candidate (the GPU will cull it — it is behind the camera)
        Assert.Equal(1, shadow.Count);  // and still a shadow caster (its shadow enters the view)
    }

    [Fact]
    public void CollectRenderLists_DropsACasterWhoseShadowMissesTheView()
    {
        // The tightening the extruded frustum buys (P3-M1 debt #2): a caster far BELOW the view, with the light
        // travelling straight down, casts its shadow further down — never into the frustum. The light volume
        // (Wide) would keep it, but ANDing the extruded frustum (its "bottom" plane is kept for a downward light)
        // drops it. This is the off-screen caster that used to bloat the shadow list on a flat scene.
        using var world = new GameWorld();
        world.SpawnImported(Drawable(new Double3(0, -100, -10), 1f, 0)); // far below, shadow goes further down

        var extruded = ExtrudedShadowFrustum.FromCameraFrustum(in Camera, new Vector3(0f, -1f, 0f));
        var render = new RenderList();
        var shadow = new RenderList();
        Cull(world, render, shadow, ViewAt(Double3.Zero), in Wide, in extruded);

        Assert.Equal(1, render.Count); // a scene candidate (the GPU will cull it — far below the view)
        Assert.Equal(0, shadow.Count); // dropped: its shadow never reaches the view
    }

    [Fact]
    public void CollectRenderLists_CandidateSphereIsCameraRelative()
    {
        // The candidate's sphere is narrowed against the frame origin (spec §3.3): the same entity at the origin
        // and 10 000 km out produces the SAME camera-relative sphere when viewed from the matching eye — which is
        // what lets the GPU cull run in float without losing metres. Here, viewed from a far origin, the sphere is
        // the small local offset, not the enormous absolute coordinate.
        var far = new Double3(1e7, 1e7, 1e7);
        using var world = new GameWorld();
        world.SpawnImported(Drawable(far + new Double3(0, 0, -10), 2f, 0));

        var render = new RenderList();
        Cull(world, render, new RenderList(), ViewAt(far), in Wide, in AllCasters);

        Assert.Equal(1, render.Count);
        Assert.Equal(new Vector4(0, 0, -10, 2), render.Items[0].CameraRelativeSphere);
    }

    [Fact]
    public void AggregateBounds_NoEntities_ReturnsEmpty()
    {
        using var world = new GameWorld();
        var bounds = world.AggregateBounds();

        // Empty seed: Min is +inf, Max is -inf. Callers must guard this degenerate extent.
        Assert.True(double.IsPositiveInfinity(bounds.Min.X));
        Assert.True(double.IsNegativeInfinity(bounds.Max.X));
    }

    [Fact]
    public void Systems_AreAllocationFreeInSteadyState()
    {
        using var world = new GameWorld();
        var root = world.SpawnLocalRoot(new Double3(1, 2, 3), Quaternion.Identity, 1f);
        world.SpawnLocalChild(root, new Double3(0, 1, 0), Quaternion.Identity, 1f);
        for (var i = 0; i < 32; i++)
        {
            world.SpawnImported(new ImportedEntitySpec(
                new MeshHandle(i, 1), new MaterialHandle(0, 1), new Double3(i, 0, 0), Matrix4x4.Identity,
                Vector3.Zero, 1f, (uint)i));
        }

        var render = new RenderList();
        var shadow = new RenderList();
        var spin = new TestSpin();

        // Warm up: first calls may grow the reused buffers (walk stack, render lists).
        for (var i = 0; i < 5; i++)
        {
            world.AnimateDrawables(ref spin);
            world.PropagateTransforms();
            _ = world.AggregateBounds();
            Cull(world, render, shadow, ViewAt(Double3.Zero), in Wide, in AllCasters);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            world.AnimateDrawables(ref spin); // the W4 path — its zero-alloc must be covered too (audit Med1)
            world.PropagateTransforms();
            _ = world.AggregateBounds();
            Cull(world, render, shadow, ViewAt(Double3.Zero), in Wide, in AllCasters);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated); // zero managed allocation per frame on the hot path
    }

    // Rotation-only animator (keeps the translation row zero): the AnimateDrawables path for the alloc test.
    private struct TestSpin : IDrawableAnimator
    {
        public void Animate(ulong globalId, ref Double3 position, ref Matrix4x4 rotationScale)
            => rotationScale = Matrix4x4.CreateRotationY(0.01f) * rotationScale;
    }

    // Translation animator: shifts the double WorldPosition (leaving rotation/scale untouched, translation row
    // zero). Used to prove AggregateBounds tracks a moving entity.
    private struct TestTranslate(Double3 delta) : IDrawableAnimator
    {
        public void Animate(ulong globalId, ref Double3 position, ref Matrix4x4 rotationScale)
            => position += delta;
    }
}
