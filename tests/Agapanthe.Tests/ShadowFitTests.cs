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

    // Builds a RenderView DIRECTLY with the given origin and the eye at that origin (eyeRelative = 0), bypassing
    // Camera.CreateView's cell snapping — these tests exercise ShadowFit, not the M4 origin-quantization policy,
    // so they want an origin they control exactly.
    private static RenderView View(Double3 origin, float yaw = 0f, float near = 0.1f, float far = 1000f)
    {
        var camera = new Camera { Position = origin, Yaw = yaw, Near = near, Far = far, AspectRatio = 16f / 9f };
        var view = MathHelpers.LookAt(Vector3.Zero, camera.Forward, Vector3.UnitY);
        return new RenderView(
            origin, Vector3.Zero, in view, camera.ProjectionMatrix, camera.FovY, camera.AspectRatio, near, far);
    }

    // The extent of the fitted ortho box along the light's right axis, recovered from the matrix: the ortho scales
    // the fitted width to NDC [-1,1], so 2/|column| is that width. It is the quantity that decides shadow texel
    // density, i.e. shadow quality.
    private static float FittedWidth(Matrix4x4 lightViewProj)
        => 2f / new Vector3(lightViewProj.M11, lightViewProj.M21, lightViewProj.M31).Length();


    // --- P3-M5 CSM: ComputeCascades ------------------------------------------------------------------------------

    [Fact]
    public void Cascades_SplitDepthsAreMonotoneAndSpanTheRange()
    {
        var settings = CascadeSettings.Default; // 4 cascades, λ0.5, 200 m
        var view = View(Double3.Zero, far: 10_000f);
        Span<Matrix4x4> mats = stackalloc Matrix4x4[settings.Count];
        Span<float> splits = stackalloc float[settings.Count];

        ShadowFit.ComputeCascades(in view, Sun, in settings, Resolution, mats, splits);

        // Strictly increasing near→far, the last cascade reaching MaxDistance.
        for (var i = 1; i < settings.Count; i++)
        {
            Assert.True(splits[i] > splits[i - 1], $"split {i} ({splits[i]}) must exceed split {i - 1} ({splits[i - 1]})");
        }

        Assert.Equal(settings.MaxDistance, splits[^1], 2);
        // The practical split must pack the near cascade TIGHTLY — that is the whole point of the CSM, and the
        // quantity that decides contact-shadow sharpness. The old bound (< 50 m, i.e. merely tighter than uniform)
        // was far too loose: it happily passed at 25 m, where cascade 0 samples at 3.2 cm/texel and the 5×5 PCF
        // smears the contact shadow over ~16 cm (audit MAJEUR-1). At the default λ=0.85 cascade 0 lands near 8 m.
        Assert.True(splits[0] < 12f, $"the near cascade ({splits[0]:F1} m) is too wide for a sharp contact shadow");
    }

    [Fact]
    public void Cascades_NearCascadeHasFinerTexelsThanTheFar()
    {
        // The whole point of CSM: the near slice is tiny, so its 2048² tile gives a far denser texel grid than the
        // far slice's — sharp shadows at the feet, coarser (but still bounded) far away.
        var settings = CascadeSettings.Default;
        var view = View(Double3.Zero, far: 10_000f);
        Span<Matrix4x4> mats = stackalloc Matrix4x4[settings.Count];
        Span<float> splits = stackalloc float[settings.Count];

        ShadowFit.ComputeCascades(in view, Sun, in settings, Resolution, mats, splits);

        Assert.True(
            FittedWidth(mats[0]) < FittedWidth(mats[^1]),
            $"near cascade width {FittedWidth(mats[0])} must be smaller than far {FittedWidth(mats[^1])}");
    }

    [Fact]
    public void Cascades_EachSliceCornersLandInsideItsOwnCascade()
    {
        // Every cascade must cover its slice: the eight frustum-slice corners project inside that cascade's clip
        // box (xy in [-1,1], z in [0,1]). If a corner fell outside, that part of the view would sample no shadow.
        var settings = CascadeSettings.Default;
        var view = View(Double3.Zero, far: 10_000f);
        Span<Matrix4x4> mats = stackalloc Matrix4x4[settings.Count];
        Span<float> splits = stackalloc float[settings.Count];
        ShadowFit.ComputeCascades(in view, Sun, in settings, Resolution, mats, splits);

        var sliceNear = view.Near;
        for (var i = 0; i < settings.Count; i++)
        {
            var (center, radius) = ShadowFit.FitSliceSphere(in view, sliceNear, splits[i]);
            // Sample the sphere's extremes along each axis — a superset of the slice corners.
            foreach (var axis in stackalloc[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
            {
                var p = center + (axis * radius);
                var clip = Vector4.Transform(new Vector4(p, 1f), mats[i]);
                var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
                Assert.InRange(ndc.X, -1.0001f, 1.0001f);
                Assert.InRange(ndc.Y, -1.0001f, 1.0001f);
                Assert.InRange(ndc.Z, -0.0001f, 1.0001f);
            }

            sliceNear = splits[i];
        }
    }

    [Fact]
    public void Cascades_UpstreamCasterStaysInsideTheDepthRange()
    {
        // A caster well above the near cascade (straight-down light) still throws its shadow into it: the fixed
        // κ·r setback must clear it, or it would be clipped and stop casting.
        var sun = Vector3.Normalize(new Vector3(0f, -1f, 0f));
        var settings = CascadeSettings.Default;
        var view = View(Double3.Zero, far: 10_000f);
        Span<Matrix4x4> mats = stackalloc Matrix4x4[settings.Count];
        Span<float> splits = stackalloc float[settings.Count];
        ShadowFit.ComputeCascades(in view, sun, in settings, Resolution, mats, splits);

        var (center, radius) = ShadowFit.FitSliceSphere(in view, view.Near, splits[0]);
        // A caster two radii above the near cascade's centre — upstream of the slice along the downward light.
        var caster = center + new Vector3(0f, 2f * radius, 0f);
        var clip = Vector4.Transform(new Vector4(caster, 1f), mats[0]);
        Assert.InRange(clip.Z / clip.W, 0f, 1f);
    }

    [Fact]
    public void Cascades_SnapIsStable_WhenTheCameraCreepsSubTexel()
    {
        // The per-cascade snap must kill shimmer exactly as the single-cascade one does: a static point creeps by
        // sub-texel camera motion and must not drift within its cascade.
        var settings = CascadeSettings.Default;
        var worldPoint = new Double3(2, 0, -5); // inside the near cascade
        var projections = new List<Vector2>();
        Span<Matrix4x4> mats = stackalloc Matrix4x4[settings.Count];
        Span<float> splits = stackalloc float[settings.Count];

        for (var frame = 0; frame < 8; frame++)
        {
            var eye = new Double3(0, 0, -0.0005 * frame);
            var view = View(eye, far: 10_000f);
            ShadowFit.ComputeCascades(in view, Sun, in settings, Resolution, mats, splits);
            var relative = worldPoint.ToVector3(eye);
            var clip = Vector4.Transform(new Vector4(relative, 1f), mats[0]);
            projections.Add(new Vector2(clip.X / clip.W, clip.Y / clip.W));
        }

        // Whole-texel snapping (the anti-crawl) lets a point APPEAR to jump up to ~1 texel when the grid re-snaps,
        // so the bound is a small constant (2 texels), NOT zero. The distinction from a drifting (unsnapped) grid is
        // that the movement stays bounded instead of growing with the camera; a continuous drift over more frames
        // would blow far past this. The near cascade's texel is fine (~1 cm), so this is a strict test.
        var texelNdc = 2f / Resolution;
        foreach (var p in projections)
        {
            Assert.True(
                Vector2.Distance(p, projections[0]) < 2f * texelNdc,
                $"the static point drifted {Vector2.Distance(p, projections[0]) / texelNdc:F2} texels in cascade 0");
        }
    }
}
