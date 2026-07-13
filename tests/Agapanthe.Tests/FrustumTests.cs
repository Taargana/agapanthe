using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Tests;

/// <summary>
/// The GPU-free frustum used for sphere culling (spec §3.5). The extraction is easy to get subtly wrong (column
/// vs row, the Vulkan z∈[0,w] near plane rather than OpenGL's [−w,w]), and a wrong plane silently culls visible
/// geometry or keeps everything — so it is pinned here, not eyeballed.
/// </summary>
public sealed class FrustumTests
{
    // A camera at the origin looking down -Z (rotation-only view, as the engine builds it), 60° vertical FOV,
    // 16:9, near 0.1, far 100 — the M3 RenderView shape.
    private static Frustum Camera(float near = 0.1f, float far = 100f)
    {
        var view = MathHelpers.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        var proj = MathHelpers.PerspectiveVulkan(MathF.PI / 3f, 16f / 9f, near, far);
        return Frustum.FromViewProjection(view * proj);
    }

    [Fact]
    public void PointBlankAheadIsInside()
    {
        // 10 m straight ahead, well within near/far and the FOV.
        Assert.True(Camera().Intersects(new Vector3(0f, 0f, -10f), 1f));
    }

    [Fact]
    public void BehindTheCameraIsCulled()
    {
        // +Z is behind a camera that looks down -Z.
        Assert.False(Camera().Intersects(new Vector3(0f, 0f, 10f), 1f));
    }

    [Fact]
    public void BeyondFarIsCulled()
    {
        Assert.False(Camera(far: 100f).Intersects(new Vector3(0f, 0f, -200f), 1f));
    }

    [Fact]
    public void CloserThanNearIsCulled()
    {
        Assert.False(Camera(near: 1f).Intersects(new Vector3(0f, 0f, -0.2f), 0.01f));
    }

    [Fact]
    public void FarOffToTheSideIsCulled()
    {
        // 10 m ahead but 1000 m to the right: far outside the horizontal FOV.
        Assert.False(Camera().Intersects(new Vector3(1000f, 0f, -10f), 1f));
    }

    [Fact]
    public void StraddlingSphereIsKept()
    {
        // A big sphere centred just behind the camera still overlaps the frustum: its radius reaches in.
        // The conservative test must keep it (a false negative would drop visible geometry).
        Assert.True(Camera().Intersects(new Vector3(0f, 0f, 2f), 5f));
    }

    [Fact]
    public void RadiusReachesAcrossThePlane()
    {
        // A point just outside the far plane, but a sphere large enough to cross it, is kept.
        Assert.False(Camera(far: 100f).Intersects(new Vector3(0f, 0f, -101f), 0.5f));
        Assert.True(Camera(far: 100f).Intersects(new Vector3(0f, 0f, -101f), 2f));
    }

    [Fact]
    public void OrthographicLightVolume_CullsAndKeeps()
    {
        // The SAME type from a light's ortho view·proj bounds the shadow volume. A 20 m box centred at the
        // origin: a point inside is kept, one well outside on X is culled.
        var view = MathHelpers.LookAt(new Vector3(0f, 10f, 0f), Vector3.Zero, Vector3.UnitZ); // looking down
        var proj = MathHelpers.OrthographicVulkan(20f, 20f, 0.1f, 50f);
        var light = Frustum.FromViewProjection(view * proj);

        Assert.True(light.Intersects(new Vector3(3f, 0f, 3f), 1f));
        Assert.False(light.Intersects(new Vector3(40f, 0f, 0f), 1f));
    }
}
