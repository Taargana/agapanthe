using System.Numerics;

namespace Agapanthe.Ui;

/// <summary>
/// Draws a series of samples as a bar graph (UI-2). Pure, GPU-free and allocation-free: it turns a
/// <see cref="ReadOnlySpan{T}"/> of floats into quads in a <see cref="UiDrawList"/>, so it is unit-testable with no
/// device — the same reason the rest of <c>Agapanthe.Ui</c> lives here rather than in the renderer.
/// <para>
/// Bars sample the font atlas's reserved white texel, so a graph costs no extra texture, no extra pipeline and no
/// extra draw call: it merges into the frame's single UI draw alongside the text.
/// </para>
/// </summary>
public static class Sparkline
{
    /// <summary>
    /// Draws <paramref name="samples"/> as vertical bars filling <paramref name="rect"/> (minX, minY, maxX, maxY in
    /// framebuffer pixels), scaled so that <paramref name="scaleMax"/> reaches the full height.
    /// <para>
    /// Bars are drawn oldest-first, left to right. When there are more samples than pixels of width, the series is
    /// <b>subsampled</b> rather than drawn sub-pixel: a bar thinner than a pixel would rasterise to nothing or to
    /// aliased noise, and the graph would look like it were lying.
    /// </para>
    /// </summary>
    /// <param name="scaleMax">
    /// Value mapped to full height. Pass the series' own max for an auto-scaling graph, or a fixed budget (e.g.
    /// 16.7 ms) for a graph where the bar height is comparable across frames. A non-positive value, or a flat
    /// series, yields empty bars rather than a division by zero.
    /// </param>
    /// <returns>The number of bars emitted.</returns>
    public static int Draw(
        UiDrawList drawList,
        ReadOnlySpan<float> samples,
        Vector4 rect,
        Vector4 whiteTexelUv,
        uint rgba,
        float scaleMax)
    {
        ArgumentNullException.ThrowIfNull(drawList);

        var width = rect.Z - rect.X;
        var height = rect.W - rect.Y;
        if (samples.IsEmpty || width <= 0f || height <= 0f)
        {
            return 0;
        }

        // One bar per pixel column at most.
        var barCount = Math.Min(samples.Length, (int)width);
        if (barCount <= 0)
        {
            return 0;
        }

        var barWidth = width / barCount;
        var drawn = 0;

        for (var i = 0; i < barCount; i++)
        {
            // Subsample: map bar i back onto the series so the FULL history is represented, not just its head.
            var sourceIndex = barCount == samples.Length
                ? i
                : (int)((long)i * samples.Length / barCount);
            var value = samples[sourceIndex];

            // A non-positive scale (empty or flat series) collapses every bar to zero height: honest, and it cannot
            // divide by zero.
            // float.IsFinite is not redundant with Clamp: Clamp returns its input unchanged when neither
            // comparison holds, which is exactly the case for NaN — a NaN would sail through and put a quad with
            // NaN coordinates in front of the rasteriser. This is a PUBLIC primitive meant for arbitrary
            // application series, where a divide-by-zero upstream is a matter of time.
            var normalized = scaleMax > 0f && float.IsFinite(value)
                ? Math.Clamp(value / scaleMax, 0f, 1f)
                : 0f;
            var barHeight = normalized * height;
            if (barHeight > 0f && barHeight < 1f)
            {
                barHeight = 1f; // "this frame allocated" must never disappear into sub-pixel rounding
            }

            if (barHeight <= 0f)
            {
                continue; // nothing to draw; still counted as a column, just empty
            }

            var x0 = rect.X + (i * barWidth);
            var x1 = x0 + barWidth;
            // Bars grow UPWARD from the bottom edge; screen Y points down, so the top edge is the smaller value.
            var y1 = rect.W;
            var y0 = y1 - barHeight;

            drawList.AddRect(new Vector4(x0, y0, x1, y1), whiteTexelUv, rgba);
            drawn++;
        }

        return drawn;
    }
}
