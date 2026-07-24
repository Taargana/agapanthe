using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Tests;

/// <summary>
/// Reversed-Z perspective (P3-M8): the depth-range fix for planetary scale. Maps the near plane to NDC z = 1 and
/// the far plane to z = 0 (the reverse of <see cref="MathHelpers.PerspectiveVulkan"/>), which — paired with the
/// engine's D32 float depth — spreads precision near-uniformly across a huge near/far ratio. The derivation is the
/// clip-space transform z → w − z applied to the standard matrix; these tests pin the resulting NDC depths.
/// </summary>
public sealed class ReversedZProjectionTests
{
    private const float Near = 0.1f;
    private const float Far = 2.0e10f; // planetary far plane (the Sun sphere sits near here)

    [Fact]
    public void NearPlane_MapsToOne_FarPlane_MapsToZero()
    {
        var proj = MathHelpers.PerspectiveVulkanReversed(MathF.PI / 3f, 16f / 9f, Near, Far);

        // View space looks down -Z: a point at -near is on the near plane, -far on the far plane.
        var atNear = MathHelpers.ProjectPoint(new Vector3(0, 0, -Near), proj);
        var atFar = MathHelpers.ProjectPoint(new Vector3(0, 0, -Far), proj);

        Assert.Equal(1f, atNear.Z, 3); // reversed-Z: near → 1
        Assert.Equal(0f, atFar.Z, 3);  // reversed-Z: far  → 0
    }

    [Fact]
    public void DepthIsMonotonic_NearerIsGreater()
    {
        // The whole point: with reversed-Z + GreaterOrEqual, a nearer fragment must have a STRICTLY GREATER NDC z
        // than a farther one, all the way across the range (so the depth test keeps the nearer surface).
        var proj = MathHelpers.PerspectiveVulkanReversed(MathF.PI / 3f, 16f / 9f, Near, Far);

        var depths = new float[6];
        var d = new[] { 0.1f, 1f, 100f, 1e4f, 1e7f, 1e10f };
        for (var i = 0; i < d.Length; i++)
        {
            depths[i] = MathHelpers.ProjectPoint(new Vector3(0, 0, -d[i]), proj).Z;
        }

        for (var i = 1; i < depths.Length; i++)
        {
            Assert.True(depths[i] < depths[i - 1], $"z at {d[i]} m ({depths[i]}) must be < z at {d[i - 1]} m ({depths[i - 1]})");
        }
    }

    [Fact]
    public void KeepsTheXyMappingOfTheStandardProjection()
    {
        // Reversing depth must not disturb x/y: the transform touches only the z output (column 3).
        var std = MathHelpers.PerspectiveVulkan(MathF.PI / 3f, 16f / 9f, Near, Far);
        var rev = MathHelpers.PerspectiveVulkanReversed(MathF.PI / 3f, 16f / 9f, Near, Far);
        var p = new Vector3(3f, -2f, -5f);

        var a = MathHelpers.ProjectPoint(p, std);
        var b = MathHelpers.ProjectPoint(p, rev);

        Assert.Equal(a.X, b.X, 4);
        Assert.Equal(a.Y, b.Y, 4);
    }
}
