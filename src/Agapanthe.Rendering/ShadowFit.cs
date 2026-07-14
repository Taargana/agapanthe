using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Rendering;

/// <summary>
/// Fits the directional light's orthographic frustum for the shadow pass (spec §3.5). GPU-free and static, so the
/// fit — the part that is easy to get subtly wrong and impossible to see in a capture — is unit-testable.
/// <para>
/// <b>Why the camera frustum.</b> Fitting the light to the whole SCENE does not scale: in a large world the
/// shadow map would have to span kilometres, and a 2048² map spread over kilometres gives shadow texels the size
/// of a car. The fit must instead cover only what the camera can actually see — the frustum, truncated at
/// <c>shadowDistance</c> (there is no point shadowing what is a kilometre away and two pixels tall).
/// </para>
/// <para>
/// <b>…but never wider than the scene.</b> On a small scene the frustum is far LARGER than the geometry, and
/// fitting to it would throw away most of the shadow map's resolution for nothing. So the fit takes whichever of
/// the two spheres is smaller. Today (one helmet) that is the scene; in M4 (a large world) it will be the
/// frustum. One rule, correct at both ends.
/// </para>
/// <para>
/// <b>Stability.</b> The frustum is bounded by a SPHERE, not by its eight corners: a sphere's radius does not
/// change when the camera merely rotates, so the shadow map's extent stays constant and its texels do not crawl
/// across surfaces as you look around. The sphere's centre is then snapped to the shadow map's texel grid, which
/// kills the same shimmering under translation. Both are the standard cure for "swimming" shadows, and both are
/// cheap.
/// </para>
/// <para>
/// Everything here is in the frame's CAMERA-RELATIVE space (spec §3.3): the scene bounds are narrowed against
/// <see cref="RenderView.Origin"/>, and the frustum is built from the rotation-only view matrix, whose eye is the
/// origin by construction. The result therefore maps the same camera-relative positions the vertex and fragment
/// stages carry.
/// </para>
/// </summary>
internal static class ShadowFit
{
    /// <summary>
    /// The light-space <c>view · proj</c> (row-vector; a camera-relative point maps to light clip space as
    /// <c>p · result</c>). <paramref name="shadowDistance"/> truncates the shadowed part of the frustum; a
    /// non-positive value means "the whole frustum".
    /// </summary>
    public static Matrix4x4 ComputeLightViewProj(
        in RenderView view,
        in Double3Bounds sceneBounds,
        Vector3 lightDirection,
        float shadowDistance,
        uint shadowResolution)
    {
        var dir = lightDirection.LengthSquared() > 1e-12f
            ? Vector3.Normalize(lightDirection)
            : new Vector3(0f, -1f, 0f);

        var (frustumCenter, frustumRadius) = FitFrustumSphere(in view, shadowDistance);
        var (sceneCenter, sceneRadius) = FitSceneSphere(in sceneBounds, view.Origin);

        // The scene is smaller than what the camera can see → shadow the scene, tightly; otherwise fit the frustum.
        var (rawCenter, rawRadius) = sceneRadius <= frustumRadius
            ? (sceneCenter, sceneRadius)
            : (frustumCenter, frustumRadius);

        // Both fits are quantized, and both must be. The scene fit used to be left raw on the grounds that "a
        // static scene cannot shimmer" — true then, false since P3-M1 recomputes the world bounds every frame: the
        // moment an entity spins or translates, the scene sphere breathes and its centre drifts, and an unsnapped
        // map resamples the whole shadow every frame (crawling edges). Snapping the centre alone would not be
        // enough either: the texel grid is derived FROM the radius, so a radius that varies continuously moves the
        // grid it defines. Quantize the radius first, then snap the centre onto the grid that radius fixes.
        var radius = QuantizeRadius(rawRadius);
        var center = SnapToTexelGrid(rawCenter, view.Origin, dir, radius, shadowResolution);

        // Depth range. The eye must sit UPSTREAM of every caster: a caster outside the fitted sphere still throws
        // its shadow INTO it, and one that falls outside the light's depth range is clipped — it simply stops
        // casting. A fixed setback (the eye at 2r with a [0.5r, 3.5r] range) only leaves 0.5r of room upstream, so
        // a tall building behind the frustum would drop its shadow silently. Measure the real upstream extent of
        // the world along the light instead.
        var up = MathF.Abs(dir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var upstream = UpstreamExtent(in sceneBounds, view.Origin, center, dir);
        var eyeDistance = MathF.Max(radius * 2f, upstream + radius);
        var eye = center - (dir * eyeDistance);

        var lightView = MathHelpers.LookAt(eye, center, up);
        var lightProj = MathHelpers.OrthographicVulkan(
            2f * radius, 2f * radius, MathF.Max(radius * 0.01f, 1e-4f), eyeDistance + radius);
        return lightView * lightProj;
    }

    /// <summary>
    /// How far the scene extends UPSTREAM of the fitted centre along the light (i.e. on the side the light comes
    /// from), in the frame's camera-relative space. That is the distance the light's eye must clear for every
    /// caster to stay inside the depth range. Lateral casters are NOT covered — the ortho box is exactly the
    /// fitted sphere, and widening it would cost shadow resolution everywhere; the answer to those is to cull the
    /// caster list against the light volume (spec §3.5, M4).
    /// </summary>
    private static float UpstreamExtent(in Double3Bounds bounds, Double3 origin, Vector3 center, Vector3 dir)
    {
        if (bounds.IsEmpty)
        {
            return 0f;
        }

        var min = bounds.Min.ToVector3(origin);
        var max = bounds.Max.ToVector3(origin);

        // The most upstream corner of the AABB along the light: pick, per axis, whichever bound lies further
        // against the light direction. dot(center - corner, dir) is then the extent we must clear.
        var corner = new Vector3(
            dir.X > 0f ? min.X : max.X,
            dir.Y > 0f ? min.Y : max.Y,
            dir.Z > 0f ? min.Z : max.Z);

        return MathF.Max(Vector3.Dot(center - corner, dir), 0f);
    }

    /// <summary>
    /// The bounding sphere of the camera frustum truncated at <paramref name="shadowDistance"/>, in
    /// camera-relative space. Built from the eight corners, so it is exact for any FOV/aspect; the centre lies on
    /// the view axis and the radius is rotation-invariant (the corners rotate rigidly with the camera).
    /// </summary>
    private static (Vector3 Center, float Radius) FitFrustumSphere(in RenderView view, float shadowDistance)
    {
        var near = MathF.Max(view.Near, 1e-4f);
        var far = shadowDistance > 0f ? MathF.Min(view.Far, shadowDistance) : view.Far;
        far = MathF.Max(far, near * 1.001f); // a degenerate slice would give a zero-radius sphere

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
    /// The scene's bounding sphere, narrowed against the frame's camera origin (spec §3.3). Same 10% margin and
    /// unit fallback the pre-M3 fit used. An empty world folds to inverted infinities, which would poison the
    /// matrix to NaN — it collapses to the degenerate zero box instead.
    /// </summary>
    private static (Vector3 Center, float Radius) FitSceneSphere(in Double3Bounds bounds, Double3 origin)
    {
        var min = bounds.IsEmpty ? Vector3.Zero : bounds.Min.ToVector3(origin);
        var max = bounds.IsEmpty ? Vector3.Zero : bounds.Max.ToVector3(origin);
        var center = (min + max) * 0.5f;
        var radius = Vector3.Distance(min, max) * 0.5f;
        return (center, radius > 1e-4f ? radius * 1.1f : 1f);
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
