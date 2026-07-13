namespace Agapanthe.Core;

/// <summary>
/// A double-precision, axis-aligned world-space bounding box (spec §3.5, system 2). Replaces the float
/// <c>Scene.BoundsMin/Max/Center/Diagonal</c> so the scene extent — consumed by the camera framing and the
/// light setup — stays exact far from the origin. System 2 folds the per-entity world bounds into one of these.
/// </summary>
public readonly struct Double3Bounds
{
    public readonly Double3 Min;
    public readonly Double3 Max;

    public Double3Bounds(Double3 min, Double3 max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>
    /// The seed for a union fold: inverted infinities so the first <see cref="Union"/> yields the real box.
    /// </summary>
    public static Double3Bounds Empty => new(
        new Double3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
        new Double3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity));

    public Double3 Center => (Min + Max) * 0.5;

    public Double3 Size => Max - Min;

    /// <summary>Length of the full diagonal (Max − Min), matching the legacy <c>Scene.BoundsDiagonal</c>.</summary>
    public double Diagonal => Size.Length;

    /// <summary>The smallest box containing both — system 2's fold of per-entity bounds into the scene extent.</summary>
    public static Double3Bounds Union(in Double3Bounds a, in Double3Bounds b) => new(
        new Double3(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y), Math.Min(a.Min.Z, b.Min.Z)),
        new Double3(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y), Math.Max(a.Max.Z, b.Max.Z)));
}
