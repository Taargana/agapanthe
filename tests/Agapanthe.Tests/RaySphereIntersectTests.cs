using Agapanthe.Core;

namespace Agapanthe.Tests;

/// <summary>
/// Physics queries — pure host-side ray/sphere intersection math (mirrors the
/// <see cref="MathHelpers"/>/<c>ShadowFit</c>/<c>GpuTimestampMath</c> precedent of testing the
/// GPU-free math a feature depends on, independent of any World/ECS state). TDD-first: written
/// before <see cref="RaySphereIntersect"/> exists.
/// </summary>
public sealed class RaySphereIntersectTests
{
    [Fact]
    public void DirectHit_ReturnsNearestDistance()
    {
        var ray = new Ray(new Double3(0, 0, 0), new System.Numerics.Vector3(0, 0, -1));
        var ok = RaySphereIntersect.TryIntersectSphere(
            in ray, new Double3(0, 0, -10), sphereRadius: 2f, maxDistance: 100, out var distance);

        Assert.True(ok);
        Assert.Equal(8.0, distance, 6); // enters the sphere at z = -8 (10 - 2)
    }

    [Fact]
    public void Miss_RayPassesBesideSphere()
    {
        var ray = new Ray(new Double3(0, 0, 0), new System.Numerics.Vector3(0, 0, -1));
        var ok = RaySphereIntersect.TryIntersectSphere(
            in ray, new Double3(10, 0, -10), sphereRadius: 2f, maxDistance: 100, out var distance);

        Assert.False(ok);
        Assert.Equal(0.0, distance);
    }

    [Fact]
    public void SphereBehindOrigin_IsAMiss()
    {
        var ray = new Ray(new Double3(0, 0, 0), new System.Numerics.Vector3(0, 0, -1));
        var ok = RaySphereIntersect.TryIntersectSphere(
            in ray, new Double3(0, 0, 10), sphereRadius: 2f, maxDistance: 100, out var distance);

        Assert.False(ok);
        Assert.Equal(0.0, distance);
    }

    [Fact]
    public void OriginInsideSphere_IsAnImmediateHitAtZero()
    {
        var ray = new Ray(new Double3(0, 0, 0), new System.Numerics.Vector3(0, 0, -1));
        var ok = RaySphereIntersect.TryIntersectSphere(
            in ray, new Double3(0, 0, 0), sphereRadius: 5f, maxDistance: 100, out var distance);

        Assert.True(ok);
        Assert.Equal(0.0, distance);
    }

    [Fact]
    public void Tangent_ResolvesToTheSingleRoot()
    {
        // Ray along +X at y=2, sphere of radius 2 centred at the origin: grazes exactly at x=0.
        var ray = new Ray(new Double3(-10, 2, 0), new System.Numerics.Vector3(1, 0, 0));
        var ok = RaySphereIntersect.TryIntersectSphere(
            in ray, new Double3(0, 0, 0), sphereRadius: 2f, maxDistance: 100, out var distance);

        Assert.True(ok);
        Assert.Equal(10.0, distance, 3);
    }

    [Fact]
    public void MaxDistance_ClipsAFartherHit()
    {
        var ray = new Ray(new Double3(0, 0, 0), new System.Numerics.Vector3(0, 0, -1));
        var ok = RaySphereIntersect.TryIntersectSphere(
            in ray, new Double3(0, 0, -10), sphereRadius: 2f, maxDistance: 5, out var distance);

        Assert.False(ok);
        Assert.Equal(0.0, distance);
    }

    [Fact]
    public void PlanetaryMagnitude_DoesNotCatastrophicallyCancel()
    {
        // Audit fix (csharp-lowlevel F11): the classical b*b-4ac form loses the sphere's radius term to
        // rounding once |oc| gets this large (this engine's own AGAPANTHE_SUN distance constant). A camera at
        // the origin, a 2 m-radius sphere centred 7.48e10 m away along +X — the ray should hit the sphere's
        // near surface at distance (7.48e10 - 2), not miss due to the radius vanishing into rounding.
        const double sunDistance = 7.48e10;
        var ray = new Ray(new Double3(0, 0, 0), new System.Numerics.Vector3(1, 0, 0));
        var ok = RaySphereIntersect.TryIntersectSphere(
            in ray, new Double3(sunDistance, 0, 0), sphereRadius: 2f, maxDistance: sunDistance,
            out var distance);

        Assert.True(ok);
        Assert.Equal(sunDistance - 2.0, distance, 3);
    }
}
