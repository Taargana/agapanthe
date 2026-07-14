using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free tests for <see cref="ExtrudedShadowFrustum"/> (P3-M1, culling debt #2): the camera frustum swept toward
/// the light, used to drop off-screen shadow casters whose shadows never reach the view — without ever dropping one
/// whose shadow does (no false negatives → no shadow popping).
/// </summary>
public sealed class ExtrudedShadowFrustumTests
{
    // Camera at the origin, looking down -Z, 60° FOV, aspect 1, near 0.1 / far 100 (same as WorldSystemsTests).
    // Half-width at depth d is d·tan(30°) ≈ 0.577·d, so at z=-10 the frustum reaches x,y ≈ ±5.77.
    private static readonly Frustum Camera = Frustum.FromViewProjection(
        MathHelpers.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
        * MathHelpers.PerspectiveVulkan(MathF.PI / 3f, 1f, 0.1f, 100f));

    private const float R = 0.5f;

    [Fact]
    public void KeepsALateralOffScreenCaster_WhoseShadowProjectsIntoView()
    {
        // Light travels in -X (comes from the right). A caster far in +X at depth -10 is OUTSIDE the camera frustum
        // (x=20 ≫ 5.77), yet its shadow travels -X straight through the view → it MUST be kept (anti-popping).
        var extruded = ExtrudedShadowFrustum.FromCameraFrustum(in Camera, new Vector3(-1f, 0f, 0f));
        var caster = new Vector3(20f, 0f, -10f);

        Assert.False(Camera.Intersects(caster, R));   // off-screen for the camera
        Assert.True(extruded.Intersects(caster, R));   // but its shadow enters the view → kept
    }

    [Fact]
    public void DropsAnOffScreenCaster_WhoseShadowCannotEnter()
    {
        // Same light (-X). A caster far in -X is both off-screen AND downstream (its shadow travels further -X,
        // away from the view) → the kept "left" plane rejects it. This proves the wedge actually tightens.
        var extruded = ExtrudedShadowFrustum.FromCameraFrustum(in Camera, new Vector3(-1f, 0f, 0f));
        var caster = new Vector3(-20f, 0f, -10f);

        Assert.False(Camera.Intersects(caster, R));    // off-screen
        Assert.False(extruded.Intersects(caster, R));  // and its shadow never reaches the view → dropped
    }

    [Fact]
    public void KeepsEveryOnScreenCaster()
    {
        // The view frustum is a subset of the wedge (sweeping only adds volume), so anything the camera sees is
        // always kept, whatever the light direction.
        var dir = Vector3.Normalize(new Vector3(0.3f, -1f, -0.2f));
        var extruded = ExtrudedShadowFrustum.FromCameraFrustum(in Camera, dir);

        foreach (var p in new[]
        {
            new Vector3(0f, 0f, -1f),
            new Vector3(0f, 0f, -5f),
            new Vector3(2f, 1f, -20f),
            new Vector3(-3f, -2f, -50f),
        })
        {
            Assert.True(Camera.Intersects(p, R), $"sanity: {p} should be in the camera frustum");
            Assert.True(extruded.Intersects(p, R), $"{p} is on-screen and must be kept");
        }
    }

    [Fact]
    public void NearParallelLight_DoesNotProduceAFalseNegative()
    {
        // Light almost parallel to -X but tipped slightly into -Z, so the near/far planes are borderline. The ε
        // bias must DROP borderline planes (widen the wedge), never keep one and clip a caster whose shadow enters.
        // The same lateral off-screen caster as the headline case must still be kept.
        var dir = Vector3.Normalize(new Vector3(-1f, 0f, -1e-4f));
        var extruded = ExtrudedShadowFrustum.FromCameraFrustum(in Camera, dir);
        var caster = new Vector3(20f, 0f, -10f);

        Assert.False(Camera.Intersects(caster, R));
        Assert.True(extruded.Intersects(caster, R)); // no false negative from the borderline planes
    }
}
