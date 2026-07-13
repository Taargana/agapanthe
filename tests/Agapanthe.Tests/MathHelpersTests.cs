using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Tests;

public class MathHelpersTests
{
    private const float FovY = MathF.PI / 2f; // 90°
    private const float Near = 0.1f;
    private const float Far = 100f;

    [Fact]
    public void PerspectiveVulkan_PointOnNearPlane_MapsToDepthZero()
    {
        var proj = MathHelpers.PerspectiveVulkan(FovY, 1f, Near, Far);
        var clip = MathHelpers.ProjectPoint(new Vector3(0f, 0f, -Near), proj);
        Assert.Equal(0f, clip.Z, 5);
    }

    [Fact]
    public void PerspectiveVulkan_PointOnFarPlane_MapsToDepthOne()
    {
        var proj = MathHelpers.PerspectiveVulkan(FovY, 1f, Near, Far);
        var clip = MathHelpers.ProjectPoint(new Vector3(0f, 0f, -Far), proj);
        Assert.Equal(1f, clip.Z, 4);
    }

    [Fact]
    public void PerspectiveVulkan_ViewSpaceUp_MapsToNegativeClipY()
    {
        // Vulkan clip space is Y-down: a point above center must project to negative Y.
        var proj = MathHelpers.PerspectiveVulkan(FovY, 1f, Near, Far);
        var clip = MathHelpers.ProjectPoint(new Vector3(0f, 1f, -10f), proj);
        Assert.True(clip.Y < 0f);
    }

    [Fact]
    public void PerspectiveVulkan_ViewSpaceRight_MapsToPositiveClipX()
    {
        var proj = MathHelpers.PerspectiveVulkan(FovY, 1f, Near, Far);
        var clip = MathHelpers.ProjectPoint(new Vector3(1f, 0f, -10f), proj);
        Assert.True(clip.X > 0f);
    }

    [Fact]
    public void PerspectiveVulkan_90DegFov_EdgeOfFrustum_MapsToClipOne()
    {
        // At 90° fovY and aspect 1, a point at |y| == |z| lies on the frustum edge.
        var proj = MathHelpers.PerspectiveVulkan(FovY, 1f, Near, Far);
        var clip = MathHelpers.ProjectPoint(new Vector3(0f, -10f, -10f), proj);
        Assert.Equal(1f, clip.Y, 4);
    }

    private const float OrthoWidth = 4f;
    private const float OrthoHeight = 2f;

    [Fact]
    public void OrthographicVulkan_PointOnNearPlane_MapsToDepthZero()
    {
        var proj = MathHelpers.OrthographicVulkan(OrthoWidth, OrthoHeight, Near, Far);
        var clip = MathHelpers.ProjectPoint(new Vector3(0f, 0f, -Near), proj);
        Assert.Equal(0f, clip.Z, 5);
    }

    [Fact]
    public void OrthographicVulkan_PointOnFarPlane_MapsToDepthOne()
    {
        var proj = MathHelpers.OrthographicVulkan(OrthoWidth, OrthoHeight, Near, Far);
        var clip = MathHelpers.ProjectPoint(new Vector3(0f, 0f, -Far), proj);
        Assert.Equal(1f, clip.Z, 5);
    }

    [Fact]
    public void OrthographicVulkan_ViewSpaceUp_MapsToNegativeClipY()
    {
        // Vulkan clip space is Y-down: a point above centre must project to negative Y (unlike the raw
        // System.Numerics ortho, which is Y-up). Depth is independent of X/Y under orthographic.
        var proj = MathHelpers.OrthographicVulkan(OrthoWidth, OrthoHeight, Near, Far);
        var clip = MathHelpers.ProjectPoint(new Vector3(0f, 0.5f, -10f), proj);
        Assert.True(clip.Y < 0f);
    }

    [Fact]
    public void OrthographicVulkan_ViewSpaceRight_MapsToPositiveClipX()
    {
        var proj = MathHelpers.OrthographicVulkan(OrthoWidth, OrthoHeight, Near, Far);
        var clip = MathHelpers.ProjectPoint(new Vector3(1f, 0f, -10f), proj);
        Assert.True(clip.X > 0f);
    }

    [Fact]
    public void OrthographicVulkan_Corners_MapToNdcExtents()
    {
        // Centred box: (±w/2, ±h/2) map to the NDC unit square, with Y flipped for Vulkan.
        // +Y (top of the box) → NDC y = -1, -Y (bottom) → NDC y = +1.
        var proj = MathHelpers.OrthographicVulkan(OrthoWidth, OrthoHeight, Near, Far);

        var topRight = MathHelpers.ProjectPoint(new Vector3(OrthoWidth / 2f, OrthoHeight / 2f, -Near), proj);
        Assert.Equal(1f, topRight.X, 5);
        Assert.Equal(-1f, topRight.Y, 5);
        Assert.Equal(0f, topRight.Z, 5);

        var bottomLeftFar = MathHelpers.ProjectPoint(new Vector3(-OrthoWidth / 2f, -OrthoHeight / 2f, -Far), proj);
        Assert.Equal(-1f, bottomLeftFar.X, 5);
        Assert.Equal(1f, bottomLeftFar.Y, 5);
        Assert.Equal(1f, bottomLeftFar.Z, 5);
    }

    [Fact]
    public void OrthographicVulkan_AndPerspective_AgreeOnYFlipSign()
    {
        // The two projections must share the same Y-down convention: the same view-space point projects
        // to a clip Y of the same sign under both, so geometry doesn't flip between passes.
        var ortho = MathHelpers.OrthographicVulkan(OrthoWidth, OrthoHeight, Near, Far);
        var persp = MathHelpers.PerspectiveVulkan(FovY, 1f, Near, Far);

        foreach (var y in new[] { 0.5f, -0.5f })
        {
            var p = new Vector3(0f, y, -10f);
            var orthoY = MathHelpers.ProjectPoint(p, ortho).Y;
            var perspY = MathHelpers.ProjectPoint(p, persp).Y;
            Assert.Equal(MathF.Sign(perspY), MathF.Sign(orthoY));
        }
    }

    [Fact]
    public void LookAt_EyeLookingDownNegativeZ_TargetIsInFront()
    {
        var view = MathHelpers.LookAt(new Vector3(0f, 0f, 5f), Vector3.Zero, Vector3.UnitY);
        var viewSpace = Vector4.Transform(new Vector4(0f, 0f, 0f, 1f), view);
        // Right-handed view space looks down -Z: the target sits at negative Z.
        Assert.Equal(-5f, viewSpace.Z, 5);
        Assert.Equal(0f, viewSpace.X, 5);
        Assert.Equal(0f, viewSpace.Y, 5);
    }

    [Fact]
    public void MaxStretch_IsExactForRotationTimesUniformScale()
    {
        // The common case must NOT be inflated (else every entity's cull radius grows and the captures shift):
        // a rotation composed with a uniform scale stretches every direction by exactly the scale.
        var m = Matrix4x4.CreateScale(2.5f) * Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateRotationX(0.3f);

        Assert.Equal(2.5f, MathHelpers.MaxStretch(m), 4);
    }

    [Fact]
    public void MaxStretch_UpperBoundsTheStretchOfEveryDirection()
    {
        // The load-bearing property (audit P2-M4 M1): σ_max must be >= the actual stretch of ANY unit vector,
        // or a bounding sphere grown by it would UNDER-cover and a visible object would be culled. A sheared
        // transform (rotation + non-uniform scale) is the case the old longest-row heuristic got wrong.
        var m = Matrix4x4.CreateScale(3f, 1f, 0.5f) * Matrix4x4.CreateRotationZ(0.6f) * Matrix4x4.CreateRotationY(1.1f);
        var sigmaMax = MathHelpers.MaxStretch(m);

        var rng = new Random(987);
        for (var i = 0; i < 2000; i++)
        {
            var v = Vector3.Normalize(new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1)));
            if (!float.IsFinite(v.X))
            {
                continue;
            }

            var stretched = Vector3.TransformNormal(v, m).Length();
            Assert.True(stretched <= sigmaMax + 1e-3f, $"direction {v} stretched to {stretched} > σ_max {sigmaMax}");
        }

        // And it is the TIGHT bound: the largest scale (3) is achievable, so σ_max must equal it, not exceed it.
        Assert.Equal(3f, sigmaMax, 3);
    }

    [Fact]
    public void MaxStretch_LongestRowWouldHaveUnderCovered_Shear()
    {
        // Pins the actual bug: for this sheared matrix the longest basis row is strictly shorter than σ_max, so
        // the old MaxAxisScale returned a radius that dropped visible geometry. σ_max does not.
        // Rotate THEN scale (R·S): this is the order whose basis rows are shorter than σ_max. (Scale-then-rotate
        // keeps the rows at exactly σ_max, which is why the old heuristic looked fine on simple cases.)
        var m = Matrix4x4.CreateRotationZ(MathF.PI / 4f) * Matrix4x4.CreateScale(2f, 1f, 1f);
        var longestRow = MathF.Sqrt(MathF.Max(
            new Vector3(m.M11, m.M12, m.M13).LengthSquared(),
            MathF.Max(
                new Vector3(m.M21, m.M22, m.M23).LengthSquared(),
                new Vector3(m.M31, m.M32, m.M33).LengthSquared())));

        Assert.True(longestRow < 2f - 0.05f, $"expected the longest row ({longestRow}) to under-cover σ_max = 2");
        Assert.Equal(2f, MathHelpers.MaxStretch(m), 3);
    }
}
