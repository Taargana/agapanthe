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
}
