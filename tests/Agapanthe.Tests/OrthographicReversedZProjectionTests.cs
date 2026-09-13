using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Tests;

/// <summary>
/// Slice-2 — reversed-Z orthographic (mirrors <see cref="ReversedZProjectionTests"/> for
/// <see cref="MathHelpers.OrthographicVulkanReversed"/>): maps the near plane to NDC z = 1 and the far
/// plane to z = 0, matching the engine's pipeline-wide reversed-Z convention (<c>ClearDepth = 0f</c> +
/// <c>GreaterOrEqual</c>, hardcoded in <c>Renderer.cs</c>). A *standard*-depth orthographic matrix under
/// that fixed compare op would silently invert near/far ordering — a real depth-test correctness bug, not
/// one a validation layer would ever surface. TDD-first: this test is written before
/// <see cref="MathHelpers.OrthographicVulkanReversed"/> exists.
/// </summary>
public sealed class OrthographicReversedZProjectionTests
{
    private const float Near = 0.1f;
    private const float Far = 200f;
    private const float Width = 40f;
    private const float Height = 30f;

    [Fact]
    public void NearPlane_MapsToOne_FarPlane_MapsToZero()
    {
        var proj = MathHelpers.OrthographicVulkanReversed(Width, Height, Near, Far);

        // View space looks down -Z: a point at -near is on the near plane, -far on the far plane.
        var atNear = MathHelpers.ProjectPoint(new Vector3(0, 0, -Near), proj);
        var atFar = MathHelpers.ProjectPoint(new Vector3(0, 0, -Far), proj);

        Assert.Equal(1f, atNear.Z, 3); // reversed-Z: near → 1
        Assert.Equal(0f, atFar.Z, 3);  // reversed-Z: far  → 0
    }

    [Fact]
    public void DepthIsMonotonic_NearerIsGreater()
    {
        var proj = MathHelpers.OrthographicVulkanReversed(Width, Height, Near, Far);

        var d = new[] { 0.1f, 1f, 10f, 50f, 100f, 199f };
        var depths = new float[d.Length];
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
        var std = MathHelpers.OrthographicVulkan(Width, Height, Near, Far);
        var rev = MathHelpers.OrthographicVulkanReversed(Width, Height, Near, Far);
        var p = new Vector3(3f, -2f, -5f);

        var a = MathHelpers.ProjectPoint(p, std);
        var b = MathHelpers.ProjectPoint(p, rev);

        Assert.Equal(a.X, b.X, 4);
        Assert.Equal(a.Y, b.Y, 4);
    }
}
