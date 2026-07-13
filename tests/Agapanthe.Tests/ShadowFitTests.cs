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
    public void FrustumFit_IsCameraRelative_SoFarOutIsIdenticalToTheOrigin()
    {
        // Same invariant as the render lists (spec §3.3): 10 000 km out, with the world moved with it, the light
        // matrix must be the same one — it is expressed in the frame's camera-relative space.
        var far = new Double3(1e7, 1e7, 1e7);

        var atOrigin = ShadowFit.ComputeLightViewProj(
            View(Double3.Zero, far: 10_000f), Box(Double3.Zero, 50_000), Sun, 100f, Resolution);
        var atFar = ShadowFit.ComputeLightViewProj(
            View(far, far: 10_000f), Box(far, 50_000), Sun, 100f, Resolution);

        Assert.Equal(atOrigin, atFar);
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
