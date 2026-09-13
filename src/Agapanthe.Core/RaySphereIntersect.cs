namespace Agapanthe.Core;

/// <summary>
/// Physics queries — pure ray/sphere intersection math, GPU-free and unit-tested on its own
/// (mirrors the <see cref="MathHelpers"/>/<c>ShadowFit</c>/<c>GpuTimestampMath</c> precedent of
/// extracting the pure math a feature depends on).
/// </summary>
public static class RaySphereIntersect
{
    /// <summary>
    /// Finds the nearest intersection of <paramref name="ray"/> with a sphere of
    /// <paramref name="sphereRadius"/> centred at <paramref name="sphereCenter"/>, within
    /// <c>[0, maxDistance]</c>. Solved in <c>double</c> throughout — the ray's origin is already
    /// double precision, and casting to <c>float</c> early would reintroduce exactly the
    /// precision problem <see cref="Double3"/>/camera-relative-origin exists to avoid.
    /// <para>
    /// When the ray's origin is already inside the sphere, this reports an immediate hit at
    /// <paramref name="distance"/> = 0 (the ray already touches the sphere at its start) rather
    /// than the far exit point — the simpler, more intuitive convention for "what does this ray
    /// touch": a query starting inside a volume is already touching it.
    /// </para>
    /// <para>
    /// A tangent/grazing ray (the quadratic's two roots coincide) resolves to that single root —
    /// no special-cased branch, the general formula already produces the right answer.
    /// </para>
    /// </summary>
    public static bool TryIntersectSphere(
        in Ray ray, Double3 sphereCenter, float sphereRadius, double maxDistance, out double distance)
    {
        distance = 0.0;

        var oc = ray.Origin - sphereCenter;
        var r2 = (double)sphereRadius * sphereRadius;

        if (oc.LengthSquared < r2)
        {
            // Origin already inside the sphere — an immediate hit, see remarks. This comparison needs no
            // subtraction, so it stays exact regardless of how far sphereCenter itself is from the origin.
            return maxDistance >= 0.0;
        }

        double dx = ray.Direction.X, dy = ray.Direction.Y, dz = ray.Direction.Z;
        var a = (dx * dx) + (dy * dy) + (dz * dz);
        var h = (oc.X * dx) + (oc.Y * dy) + (oc.Z * dz);

        // Audit fix (csharp-lowlevel F11): the classical b*b-4ac / oc.LengthSquared-r*r form catastrophically
        // cancels once sphereCenter is far from the origin (this engine is planetary/interstellar scale in
        // double, e.g. AGAPANTHE_SUN at ~7.48e10 m) — squaring oc's ~1e10+ magnitude before subtracting a
        // ~1-100 m radius term loses the radius entirely to rounding, since ulp(oc.LengthSquared) at that
        // scale vastly exceeds r*r. Reformulated geometrically instead: find the point on the ray nearest the
        // sphere's centre (a direct Double3 addition, no squaring), then subtract sphereCenter from THAT point
        // — a subtraction of two nearby large values, which is exact per Sterbenz's lemma when a hit is close,
        // rather than the previous "huge minus huge" subtraction whose result was itself huge. Only the small
        // residual perpendicular vector ever gets squared.
        var tClosest = -h / a;
        var closestPoint = ray.Origin + (new Double3(ray.Direction) * tClosest);
        var perpLengthSquared = (closestPoint - sphereCenter).LengthSquared;

        if (perpLengthSquared > r2)
        {
            return false;
        }

        var halfChord = Math.Sqrt((r2 - perpLengthSquared) / a);
        var t0 = tClosest - halfChord;
        var t1 = tClosest + halfChord;

        // t0 <= t1 always (halfChord >= 0); take the smallest non-negative root — the nearest point ahead of
        // the ray's origin. Both negative means the sphere is behind it.
        var nearest = t0 >= 0.0 ? t0 : t1;
        if (nearest < 0.0 || nearest > maxDistance)
        {
            return false;
        }

        distance = nearest;
        return true;
    }
}
