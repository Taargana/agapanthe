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

    // A plane every point satisfies: zero normal, huge positive D → Distance == D ≥ -radius always. Finite (not
    // +inf) to keep the dot product NaN-free.
    private static readonly Vector4 AlwaysInside = new(0f, 0f, 0f, float.MaxValue);

    // Margin toward DROPPING a near-parallel plane. Keeping a borderline plane would tighten the wedge and could
    // drop a caster whose shadow reaches the view (false negative → popping); dropping it only widens the wedge
    // (a safe false positive). So we keep a plane only when it faces upstream by more than this margin.
    private const float ParallelEpsilon = 1e-4f;

    private ExtrudedShadowFrustum(
        Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3, Vector4 p4, Vector4 p5)
    {
        _p0 = p0;
        _p1 = p1;
        _p2 = p2;
        _p3 = p3;
        _p4 = p4;
        _p5 = p5;
    }

    /// <summary>
    /// Builds the wedge from the camera frustum and the light's propagation direction (source→surface), both in
    /// the same camera-relative space. A frustum plane with inward normal <c>n</c> survives iff sweeping toward the
    /// light (<c>-direction</c>) keeps points on its inner side, i.e. <c>dot(n, direction) &lt; -ε</c>; the rest are
    /// dropped (their side becomes open toward the light). A degenerate direction (near zero) drops every plane, so
    /// the wedge keeps everything — the conservative fallback.
    /// </summary>
    public static ExtrudedShadowFrustum FromCameraFrustum(in Frustum cameraFrustum, Vector3 lightDirection)
    {
        Span<Vector4> planes = stackalloc Vector4[6];
        cameraFrustum.CopyPlanes(planes);

        var length = lightDirection.Length();
        var dir = length > 1e-8f ? lightDirection / length : Vector3.Zero;

        for (var i = 0; i < 6; i++)
        {
            var n = new Vector3(planes[i].X, planes[i].Y, planes[i].Z);
            // Keep the plane only if it faces upstream (its inner half-space contains the light-ward ray). Drop the
            // rest — including near-parallel ones (|dot| ≤ ε) — so the wedge is never tighter than the true region.
            if (Vector3.Dot(n, dir) >= -ParallelEpsilon)
            {
                planes[i] = AlwaysInside;
            }
        }

        return new ExtrudedShadowFrustum(planes[0], planes[1], planes[2], planes[3], planes[4], planes[5]);
    }

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
            && Distance(_p5, center) >= negRadius;
    }

    private static float Distance(Vector4 plane, Vector3 p)
        => (plane.X * p.X) + (plane.Y * p.Y) + (plane.Z * p.Z) + plane.W;
}
