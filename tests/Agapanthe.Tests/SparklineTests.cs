using System.Numerics;
using Agapanthe.Ui;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free tests for the profiler's graph primitive (UI-2). Bars are plain quads in a draw list, so every property
/// that matters — count, bounds, scaling, degenerate inputs — is checkable with no device.
/// </summary>
public class SparklineTests
{
    private static readonly Vector4 White = new(0.9f, 0.9f, 1f, 1f);
    private const uint Colour = 0x40C0FFFFu;

    // 100 px wide, 50 px tall, anchored away from the origin so bounds bugs cannot hide behind zeros.
    private static Vector4 Rect() => new(10f, 20f, 110f, 70f);

    [Fact]
    public void Draw_EmitsOneBarPerSample()
    {
        var list = new UiDrawList();

        var drawn = Sparkline.Draw(list, [1f, 2f, 3f, 4f], Rect(), White, Colour, scaleMax: 4f);

        Assert.Equal(4, drawn);
        Assert.Equal(4, list.Count);
    }

    [Fact]
    public void Draw_BarsStayInsideTheRect()
    {
        var list = new UiDrawList();
        var rect = Rect();

        Sparkline.Draw(list, [1f, 5f, 2f, 4f, 3f], rect, White, Colour, scaleMax: 5f);

        foreach (var quad in list.Quads)
        {
            Assert.True(quad.Rect.X >= rect.X - 0.01f, "bar escaped the left edge");
            Assert.True(quad.Rect.Z <= rect.Z + 0.01f, "bar escaped the right edge");
            Assert.True(quad.Rect.Y >= rect.Y - 0.01f, "bar escaped the top edge");
            Assert.True(quad.Rect.W <= rect.W + 0.01f, "bar escaped the bottom edge");
        }
    }

    [Fact]
    public void Draw_BarsGrowUpwardFromTheBottomEdge()
    {
        var list = new UiDrawList();
        var rect = Rect();

        Sparkline.Draw(list, [1f, 4f], rect, White, Colour, scaleMax: 4f);

        var small = list.Quads[0];
        var tall = list.Quads[1];

        // Both sit on the bottom edge; the taller one starts higher up (smaller Y, since screen Y points down).
        Assert.Equal(rect.W, small.Rect.W, 3);
        Assert.Equal(rect.W, tall.Rect.W, 3);
        Assert.True(tall.Rect.Y < small.Rect.Y, "the larger sample must produce the taller bar");
    }

    [Fact]
    public void Draw_ScalesAgainstScaleMaxNotTheSeriesMax()
    {
        // A fixed budget (e.g. a 16.7 ms frame target) must make bar height comparable across calls.
        var list = new UiDrawList();
        var rect = Rect();

        Sparkline.Draw(list, [5f], rect, White, Colour, scaleMax: 10f);

        var height = rect.W - list.Quads[0].Rect.Y;
        Assert.Equal((rect.W - rect.Y) * 0.5f, height, 3); // half the budget → half the height
    }

    [Fact]
    public void Draw_ClampsValuesAboveTheScale()
    {
        // A spike past the budget must fill the graph, never overflow it.
        var list = new UiDrawList();
        var rect = Rect();

        Sparkline.Draw(list, [999f], rect, White, Colour, scaleMax: 10f);

        Assert.Equal(rect.Y, list.Quads[0].Rect.Y, 3);
    }

    [Fact]
    public void Draw_SubsamplesWhenSamplesOutnumberPixels()
    {
        // A bar narrower than a pixel rasterises to nothing or to aliased noise — the graph would look like it were
        // lying. One bar per pixel column at most.
        var list = new UiDrawList();
        var samples = new float[1000];
        Array.Fill(samples, 1f);

        var drawn = Sparkline.Draw(list, samples, Rect(), White, Colour, scaleMax: 1f);

        Assert.Equal(100, drawn); // the rect is 100 px wide
    }

    [Fact]
    public void Draw_SubsamplingSpansTheWholeSeries()
    {
        // Subsampling must represent the FULL history, not just its head: a 2 px graph of [0,0,…,1,1] has to show
        // the tail, otherwise a recent spike is invisible.
        var list = new UiDrawList();
        var samples = new float[100];
        for (var i = 50; i < 100; i++)
        {
            samples[i] = 1f;
        }

        Sparkline.Draw(list, samples, new Vector4(0f, 0f, 2f, 10f), White, Colour, scaleMax: 1f);

        Assert.Equal(1, list.Count); // first half is zero (no bar), second half draws one
    }

    // ---- Degenerate inputs --------------------------------------------------------------------------------------

    [Fact]
    public void Draw_EmptySeriesDrawsNothing()
    {
        var list = new UiDrawList();

        Assert.Equal(0, Sparkline.Draw(list, [], Rect(), White, Colour, scaleMax: 1f));
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void Draw_FlatZeroSeriesDrawsNoBars()
    {
        // scaleMax = 0 is the "series is all zeros" case — exactly what a healthy 0-alloc graph looks like. It must
        // render an empty graph, not divide by zero.
        var list = new UiDrawList();

        var drawn = Sparkline.Draw(list, [0f, 0f, 0f], Rect(), White, Colour, scaleMax: 0f);

        Assert.Equal(0, drawn);
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void Draw_NegativeScaleIsTreatedAsEmpty()
    {
        var list = new UiDrawList();

        Assert.Equal(0, Sparkline.Draw(list, [1f, 2f], Rect(), White, Colour, scaleMax: -5f));
    }

    [Theory]
    [InlineData(0f, 50f)] // zero width
    [InlineData(100f, 0f)] // zero height
    public void Draw_DegenerateRectDrawsNothing(float width, float height)
    {
        var list = new UiDrawList();

        var drawn = Sparkline.Draw(
            list, [1f, 2f], new Vector4(10f, 20f, 10f + width, 20f + height), White, Colour, scaleMax: 2f);

        Assert.Equal(0, drawn);
    }

    [Fact]
    public void Draw_SolidBarsCarryNoSdfFlag()
    {
        // Bars sample the atlas's white texel: they must take the solid branch of the shader, not the glyph one.
        var list = new UiDrawList();

        Sparkline.Draw(list, [1f], Rect(), White, Colour, scaleMax: 1f);

        Assert.Equal(0u, list.Quads[0].Flags & UiQuad.FlagSdfGlyph);
        Assert.Equal(White, list.Quads[0].UvRect);
    }

    [Fact]
    public void Draw_NonFiniteSampleDrawsNoBar()
    {
        // Math.Clamp passes NaN straight through (every comparison with NaN is false), so without an explicit
        // finiteness check a NaN sample would put a quad with NaN coordinates in front of the rasteriser. This is a
        // PUBLIC primitive for arbitrary application series, where an upstream divide-by-zero is a matter of time.
        var list = new UiDrawList();

        Sparkline.Draw(list, [float.NaN, float.PositiveInfinity, 1f], Rect(), White, Colour, scaleMax: 1f);

        Assert.Equal(1, list.Count); // only the finite sample drew
        foreach (var quad in list.Quads)
        {
            Assert.True(float.IsFinite(quad.Rect.X) && float.IsFinite(quad.Rect.Y), "quad has non-finite coords");
            Assert.True(float.IsFinite(quad.Rect.Z) && float.IsFinite(quad.Rect.W), "quad has non-finite coords");
        }
    }

    [Fact]
    public void Draw_TinyButNonZeroSampleStillDrawsAVisibleBar()
    {
        // A regression of a few hundred bytes against a scale set by one big warm-up spike would rasterise to a
        // fraction of a pixel — an empty graph while the gate is broken. Any non-zero sample gets at least 1 px.
        var list = new UiDrawList();

        Sparkline.Draw(list, [0.0001f], Rect(), White, Colour, scaleMax: 1f);

        Assert.Equal(1, list.Count);
        Assert.True(list.Quads[0].Rect.W - list.Quads[0].Rect.Y >= 1f, "a non-zero sample must be at least 1 px tall");
    }

    // ---- Zero allocation ----------------------------------------------------------------------------------------

    [Fact]
    public void Draw_AllocatesNothingAfterWarmup()
    {
        var list = new UiDrawList(initialCapacity: 512);
        var samples = new float[240];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i % 17;
        }

        AllocationProbe.AssertNoAllocation("Sparkline.Draw", () =>
        {
            list.Clear();
            Sparkline.Draw(list, samples, Rect(), White, Colour, scaleMax: 16f);
        });
    }
}
