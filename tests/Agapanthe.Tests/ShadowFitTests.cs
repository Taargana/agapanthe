using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

/// <summary>
/// The directional-shadow fit (spec §3.5), GPU-free. A wrong fit is nearly invisible in a capture — the image
/// still looks like a shadowed helmet — so it is asserted here rather than eyeballed.
/// </summary>
public sealed class ShadowFitTests
{
    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0.4f, -0.7f, -0.6f));
    private const uint Resolution = 2048;

    // Builds a RenderView DIRECTLY with the given origin and the eye at that origin (eyeRelative = 0), bypassing
    // Camera.CreateView's cell snapping — these tests exercise ShadowFit, not the M4 origin-quantization policy,
    // so they want an origin they control exactly.
    private static RenderView View(Double3 origin, float yaw = 0f, float near = 0.1f, float far = 1000f)
    {
        var camera = new Camera { Position = origin, Yaw = yaw, Near = near, Far = far, AspectRatio = 16f / 9f };
        var view = MathHelpers.LookAt(Vector3.Zero, camera.Forward, Vector3.UnitY);
        return new RenderView(
            origin, Vector3.Zero, in view, camera.ProjectionMatrix, camera.FovY, camera.AspectRatio, near, far);
    }

    // The extent of the fitted ortho box along the light's right axis, recovered from the matrix: the ortho scales
    // the fitted width to NDC [-1,1], so 2/|column| is that width. It is the quantity that decides shadow texel
    // density, i.e. shadow quality.
    private static float FittedWidth(Matrix4x4 lightViewProj)
        => 2f / new Vector3(lightViewProj.M11, lightViewProj.M21, lightViewProj.M31).Length();

    private static Double3Bounds Box(Double3 center, double halfSize)
        => new(
            center - new Double3(halfSize, halfSize, halfSize),
            center + new Double3(halfSize, halfSize, halfSize));

    // Since P3-M2 D3.b the fit takes scene bounds (footprint) and caster bounds (depth range) separately. These
    // tests predate that split and reason about ONE set of bounds, so we hand the same bounds to both — reproducing
    // the pre-D3 behaviour (footprint from the scene, depth range from the same geometry). The eyeDistance out is
    // dropped; the depth-range tests assert against the projected clip depth, not the raw distance.
    private static Matrix4x4 Fit(
        in RenderView view, in Double3Bounds bounds, Vector3 sun, float shadowDistance, uint resolution)
        => ShadowFit.ComputeLightViewProj(
            in view, in bounds, in bounds, sun, shadowDistance, resolution, out _);

    [Fact]
    public void SceneFit_IsStable_WhenTheSceneBoundsBreathe()
    {
        // Since P3-M1 the world bounds are recomputed EVERY frame, so an entity that spins or drifts makes the scene
        // sphere breathe and slide a little each frame. Left unquantized, the fit would then resample the whole
        // shadow map every frame and every shadow edge would crawl. Quantized, the map is a STEP function of the
        // scene: the radius never moves off its rung (so the texel grid it defines is fixed), and the centre only
        // jumps whole texels.
        var view = View(Double3.Zero);
        var widths = new HashSet<float>();
        var phases = new HashSet<(double, double)>();

        // Sweep the scene across ~8 texels in 64 steps, growing it slightly as we go. Unquantized, that is 64
        // distinct maps (a continuous slide). Quantized, the centre lands on a handful of grid positions.
        const float texel = 5f / Resolution; // 2·radius/resolution, radius ≈ 2.5 after quantization
        for (var step = 0; step < 64; step++)
        {
            var drift = step * (texel / 8.0);
            var breathed = Box(new Double3(drift, 0, 0), 1.2 + (drift * 0.01));
            var fit = Fit(in view, in breathed, Sun, 0f, Resolution);
            widths.Add(FittedWidth(fit));
            // Round off the ~1e-7 float noise of the round trip; the x/y phase is what crawls (z is not snapped).
            phases.Add((Math.Round(fit.M41, 6), Math.Round(fit.M42, 6)));
        }

        Assert.Single(widths); // the radius stays on one rung: the texel grid it defines never moves
        Assert.InRange(phases.Count, 1, 16); // a staircase of whole texels over the sweep, not 64 distinct maps
    }

    [Fact]
    public void SmallScene_FitsTheScene_NotTheMuchLargerFrustum()
    {
        // A helmet in a 1000 m frustum: fitting the frustum would spread the shadow map over ~1 km and leave the
        // helmet a handful of texels. The fit must stay on the scene.
        var view = View(Double3.Zero);
        var scene = Box(Double3.Zero, 1.2); // ~2.4 m across

        var lightViewProj = Fit(
            in view, in scene, Sun, 0f, Resolution);

        var width = FittedWidth(lightViewProj);
        Assert.InRange(width, 2f, 6f); // the scene's sphere (diagonal + 10% margin), not the frustum's
    }

    [Fact]
    public void LargeScene_FitsTheFrustum_NotTheWholeWorld()
    {
        // The M4 case: a world far larger than what the camera sees. Fitting the world would make every shadow
        // texel metres wide; the fit must fall back on the camera frustum, capped by the shadow distance.
        var view = View(Double3.Zero, far: 10_000f);
        var world = Box(Double3.Zero, 50_000); // 100 km across

        var lightViewProj = Fit(
            in view, in world, Sun, 100f, Resolution);

        var width = FittedWidth(lightViewProj);
        Assert.True(width < 1_000f, $"fitted width {width} should follow the 100 m shadow distance, not the 100 km world");
        Assert.True(width > 100f, $"fitted width {width} must still cover the frustum out to the shadow distance");
    }

    [Fact]
    public void ShadowDistance_CapsTheFit()
    {
        var world = Box(Double3.Zero, 50_000);

        var near = Fit(
            View(Double3.Zero, far: 10_000f), in world, Sun, 50f, Resolution);
        var far = Fit(
            View(Double3.Zero, far: 10_000f), in world, Sun, 200f, Resolution);

        Assert.True(
            FittedWidth(near) < FittedWidth(far),
            "a shorter shadow distance must produce a tighter fit (denser shadow texels)");
    }

    [Fact]
    public void FrustumFit_RadiusIsRotationInvariant_SoShadowsDoNotShimmerWhenLookingAround()
    {
        // The frustum is bounded by a SPHERE precisely so that turning the camera cannot change the fitted extent.
        // If it did, every shadow texel would resize each frame and the edges would crawl.
        var world = Box(Double3.Zero, 50_000);

        var facingForward = Fit(
            View(Double3.Zero, yaw: 0f, far: 10_000f), in world, Sun, 100f, Resolution);
        var turned = Fit(
            View(Double3.Zero, yaw: 1.1f, far: 10_000f), in world, Sun, 100f, Resolution);

        Assert.Equal(FittedWidth(facingForward), FittedWidth(turned), 3);
    }

    [Fact]
    public void FrustumFit_TexelSnap_KeepsAStaticPointStableWhenTheCameraCreepsForward()
    {
        // The audit's catch, and the reason the snap must be anchored to the WORLD: quantizing the
        // camera-relative centre is a no-op, because that centre does not depend on where the eye is. The shadow
        // map would then slide continuously with the camera and every static edge would crawl.
        // Here the camera creeps by a fraction of a texel per frame; the light-clip position of a FIXED world
        // point must stay put (it may jump by whole texels, never drift smoothly).
        var world = Box(Double3.Zero, 50_000);
        var worldPoint = new Double3(3, 0, -20); // a static object in front of the camera

        // texelSize = 2 * frustumRadius / resolution; the frustum here is ~100 m across, so a texel is centimetric.
        var step = 0.001;
        var projections = new List<Vector2>();
        for (var frame = 0; frame < 8; frame++)
        {
            var eye = new Double3(0, 0, -step * frame); // creeping forward, sub-texel each frame
            var view = View(eye, far: 10_000f);
            var lightViewProj = Fit(view, in world, Sun, 100f, Resolution);

            // The shadow pass receives camera-relative positions, so that is what we project.
            var relative = worldPoint.ToVector3(eye);
            var clip = Vector4.Transform(new Vector4(relative, 1f), lightViewProj);
            projections.Add(new Vector2(clip.X / clip.W, clip.Y / clip.W));
        }

        // One shadow texel in NDC is 2/resolution. A drifting grid would move the point a little EVERY frame;
        // a world-anchored one holds it to well under a texel.
        var texelNdc = 2f / Resolution;
        foreach (var p in projections)
        {
            Assert.True(
                Vector2.Distance(p, projections[0]) < texelNdc,
                $"the static point moved {Vector2.Distance(p, projections[0]) / texelNdc:F2} texels: the shadow grid is drifting with the camera");
        }
    }

    [Fact]
    public void FrustumFit_KeepsUpstreamCastersInsideTheDepthRange()
    {
        // A caster far behind the fitted sphere (along the light) still throws its shadow INTO it. If the light's
        // near plane does not clear it, it is clipped and simply stops casting — a shadow that vanishes with no
        // error anywhere. The old fixed [0.5r, 3.5r] range only cleared 0.5r of upstream world.
        var sun = Vector3.Normalize(new Vector3(0f, -1f, 0f)); // straight down
        var view = View(Double3.Zero, far: 10_000f);

        // A world 2 km tall: the top is far above anything the frustum sphere covers.
        var world = new Double3Bounds(new Double3(-50_000, 0, -50_000), new Double3(50_000, 2_000, 50_000));

        var lightViewProj = Fit(view, in world, sun, 100f, Resolution);

        // A caster at the very top of that world, above the camera, must land inside the light's depth range.
        var caster = new Vector3(0f, 1_999f, 0f);
        var clip = Vector4.Transform(new Vector4(caster, 1f), lightViewProj);
        var depth = clip.Z / clip.W;

        Assert.InRange(depth, 0f, 1f); // Vulkan clip depth: outside [0,1] means clipped, i.e. no shadow at all
    }

    [Fact]
    public void FrustumFit_IsCameraRelative_SoFarOutMatchesTheOriginToWithinATexel()
    {
        // Camera-relative (spec §3.3): 10 000 km out, with the world moved with it, the fit must be the same —
        // same extent, and a caster must project to the same place in the shadow map.
        // NOT bit-identical, and that is by design: the texel grid is anchored to the ABSOLUTE world (it has to
        // be, or it would drift with the camera — see the snap test above), so 1e7 m out lands on a different
        // phase of that grid. The residue is bounded by one texel, which is the price of a grid that does not
        // crawl. The render lists themselves remain exactly bit-identical; this is only the shadow map's phase.
        var far = new Double3(1e7, 1e7, 1e7);

        var atOrigin = Fit(
            View(Double3.Zero, far: 10_000f), Box(Double3.Zero, 50_000), Sun, 100f, Resolution);
        var atFar = Fit(
            View(far, far: 10_000f), Box(far, 50_000), Sun, 100f, Resolution);

        Assert.Equal(FittedWidth(atOrigin), FittedWidth(atFar), 3); // same extent = same shadow texel density

        // The same caster, expressed camera-relative in both frames, lands within a texel of the same shadow texel.
        var caster = new Vector3(4f, 1f, -25f);
        var here = Vector4.Transform(new Vector4(caster, 1f), atOrigin);
        var there = Vector4.Transform(new Vector4(caster, 1f), atFar);
        var drift = Vector2.Distance(
            new Vector2(here.X / here.W, here.Y / here.W),
            new Vector2(there.X / there.W, there.Y / there.W));

        Assert.True(drift < 2f / Resolution, $"drift of {drift * Resolution / 2f:F2} texels between the origin and 1e7 m");
    }

    [Fact]
    public void EmptyScene_ProducesAFiniteMatrix()
    {
        // An empty world folds to inverted infinities; unguarded, they would poison the matrix to NaN and every
        // shadowed pixel with it.
        var lightViewProj = Fit(
            View(Double3.Zero), Double3Bounds.Empty, Sun, 0f, Resolution);

        Assert.True(float.IsFinite(lightViewProj.M11));
        Assert.True(float.IsFinite(lightViewProj.M44));
    }

    [Fact]
    public void ZeroLightDirection_FallsBackToStraightDown()
    {
        var lightViewProj = Fit(
            View(Double3.Zero), Box(Double3.Zero, 1.2), Vector3.Zero, 0f, Resolution);

        Assert.True(float.IsFinite(lightViewProj.M11));
    }

    // --- P3-M5 CSM: ComputeCascades ------------------------------------------------------------------------------

    [Fact]
    public void Cascades_SplitDepthsAreMonotoneAndSpanTheRange()
    {
        var settings = CascadeSettings.Default; // 4 cascades, λ0.5, 200 m
        var view = View(Double3.Zero, far: 10_000f);
        Span<Matrix4x4> mats = stackalloc Matrix4x4[settings.Count];
        Span<float> splits = stackalloc float[settings.Count];

        ShadowFit.ComputeCascades(in view, Sun, in settings, Resolution, mats, splits);

        // Strictly increasing near→far, the last cascade reaching MaxDistance.
        for (var i = 1; i < settings.Count; i++)
        {
            Assert.True(splits[i] > splits[i - 1], $"split {i} ({splits[i]}) must exceed split {i - 1} ({splits[i - 1]})");
        }

        Assert.Equal(settings.MaxDistance, splits[^1], 2);
        // The practical split packs the near cascades tighter than a uniform split would: cascade 0 is well under
        // a quarter of the range (uniform would put it at exactly 50 m for 4 cascades over 200 m).
        Assert.True(splits[0] < 50f, $"the near cascade ({splits[0]} m) should be tighter than the uniform 50 m");
    }

    [Fact]
    public void Cascades_NearCascadeHasFinerTexelsThanTheFar()
    {
        // The whole point of CSM: the near slice is tiny, so its 2048² tile gives a far denser texel grid than the
        // far slice's — sharp shadows at the feet, coarser (but still bounded) far away.
        var settings = CascadeSettings.Default;
        var view = View(Double3.Zero, far: 10_000f);
        Span<Matrix4x4> mats = stackalloc Matrix4x4[settings.Count];
        Span<float> splits = stackalloc float[settings.Count];

        ShadowFit.ComputeCascades(in view, Sun, in settings, Resolution, mats, splits);

        Assert.True(
            FittedWidth(mats[0]) < FittedWidth(mats[^1]),
            $"near cascade width {FittedWidth(mats[0])} must be smaller than far {FittedWidth(mats[^1])}");
    }

    [Fact]
    public void Cascades_EachSliceCornersLandInsideItsOwnCascade()
    {
        // Every cascade must cover its slice: the eight frustum-slice corners project inside that cascade's clip
        // box (xy in [-1,1], z in [0,1]). If a corner fell outside, that part of the view would sample no shadow.
        var settings = CascadeSettings.Default;
        var view = View(Double3.Zero, far: 10_000f);
        Span<Matrix4x4> mats = stackalloc Matrix4x4[settings.Count];
        Span<float> splits = stackalloc float[settings.Count];
        ShadowFit.ComputeCascades(in view, Sun, in settings, Resolution, mats, splits);

        var sliceNear = view.Near;
        for (var i = 0; i < settings.Count; i++)
        {
            var (center, radius) = ShadowFit.FitSliceSphere(in view, sliceNear, splits[i]);
            // Sample the sphere's extremes along each axis — a superset of the slice corners.
            foreach (var axis in stackalloc[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
            {
                var p = center + (axis * radius);
                var clip = Vector4.Transform(new Vector4(p, 1f), mats[i]);
                var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
                Assert.InRange(ndc.X, -1.0001f, 1.0001f);
                Assert.InRange(ndc.Y, -1.0001f, 1.0001f);
                Assert.InRange(ndc.Z, -0.0001f, 1.0001f);
            }

            sliceNear = splits[i];
        }
    }

    [Fact]
    public void Cascades_UpstreamCasterStaysInsideTheDepthRange()
    {
        // A caster well above the near cascade (straight-down light) still throws its shadow into it: the fixed
        // κ·r setback must clear it, or it would be clipped and stop casting.
        var sun = Vector3.Normalize(new Vector3(0f, -1f, 0f));
        var settings = CascadeSettings.Default;
        var view = View(Double3.Zero, far: 10_000f);
        Span<Matrix4x4> mats = stackalloc Matrix4x4[settings.Count];
        Span<float> splits = stackalloc float[settings.Count];
        ShadowFit.ComputeCascades(in view, sun, in settings, Resolution, mats, splits);

        var (center, radius) = ShadowFit.FitSliceSphere(in view, view.Near, splits[0]);
        // A caster two radii above the near cascade's centre — upstream of the slice along the downward light.
        var caster = center + new Vector3(0f, 2f * radius, 0f);
        var clip = Vector4.Transform(new Vector4(caster, 1f), mats[0]);
        Assert.InRange(clip.Z / clip.W, 0f, 1f);
    }

    [Fact]
    public void Cascades_SnapIsStable_WhenTheCameraCreepsSubTexel()
    {
        // The per-cascade snap must kill shimmer exactly as the single-cascade one does: a static point creeps by
        // sub-texel camera motion and must not drift within its cascade.
        var settings = CascadeSettings.Default;
        var worldPoint = new Double3(2, 0, -5); // inside the near cascade
        var projections = new List<Vector2>();
        Span<Matrix4x4> mats = stackalloc Matrix4x4[settings.Count];
        Span<float> splits = stackalloc float[settings.Count];

        for (var frame = 0; frame < 8; frame++)
        {
            var eye = new Double3(0, 0, -0.0005 * frame);
            var view = View(eye, far: 10_000f);
            ShadowFit.ComputeCascades(in view, Sun, in settings, Resolution, mats, splits);
            var relative = worldPoint.ToVector3(eye);
            var clip = Vector4.Transform(new Vector4(relative, 1f), mats[0]);
            projections.Add(new Vector2(clip.X / clip.W, clip.Y / clip.W));
        }

        // Whole-texel snapping (the anti-crawl) lets a point APPEAR to jump up to ~1 texel when the grid re-snaps,
        // so the bound is a small constant (2 texels), NOT zero. The distinction from a drifting (unsnapped) grid is
        // that the movement stays bounded instead of growing with the camera; a continuous drift over more frames
        // would blow far past this. The near cascade's texel is fine (~1 cm), so this is a strict test.
        var texelNdc = 2f / Resolution;
        foreach (var p in projections)
        {
            Assert.True(
                Vector2.Distance(p, projections[0]) < 2f * texelNdc,
                $"the static point drifted {Vector2.Distance(p, projections[0]) / texelNdc:F2} texels in cascade 0");
        }
    }
}
