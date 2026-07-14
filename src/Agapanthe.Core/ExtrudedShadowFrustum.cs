using System.Numerics;

namespace Agapanthe.Core;

/// <summary>
/// The region of space whose contents can cast a shadow into the camera frustum: the camera frustum swept toward
/// the light (P3-M1, culling debt #2). A directional shadow caster matters only if its shadow can land on a
/// visible surface, i.e. it lies "upstream" of the view frustum along the light direction. That region is the
/// Minkowski sum of the camera frustum with a ray pointing toward the light — and summing a convex polytope with a
/// ray adds no new faces: it only <b>drops</b> the frustum planes that face upstream and keeps the rest, opening
/// the volume toward the light.
/// <para>
/// Used to tighten the shadow-caster cull: a caster is kept iff it intersects BOTH the light volume (which bounds
/// what the shadow map covers) AND this wedge. On a flat scene the light volume keeps every caster; ANDing the
/// wedge drops the off-screen casters whose shadows never reach the view, without ever dropping one whose shadow
/// does (no false negatives → no shadow popping). The wedge only <b>restricts</b> the caster set; it never widens
/// it, so a caster kept by the AND is always inside the (unchanged) shadow-map volume.
/// </para>
/// <para>GPU-free and in Core like <see cref="Frustum"/>: the world culls against it without touching the render
/// layer, and the planes live in the same camera-relative space (spec §3.3).</para>
/// </summary>
public readonly struct ExtrudedShadowFrustum
{
    // We store the six plane slots as a fixed set so Intersects mirrors Frustum's six-dot test with no branching
    // or heap. A plane dropped by the extrusion is replaced by a sentinel that every point satisfies, so it never
    // rejects anything. Kept planes carry inward normals (dot(n,p)+D, outside iff < -radius), exactly like Frustum.
    private readonly Vector4 _p0;
    private readonly Vector4 _p1;
    private readonly Vector4 _p2;
    private readonly Vector4 _p3;
    private readonly Vector4 _p4;
    private readonly Vector4 _p5;

    // The 7th plane: the UPSTREAM CUT (P3-M2 D3.a). The wedge above is a Minkowski sum with a ray, so it is
    // UNBOUNDED toward the light — an entity 10 000 km upstream of the sun is still inside it. Left unbounded, that
    // entity would enter the caster list and drive the shadow map's depth range to 1e7 m, collapsing its precision.
    // This plane bounds the wedge a finite distance upstream (the shadow-caster distance): a caster further upstream
    // than that is rejected. There is NO free slot among the six — with a near-perpendicular light both near and far
    // are kept — so the cut needs its own field, and Intersects does seven dot products.
    private readonly Vector4 _cut;

    // A plane every point satisfies: zero normal, huge positive D → Distance == D ≥ -radius always. Finite (not
    // +inf) to keep the dot product NaN-free.
    private static readonly Vector4 AlwaysInside = new(0f, 0f, 0f, float.MaxValue);

    // Margin toward KEEPING a near-parallel plane. The exact rule keeps a plane iff dot(n, dir) ≤ 0, bound
    // INCLUDED: a plane exactly parallel to the light ray is not a borderline case, it is the plane that closes the
    // wedge sideways. With the sun overhead and a level camera the four lateral planes sit exactly at dot == 0, so
    // dropping them would degenerate the wedge into a half-space that culls nothing. We therefore keep the
    // borderline ones. That is safe: a plane whose true dot is +δ ≤ ε only mis-rejects casters more than d/δ (≥ 1000
    // km) upstream, and those are already dropped by the light-volume test we AND with.
    private const float ParallelEpsilon = 1e-4f;

    private ExtrudedShadowFrustum(
        Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3, Vector4 p4, Vector4 p5, Vector4 cut)
    {
        _p0 = p0;
        _p1 = p1;
        _p2 = p2;
        _p3 = p3;
        _p4 = p4;
        _p5 = p5;
        _cut = cut;
    }

    /// <summary>
    /// Builds the wedge bounded a finite distance upstream (P3-M2 D3.a). The six frustum planes are swept toward the
    /// light exactly as the unbounded overload does; the seventh plane cuts the wedge off
    /// <paramref name="maxUpstream"/> metres upstream (along <c>-direction</c>) of the anchor sphere
    /// (<paramref name="anchorCenter"/>, <paramref name="anchorRadius"/>) — the camera frustum's bounding sphere,
    /// which <c>ShadowFit</c> already computes.
    /// <para>
    /// <b>Why the anchor is a parameter, and why it is the frustum sphere.</b> <see cref="Frustum"/> exposes neither
    /// a corner nor a centre, so a "distance N from the frustum" plane cannot be built from it alone. Anchoring on
    /// the eye instead would clip legitimate casters the moment the shadow distance is large; anchoring on the
    /// frustum sphere keeps the bound relative to what the camera can actually see.
    /// </para>
    /// <para>A non-finite <paramref name="maxUpstream"/> means "no cut" (the plane is a sentinel every point
    /// satisfies) — the unbounded wedge, for the degenerate/whole-scene cases.</para>
    /// </summary>
    public static ExtrudedShadowFrustum FromCameraFrustum(
        in Frustum cameraFrustum, Vector3 lightDirection,
        Vector3 anchorCenter, float anchorRadius, float maxUpstream)
    {
        var length = lightDirection.Length();
        if (length <= 1e-8f)
        {
            return new ExtrudedShadowFrustum(
                AlwaysInside, AlwaysInside, AlwaysInside, AlwaysInside, AlwaysInside, AlwaysInside, AlwaysInside);
        }

        Span<Vector4> planes = stackalloc Vector4[6];
        cameraFrustum.CopyPlanes(planes);
        var dir = lightDirection / length;

        for (var i = 0; i < 6; i++)
        {
            var n = new Vector3(planes[i].X, planes[i].Y, planes[i].Z);
            // Drop the plane only if it clearly faces DOWNSTREAM (its inner half-space is not stable under the
            // light-ward sweep). Everything else — including the exactly-parallel planes, which are the ones that
            // close the wedge sideways — is kept.
            if (Vector3.Dot(n, dir) > ParallelEpsilon)
            {
                planes[i] = AlwaysInside;
            }
        }

        // The upstream cut: inward normal +dir (downstream is "inside"), positioned so a point is kept iff it lies
        // no more than (anchorRadius + maxUpstream) upstream of the anchor centre. Distance = dot(dir, p) + W with
        // W = anchorRadius + maxUpstream − dot(dir, anchorCenter); a caster's own radius then relaxes it by r, like
        // every other plane. A non-finite bound → the always-inside sentinel (no cut).
        var cut = float.IsFinite(maxUpstream)
            ? new Vector4(dir, anchorRadius + maxUpstream - Vector3.Dot(dir, anchorCenter))
            : AlwaysInside;

        return new ExtrudedShadowFrustum(planes[0], planes[1], planes[2], planes[3], planes[4], planes[5], cut);
    }

    /// <summary>
    /// The UNBOUNDED wedge (no upstream cut): the camera frustum swept toward the light, open to infinity on the
    /// light side. Use the bounded overload for culling real casters (an unbounded wedge lets a far-upstream caster
    /// wreck the depth range, P3-M2 D3.a); this one is for the degenerate cases (zero light direction) and tests.
    /// </summary>
    public static ExtrudedShadowFrustum FromCameraFrustum(in Frustum cameraFrustum, Vector3 lightDirection)
        => FromCameraFrustum(in cameraFrustum, lightDirection, Vector3.Zero, 0f, float.PositiveInfinity);

    /// <summary>
    /// True if the sphere is inside or straddling the wedge (kept planes only; dropped planes never reject). Same
    /// conservative plane-sphere test as <see cref="Frustum.Intersects"/> — a false positive draws an extra
    /// shadow caster, never a false negative that would drop a visible shadow.
    /// </summary>
    public bool Intersects(Vector3 center, float radius)
    {
        var negRadius = -radius;
        return Distance(_p0, center) >= negRadius
            && Distance(_p1, center) >= negRadius
            && Distance(_p2, center) >= negRadius
            && Distance(_p3, center) >= negRadius
            && Distance(_p4, center) >= negRadius
            && Distance(_p5, center) >= negRadius
            && Distance(_cut, center) >= negRadius;
    }

    private static float Distance(Vector4 plane, Vector3 p)
        => (plane.X * p.X) + (plane.Y * p.Y) + (plane.Z * p.Z) + plane.W;
}
