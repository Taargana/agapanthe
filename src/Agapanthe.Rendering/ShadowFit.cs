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

        Vector3 center;
        float radius;
        if (sceneRadius <= frustumRadius)
        {
            // The scene is smaller than what the camera can see: shadow the scene, tightly. No snapping — a
            // static scene fit does not move, so it cannot shimmer, and leaving it alone keeps this the exact
            // matrix the pre-M3 renderer produced.
            center = sceneCenter;
            radius = sceneRadius;
        }
        else
        {
            center = SnapToTexelGrid(frustumCenter, dir, frustumRadius, shadowResolution);
            radius = frustumRadius;
        }

        // The eye sits back along the light direction far enough that casters BEHIND the fitted sphere (which
        // are off-screen, but whose shadows fall into it) are still inside the depth range.
        var up = MathF.Abs(dir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var eye = center - (dir * (radius * 2f));
        var lightView = MathHelpers.LookAt(eye, center, up);
        var lightProj = MathHelpers.OrthographicVulkan(2f * radius, 2f * radius, radius * 0.5f, radius * 3.5f);
        return lightView * lightProj;
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
    /// Quantizes the fit's centre to the shadow map's texel grid, in LIGHT space. Without this, moving the camera
    /// by a fraction of a texel shifts the whole map by that fraction, and every shadow edge crawls along the
    /// surfaces — the classic shimmer. Snapped, the map moves in whole texels: the edges stay put.
    /// </summary>
    private static Vector3 SnapToTexelGrid(Vector3 center, Vector3 dir, float radius, uint resolution)
    {
        var up = MathF.Abs(dir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var lightRotation = MathHelpers.LookAt(Vector3.Zero, dir, up); // rotation only: the eye is irrelevant here
        if (!Matrix4x4.Invert(lightRotation, out var inverseRotation))
        {
            return center;
        }

        var texelSize = 2f * radius / resolution;
        if (texelSize <= 0f)
        {
            return center;
        }

        var inLight = Vector3.Transform(center, lightRotation);
        inLight.X = MathF.Floor(inLight.X / texelSize) * texelSize;
        inLight.Y = MathF.Floor(inLight.Y / texelSize) * texelSize;
        // Z is NOT snapped: depth is not quantized by the map's texel grid, only x/y are.
        return Vector3.Transform(inLight, inverseRotation);
    }
}
