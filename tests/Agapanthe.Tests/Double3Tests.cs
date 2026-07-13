using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Tests;

public sealed class Double3Tests
{
    [Fact]
    public void Arithmetic_AddSubtractScale()
    {
        var a = new Double3(1, 2, 3);
        var b = new Double3(10, 20, 30);

        Assert.Equal(new Double3(11, 22, 33), a + b);
        Assert.Equal(new Double3(9, 18, 27), b - a);
        Assert.Equal(new Double3(2, 4, 6), a * 2.0);
    }

    [Fact]
    public void LengthAndDistance()
    {
        Assert.Equal(5.0, new Double3(3, 4, 0).Length, 12);
        Assert.Equal(13.0, Double3.Distance(new Double3(0, 0, 0), new Double3(3, 4, 12)), 12);
    }

    [Theory]
    [InlineData(1.2345f)]
    [InlineData(-9876.543f)]
    [InlineData(0.1f)]
    [InlineData(3.4028e30f)]
    [InlineData(float.MaxValue)]
    public void ToVector3_AtOrigin_ReNarrowsWidenedFloatBitExact(float f)
    {
        // The M2 bounds path is: float vertex-fold -> widen to Double3 -> ToVector3(Zero) -> back to float.
        // For the capture to stay byte-identical, that round trip must return the SAME float bits.
        var widened = new Double3(f, f, f);
        var narrowed = widened.ToVector3(Double3.Zero);

        Assert.Equal(BitConverter.SingleToInt32Bits(f), BitConverter.SingleToInt32Bits(narrowed.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(f), BitConverter.SingleToInt32Bits(narrowed.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(f), BitConverter.SingleToInt32Bits(narrowed.Z));
    }

    [Fact]
    public void ToVector3_CameraRelative_KeepsPrecisionFarFromOrigin()
    {
        // 10,000 km from the origin (the spec's number), two points 10 cm apart. Absolute float32 cannot
        // resolve 10 cm there — but subtracting in double first (camera-relative) keeps it.
        var origin = new Double3(1e7, 0, 0);
        var point = new Double3(1e7 + 0.1, 0, 0);

        Assert.Equal(0.1, point.ToVector3(origin).X, 3); // camera-relative: preserved

        // The naive absolute-float path collapses the two positions to the same float -> difference is 0.
        Assert.Equal(0f, (float)point.X - (float)origin.X);
    }

    [Fact]
    public void WidenFromVector3_IsExact()
    {
        var v = new Vector3(1.5f, -2.25f, 100.125f);
        var d = new Double3(v);

        Assert.Equal((double)v.X, d.X);
        Assert.Equal((double)v.Y, d.Y);
        Assert.Equal((double)v.Z, d.Z);
    }

    [Fact]
    public void Bounds_UnionAndCenterDiagonal()
    {
        var a = new Double3Bounds(new Double3(-1, -1, -1), new Double3(1, 1, 1));
        var b = new Double3Bounds(new Double3(0, 0, 0), new Double3(3, 0, 0));
        var u = Double3Bounds.Union(a, b);

        Assert.Equal(new Double3(-1, -1, -1), u.Min);
        Assert.Equal(new Double3(3, 1, 1), u.Max);
        Assert.Equal(new Double3(1, 0, 0), u.Center);

        // Empty seeds a fold: Union(Empty, x) == x.
        Assert.Equal(a.Min, Double3Bounds.Union(Double3Bounds.Empty, a).Min);
        Assert.Equal(a.Max, Double3Bounds.Union(Double3Bounds.Empty, a).Max);
    }
}
