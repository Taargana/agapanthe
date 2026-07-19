using System.Numerics;
using System.Runtime.InteropServices;

namespace Agapanthe.Core;

/// <summary>
/// A three-component, double-precision vector for world-space positions (spec §3.6). <see cref="Vector3"/> is
/// float32, which loses sub-metre precision around 10 000 km from the origin — so a moving scene rendered far
/// out jitters. This type is the storage that keeps positions exact; the GPU still only sees float, via
/// <see cref="ToVector3"/> expressed relative to the camera. That is the whole reason the type exists
/// (System.Numerics has no double Vector3).
/// <para><see cref="LayoutKind.Sequential"/>, blittable: prerequisite of the future source-generated
/// serialization (same generator as the AOT component rooting).</para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Double3 : IEquatable<Double3>
{
    public readonly double X;
    public readonly double Y;
    public readonly double Z;

    public Double3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>Widens a float <see cref="Vector3"/> exactly (float → double is lossless).</summary>
    public Double3(Vector3 v)
    {
        X = v.X;
        Y = v.Y;
        Z = v.Z;
    }

    public static Double3 Zero => default;

    public static Double3 operator +(Double3 a, Double3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Double3 operator -(Double3 a, Double3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Double3 operator *(Double3 v, double scalar) => new(v.X * scalar, v.Y * scalar, v.Z * scalar);

    /// <summary>
    /// Applies the rotation/scale (upper 3×3) of <paramref name="m"/> to this vector, in double, row-vector
    /// convention (<c>v · M</c>). The matrix stays float — only the accumulation is double, which is what keeps a
    /// far-out root position exact: at the root the matrix is the identity, so the position passes through
    /// bit-for-bit instead of being rounded to float.
    /// </summary>
    public Double3 TransformBy(in Matrix4x4 m)
        => new(
            (X * m.M11) + (Y * m.M21) + (Z * m.M31),
            (X * m.M12) + (Y * m.M22) + (Z * m.M32),
            (X * m.M13) + (Y * m.M23) + (Z * m.M33));

    public double Length => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));

    /// <summary>Squared magnitude — avoids the square root where only comparisons are needed (broadphase).</summary>
    public double LengthSquared => (X * X) + (Y * Y) + (Z * Z);

    public static double Distance(Double3 a, Double3 b) => (a - b).Length;

    /// <summary>Squared distance between two points (no square root).</summary>
    public static double DistanceSquared(Double3 a, Double3 b) => (a - b).LengthSquared;

    /// <summary>Component-wise minimum.</summary>
    public static Double3 Min(Double3 a, Double3 b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

    /// <summary>Component-wise maximum.</summary>
    public static Double3 Max(Double3 a, Double3 b)
        => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    /// <summary>
    /// Narrows to a float <see cref="Vector3"/> expressed RELATIVE to <paramref name="origin"/> — the
    /// camera-relative operation that materialises large-coordinate rendering (spec §3.3). Subtracting in
    /// double BEFORE narrowing is what preserves precision: near the camera <c>this - origin</c> is small even
    /// when both are enormous, so the float result keeps its mantissa. With <see cref="Zero"/> it is a plain
    /// narrow, and a float value widened into a <see cref="Double3"/> narrows back to the identical float bits.
    /// </summary>
    public Vector3 ToVector3(Double3 origin)
    {
        var d = this - origin;
        return new Vector3((float)d.X, (float)d.Y, (float)d.Z);
    }

    public bool Equals(Double3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    public override bool Equals(object? obj) => obj is Double3 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public static bool operator ==(Double3 a, Double3 b) => a.Equals(b);

    public static bool operator !=(Double3 a, Double3 b) => !a.Equals(b);

    public override string ToString() => $"({X}, {Y}, {Z})";
}
