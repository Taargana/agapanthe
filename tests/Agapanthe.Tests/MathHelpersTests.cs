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
