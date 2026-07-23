using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Rendering;

/// <summary>
/// Fits the directional light's CSM cascades for the shadow pass (spec §3.5, P3-M5). GPU-free and static, so the
/// fit — the part that is easy to get subtly wrong and impossible to see in a capture — is unit-testable.
/// <para>
/// <b>Per-cascade, camera-only.</b> Each cascade is fitted to its own depth slice of the camera frustum, so the
/// shadow map's resolution stays roughly constant from the near ground to the horizon (a single map spread over a
/// large world gives texels the size of a car). The fit depends only on the camera — no scene or caster bounds —
/// which is what keeps it free of the P3-M2 circularity (the two-pass wedge is retired).
/// </para>
/// <para>
/// <b>Stability.</b> Each slice is bounded by a SPHERE, not by its eight corners: a sphere's radius does not
/// change when the camera merely rotates, so a cascade's extent stays constant and its texels do not crawl across
/// surfaces as you look around. The radius is quantized and the centre snapped to the tile's texel grid, which
/// kills the same shimmering under translation. Both are the standard cure for "swimming" shadows, and both are cheap.
/// </para>
/// <para>
/// Everything here is in the frame's CAMERA-RELATIVE space (spec §3.3): the frusta are built from the rotation-only
/// view matrix, whose eye is the origin by construction, so the result maps the same camera-relative positions the
/// vertex and fragment stages carry.
/// </para>
/// </summary>
internal static class ShadowFit
{
    /// <summary>
    /// Fits the N cascades of a CSM (P3-M5): splits the camera frustum into depth slices, fits one texel-snapped
    /// light-space matrix per slice, and reports the far view-space depth of each slice (for cascade selection in
    /// the fragment shader). Fills <paramref name="lightViewProj"/> and <paramref name="splitViewDepths"/>, both of
    /// length <see cref="CascadeSettings.Count"/>.
    /// <para>
    /// Each cascade's FOOTPRINT is the bounding sphere of its frustum slice — camera-only, so no caster bounds and
    /// no circularity (the P3-M2 two-pass wedge is retired). The DEPTH range uses a fixed generous upstream setback
    /// (<c>eye = centre − dir·κr</c>, far <c>= (κ+1)r</c>) so a caster upstream of the slice is not clipped; the
    /// per-cascade <c>UpstreamExtent</c> sophistication is deferred (backlog). The snap/quantize (anti-shimmer) are
    /// reused verbatim, each cascade snapped to its own <paramref name="tileResolution"/> (the atlas tile size).
    /// </para>
    /// </summary>
    public static void ComputeCascades(
        in RenderView view, Vector3 lightDirection, in CascadeSettings settings, uint tileResolution,
        Span<Matrix4x4> lightViewProj, Span<float> splitViewDepths, Span<Vector4> nearCutPlanes)
    {
        var n = settings.Count;
        if (lightViewProj.Length < n || splitViewDepths.Length < n || nearCutPlanes.Length < n)
        {
            throw new ArgumentException($"cascade spans must hold at least {n} entries.");
        }

        var dir = lightDirection.LengthSquared() > 1e-12f
            ? Vector3.Normalize(lightDirection)
            : new Vector3(0f, -1f, 0f);

        var near = MathF.Max(view.Near, 1e-4f);
        var far = MathF.Max(MathF.Min(view.Far, settings.MaxDistance), near * 1.001f);

        const float kappa = 4f; // upstream setback in radii — leaves (κ−1)r of room for casters behind the slice

        // Camera forward + eye in camera-relative space, for the per-cascade near-side depth cut (P3-M7 W3). The
        // view matrix is rotation+eye-translation; its inverse maps view axes to camera-relative world.
        if (!Matrix4x4.Invert(view.View, out var inverseView))
        {
            inverseView = Matrix4x4.Identity;
        }

        var forward = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, inverseView));
        var eye = view.EyeRelative;
        var eyeDepth = Vector3.Dot(forward, eye);

        var sliceNear = near;
        for (var i = 0; i < n; i++)
        {
            // Practical split scheme (Zhang et al.): blend the uniform and logarithmic partitions by Lambda. The
            // log term packs resolution into the near cascades (where texels matter most); the uniform term keeps
            // the far cascades from collapsing. Lambda ∈ [0,1]; 0 = uniform, 1 = logarithmic.
            var t = (i + 1) / (float)n;
            var uniform = near + ((far - near) * t);
            var logarithmic = near * MathF.Pow(far / near, t);
            var sliceFar = (settings.Lambda * logarithmic) + ((1f - settings.Lambda) * uniform);

            var (center, rawRadius) = FitSliceSphere(in view, sliceNear, sliceFar);
            var radius = QuantizeRadius(rawRadius);
            center = SnapToTexelGrid(center, view.Origin, dir, radius, tileResolution);

            var up = MathF.Abs(dir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
            var eyeDistance = kappa * radius;
            var lightEye = center - (dir * eyeDistance);
            var lightView = MathHelpers.LookAt(lightEye, center, up);
            var lightProj = MathHelpers.OrthographicVulkan(
                2f * radius, 2f * radius, MathF.Max(radius * 0.01f, 1e-4f), eyeDistance + radius);
            lightViewProj[i] = lightView * lightProj;
            splitViewDepths[i] = sliceFar; // the far view-space depth this cascade covers (positive, along -Z)

            // NEAR-side view-depth cut plane (P3-M7 W3): the far cascades' ortho boxes swallow the near field, so a
            // caster enters ~4 cascades (backlog §2.0bis, 4× raster). This plane rejects, from cascade i, casters
            // whose view depth is well BEFORE the slice starts — they belong to a nearer cascade. Cascade 0 is
            // EXEMPT (an all-keeping plane): cutting its near side would drop behind-camera casters whose shadow
            // still reaches the view (the P3-M6 anti-popping guarantee). The upstream (light-direction) margin κ is
            // untouched — this cuts only the view-depth overlap, not a caster's height above its slice.
            // Kept iff viewDepth(centre) >= sliceNear - margin - r, with viewDepth(p) = dot(forward, p) - eyeDepth.
            if (i == 0)
            {
                nearCutPlanes[i] = new Vector4(0f, 0f, 0f, 1f); // dot(0,c)+1 = 1 ≥ -r always → never cuts
            }
            else
            {
                var margin = (sliceFar - sliceNear) * 0.25f; // a quarter-slice cushion for straddlers + the fade band
                var nearLimit = sliceNear - margin;
                nearCutPlanes[i] = new Vector4(forward, -(eyeDepth + nearLimit));
            }

            sliceNear = sliceFar;
        }
    }

    /// <summary>
    /// The bounding sphere of the camera frustum SLICE between <paramref name="sliceNear"/> and
    /// <paramref name="sliceFar"/>, in camera-relative space (P3-M5 CSM). Built from the eight corners, so it is
    /// exact for any FOV/aspect — one call per cascade. The sphere depends only on the camera (not on any caster),
    /// which is what keeps the cascade fit free of the P3-M2 circularity.
    /// </summary>
    internal static (Vector3 Center, float Radius) FitSliceSphere(in RenderView view, float sliceNear, float sliceFar)
    {
        var near = MathF.Max(sliceNear, 1e-4f);
        var far = MathF.Max(sliceFar, near * 1.001f); // a degenerate slice would give a zero-radius sphere

        var tanHalfV = MathF.Tan(view.FovY * 0.5f);
        var tanHalfH = tanHalfV * view.AspectRatio;

        // The view matrix is rotation-only (the eye IS the origin), so its inverse is its transpose — but Invert
        // is clearer here and this runs once per frame, not per object.
        if (!Matrix4x4.Invert(view.View, out var inverseView))
        {
            inverseView = Matrix4x4.Identity;
        }

        Span<Vector3> corners = stackalloc Vector3[8];
        var i = 0;
        foreach (var d in stackalloc[] { near, far })
        {
            var x = d * tanHalfH;
            var y = d * tanHalfV;
            // Right-handed view space looks down its own -Z.
            corners[i++] = Vector3.Transform(new Vector3(-x, -y, -d), inverseView);
            corners[i++] = Vector3.Transform(new Vector3(x, -y, -d), inverseView);
            corners[i++] = Vector3.Transform(new Vector3(-x, y, -d), inverseView);
            corners[i++] = Vector3.Transform(new Vector3(x, y, -d), inverseView);
        }

        var center = Vector3.Zero;
        for (var c = 0; c < corners.Length; c++)
        {
            center += corners[c];
        }

        center /= corners.Length;

        var radius = 0f;
        for (var c = 0; c < corners.Length; c++)
        {
            radius = MathF.Max(radius, Vector3.Distance(center, corners[c]));
        }

        return (center, radius);
    }

    /// <summary>
    /// Rounds the fitted radius UP onto a coarse ladder (16 steps per octave), so a sphere that breathes with the
    /// scene — an entity spinning, a caster translating — holds a constant radius instead of varying continuously.
    /// The texel grid the snap quantizes onto is derived from this radius; a radius that never settles is a grid
    /// that never settles, and the snap would buy nothing. Rounding UP only ever grows the fit, so nothing that was
    /// shadowed falls outside it, and the ladder costs at most ~6% of the shadow map's resolution.
    /// </summary>
    private static float QuantizeRadius(float radius)
    {
        if (!float.IsFinite(radius) || radius <= 1e-4f)
        {
            return radius;
        }

        var step = MathF.Pow(2f, MathF.Ceiling(MathF.Log2(radius))) / 16f;
        return MathF.Ceiling(radius / step) * step;
    }

    /// <summary>
    /// Quantizes the fit's centre to the shadow map's texel grid, in LIGHT space. Without this, moving the camera
    /// by a fraction of a texel shifts the whole map by that fraction, and every shadow edge crawls along the
    /// surfaces — the classic shimmer. Snapped, the map moves in whole texels: the edges stay put.
    /// </summary>
    private static Vector3 SnapToTexelGrid(
        Vector3 center, Double3 origin, Vector3 dir, float radius, uint resolution)
    {
        var up = MathF.Abs(dir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var lightRotation = MathHelpers.LookAt(Vector3.Zero, dir, up); // rotation only: the eye is irrelevant here

        var texelSize = 2f * radius / resolution;
        if (texelSize <= 0f)
        {
            return center;
        }

        // The grid must be anchored to the WORLD, not to the eye. Quantizing the camera-relative centre would be a
        // no-op: that centre does not depend on where the eye IS (the view is rotation-only), so it never moves
        // under a pure translation and there is nothing for the floor() to bite on — while the geometry handed to
        // the shadow pass shifts by −Δcamera every frame. The map would slide continuously and every edge would
        // crawl: exactly the shimmer this is meant to kill.
        //
        // But only the PHASE may travel through absolute coordinates. Round-tripping the absolute centre itself
        // through the (float) light rotation would amplify that matrix's ~1e-7 relative error into METRES at
        // 1e7 m — the grid phase would then jitter with the camera and the shimmer would come straight back.
        // So: take the light-space coordinate of the origin (double, accurate), use it only to work out how far
        // the grid is offset, and apply that small delta to the camera-relative centre.
        var centerInLight = new Double3(center).TransformBy(lightRotation);
        var originInLight = origin.TransformBy(lightRotation);
        var absoluteX = centerInLight.X + originInLight.X;
        var absoluteY = centerInLight.Y + originInLight.Y;

        // A displacement of at most one texel — small, so it survives the trip back through the float rotation.
        var deltaX = (Math.Floor(absoluteX / texelSize) * texelSize) - absoluteX;
        var deltaY = (Math.Floor(absoluteY / texelSize) * texelSize) - absoluteY;

        // Z is NOT snapped: only the map's x/y are quantized by its texel grid.
        var snappedInLight = new Double3(centerInLight.X + deltaX, centerInLight.Y + deltaY, centerInLight.Z);

        // A rotation's inverse is its transpose — exactly, with the same entries, so no Invert() error.
        return snappedInLight.TransformBy(Matrix4x4.Transpose(lightRotation)).ToVector3(Double3.Zero);
    }
}
