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

    private static RenderView View(Double3 origin, float yaw = 0f, float near = 0.1f, float far = 1000f)
    {
        var camera = new Camera
        {
            Position = origin,
            Yaw = yaw,
            Near = near,
            Far = far,
            AspectRatio = 16f / 9f,
        };
        return camera.CreateView();
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

    [Fact]
    public void SmallScene_FitsTheScene_NotTheMuchLargerFrustum()
    {
        // A helmet in a 1000 m frustum: fitting the frustum would spread the shadow map over ~1 km and leave the
        // helmet a handful of texels. The fit must stay on the scene.
        var view = View(Double3.Zero);
        var scene = Box(Double3.Zero, 1.2); // ~2.4 m across

        var lightViewProj = ShadowFit.ComputeLightViewProj(
            in view, in scene, Sun, shadowDistance: 0f, Resolution);

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

        var lightViewProj = ShadowFit.ComputeLightViewProj(
            in view, in world, Sun, shadowDistance: 100f, Resolution);

        var width = FittedWidth(lightViewProj);
        Assert.True(width < 1_000f, $"fitted width {width} should follow the 100 m shadow distance, not the 100 km world");
        Assert.True(width > 100f, $"fitted width {width} must still cover the frustum out to the shadow distance");
    }

    [Fact]
    public void ShadowDistance_CapsTheFit()
    {
        var world = Box(Double3.Zero, 50_000);

        var near = ShadowFit.ComputeLightViewProj(
            View(Double3.Zero, far: 10_000f), in world, Sun, shadowDistance: 50f, Resolution);
        var far = ShadowFit.ComputeLightViewProj(
            View(Double3.Zero, far: 10_000f), in world, Sun, shadowDistance: 200f, Resolution);

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

        var facingForward = ShadowFit.ComputeLightViewProj(
            View(Double3.Zero, yaw: 0f, far: 10_000f), in world, Sun, 100f, Resolution);
        var turned = ShadowFit.ComputeLightViewProj(
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
            var lightViewProj = ShadowFit.ComputeLightViewProj(view, in world, Sun, 100f, Resolution);

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

        var lightViewProj = ShadowFit.ComputeLightViewProj(view, in world, sun, 100f, Resolution);

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

        var atOrigin = ShadowFit.ComputeLightViewProj(
            View(Double3.Zero, far: 10_000f), Box(Double3.Zero, 50_000), Sun, 100f, Resolution);
        var atFar = ShadowFit.ComputeLightViewProj(
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
        var lightViewProj = ShadowFit.ComputeLightViewProj(
            View(Double3.Zero), Double3Bounds.Empty, Sun, 0f, Resolution);

        Assert.True(float.IsFinite(lightViewProj.M11));
        Assert.True(float.IsFinite(lightViewProj.M44));
    }

    [Fact]
    public void ZeroLightDirection_FallsBackToStraightDown()
    {
        var lightViewProj = ShadowFit.ComputeLightViewProj(
            View(Double3.Zero), Box(Double3.Zero, 1.2), Vector3.Zero, 0f, Resolution);

        Assert.True(float.IsFinite(lightViewProj.M11));
    }
}
