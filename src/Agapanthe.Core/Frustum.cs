using System.Numerics;

namespace Agapanthe.Core;

/// <summary>
/// A view frustum as its six bounding planes, for sphere culling (spec §3.5). GPU-free and in Core on purpose:
/// the world culls against it without referencing the render layer, and a future server-side interest-management
/// pass (which has no GPU) can reuse the very same type.
/// <para>
/// <b>One type, two uses.</b> Built from any <c>view · projection</c> it bounds the CAMERA frustum (cull the
/// render list); built from the directional light's ortho <c>view · proj</c> it bounds the LIGHT volume (cull the
/// shadow-caster list — never against the camera frustum, or off-screen casters would pop their shadows in and
/// out, spec §3.5).
/// </para>
/// <para>
/// <b>Space.</b> The planes live in whatever space the matrix maps FROM. In this engine that is the frame's
/// camera-relative space (spec §3.3): the matrix is built from <see cref="RenderView.View"/> (origin at/near the
/// eye), so the sphere centres tested against it must be camera-relative too — which is exactly what the world
/// produces when it narrows a <see cref="Double3"/> position against the frame origin.
/// </para>
/// </summary>
public readonly struct Frustum
{
    // Six planes, each (Normal.xyz, D) with the normal pointing INTO the frustum and normalized, so the signed
    // distance of a point is dot(Normal, p) + D and a sphere of radius r is outside a plane iff that distance
    // is < -r. Order: left, right, bottom, top, near, far.
    private readonly Vector4 _left;
    private readonly Vector4 _right;
    private readonly Vector4 _bottom;
    private readonly Vector4 _top;
    private readonly Vector4 _near;
    private readonly Vector4 _far;

    private Frustum(Vector4 left, Vector4 right, Vector4 bottom, Vector4 top, Vector4 near, Vector4 far)
    {
        _left = left;
        _right = right;
        _bottom = bottom;
        _top = top;
        _near = near;
        _far = far;
    }

    /// <summary>
    /// Extracts the six planes from a row-vector <c>view · projection</c> (Gribb-Hartmann). A point maps to clip
    /// space as <c>clip = p · vp</c>, so <c>clip.x = dot(colX, (p,1))</c> where <c>colX</c> is a column of the
    /// matrix; each clip-space inequality bounding the Vulkan cube (<c>−w ≤ x,y ≤ w</c>, <c>0 ≤ z ≤ w</c>) becomes
    /// a plane by combining those columns. Planes are normalized so the distances compare against a metric radius.
    /// </summary>
    public static Frustum FromViewProjection(in Matrix4x4 vp)
    {
        // Columns of vp, as System.Numerics' Vector4.Transform reads them: clip.x = dot(colX, (p.xyz, 1)), etc.
        var colX = new Vector4(vp.M11, vp.M21, vp.M31, vp.M41);
        var colY = new Vector4(vp.M12, vp.M22, vp.M32, vp.M42);
        var colZ = new Vector4(vp.M13, vp.M23, vp.M33, vp.M43);
        var colW = new Vector4(vp.M14, vp.M24, vp.M34, vp.M44);

        return new Frustum(
            Normalize(colW + colX), // left:   x ≥ −w
            Normalize(colW - colX), // right:  x ≤  w
            Normalize(colW + colY), // bottom: y ≥ −w
            Normalize(colW - colY), // top:    y ≤  w
            Normalize(colZ),        // near:   z ≥ 0   (Vulkan depth [0, w])
            Normalize(colW - colZ)); // far:   z ≤  w
    }

    /// <summary>
    /// True if the sphere is inside or straddling the frustum. Tests the centre against all six planes: outside
    /// any one by more than the radius ⇒ culled. This is the standard plane-sphere test — it can keep a sphere
    /// that only clips a frustum corner (a cheap false positive that draws an extra object, never a false
    /// negative that drops a visible one).
    /// </summary>
    public bool Intersects(Vector3 center, float radius)
    {
        var negRadius = -radius;
        return Distance(_left, center) >= negRadius
            && Distance(_right, center) >= negRadius
            && Distance(_bottom, center) >= negRadius
            && Distance(_top, center) >= negRadius
            && Distance(_near, center) >= negRadius
            && Distance(_far, center) >= negRadius;
    }

    /// <summary>
    /// Copies the six planes (each <c>(Normal.xyz, D)</c>, inward normals, normalized) into
    /// <paramref name="dst"/> in order left, right, bottom, top, near, far. Lets
    /// <see cref="ExtrudedShadowFrustum"/> derive the shadow-caster wedge from the camera frustum without
    /// duplicating the Gribb-Hartmann extraction.
    /// </summary>
    public void CopyPlanes(Span<Vector4> dst)
    {
        dst[0] = _left;
        dst[1] = _right;
        dst[2] = _bottom;
        dst[3] = _top;
        dst[4] = _near;
        dst[5] = _far;
    }

    private static float Distance(Vector4 plane, Vector3 p)
        => (plane.X * p.X) + (plane.Y * p.Y) + (plane.Z * p.Z) + plane.W;

    private static Vector4 Normalize(Vector4 plane)
    {
        var length = new Vector3(plane.X, plane.Y, plane.Z).Length();
        return length > 1e-8f ? plane / length : plane;
    }
}
