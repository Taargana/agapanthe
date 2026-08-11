using System.Numerics;
using System.Runtime.CompilerServices;
using Agapanthe.Assets.Font;

namespace Agapanthe.Ui;

/// <summary>Horizontal alignment of a text block relative to its anchor point.</summary>
public enum TextAlign
{
    Left = 0,
    Center,
    Right,
}

/// <summary>The size of a laid-out text block, in pixels.</summary>
/// <param name="Width">Width of the widest line.</param>
/// <param name="Height">Total height: <c>lineCount × lineHeight</c>.</param>
/// <param name="LineCount">Number of lines (a trailing <c>\n</c> opens a new, possibly empty, line).</param>
public readonly record struct TextExtent(float Width, float Height, int LineCount);

/// <summary>
/// Measures text and turns it into quads (UI-1). Pure, GPU-free and allocation-free: it reads a
/// <see cref="FontAsset"/> and writes into caller-owned spans, so it is fully unit-testable with no device.
/// <para>
/// All sizes are in <b>pixels</b>; the font's metrics are in em units, so everything scales by
/// <c>pixelSize</c>. One SDF atlas therefore serves every size — which is the whole reason the atlas is a distance
/// field rather than a fixed-size bitmap.
/// </para>
/// </summary>
public static class TextLayout
{
    // Scratch capacity for one call. 256 glyphs × 48 bytes = 12 KiB of stack — deliberately NOT larger: C# emits
    // `.locals init`, so the buffer is zero-filled on EVERY call, and that cost is invisible to the project's
    // 0-alloc gate (which measures the managed heap, not the stack). A HUD line is a few dozen glyphs; text longer
    // than this is truncated rather than made to cost a quarter of a megabyte of memset per frame.
    private const int MaxGlyphsPerCall = 256;

    // Line-width scratch. Also bounded, for the same reason.
    private const int MaxLines = 64;

    /// <summary>
    /// Measures <paramref name="text"/> without emitting anything. Allocation-free.
    /// </summary>
    public static TextExtent Measure(ReadOnlySpan<char> text, FontAsset font, float pixelSize)
    {
        ArgumentNullException.ThrowIfNull(font);

        Span<PositionedGlyph> glyphs = stackalloc PositionedGlyph[MaxGlyphsPerCall];
        Span<float> lineWidths = stackalloc float[MaxLines];
        TextShaper.Shape(text, font, glyphs, out var glyphCount, out var lineCount);
        return MeasureShaped(glyphs[..glyphCount], lineWidths, font, pixelSize, ref lineCount);
    }

    /// <summary>
    /// Appends the quads for <paramref name="text"/> to <paramref name="drawList"/>, anchored at
    /// <paramref name="position"/> (the <b>top-left</b> of the text block, before alignment) and returns the block's
    /// extent. Allocation-free.
    /// </summary>
    public static TextExtent DrawText(
        UiDrawList drawList,
        ReadOnlySpan<char> text,
        FontAsset font,
        Vector2 position,
        float pixelSize,
        uint rgba,
        TextAlign align = TextAlign.Left)
    {
        ArgumentNullException.ThrowIfNull(drawList);
        ArgumentNullException.ThrowIfNull(font);

        Span<PositionedGlyph> glyphs = stackalloc PositionedGlyph[MaxGlyphsPerCall];
        Span<float> lineWidths = stackalloc float[MaxLines];
        TextShaper.Shape(text, font, glyphs, out var glyphCount, out var lineCount);
        var shaped = glyphs[..glyphCount];
        var extent = MeasureShaped(shaped, lineWidths, font, pixelSize, ref lineCount);

        for (var i = 0; i < shaped.Length; i++)
        {
            var placed = shaped[i];
            if (placed.Line >= lineCount)
            {
                continue; // past the line cap
            }

            // Line widths were computed ONCE by MeasureShaped; recomputing one per glyph made this quadratic, on a
            // path the debug overlay runs every frame.
            var alignOffset = align switch
            {
                TextAlign.Center => (extent.Width - (lineWidths[placed.Line] * pixelSize)) * 0.5f,
                TextAlign.Right => extent.Width - (lineWidths[placed.Line] * pixelSize),
                _ => 0f, // Left: no per-line work at all
            };

            // The pen sits on the BASELINE, which is `ascender` below the block's top edge. Y grows downward on
            // screen while the font's plane coordinates grow upward, hence the sign flip on the plane bounds.
            //
            // The ORIGIN is snapped to whole pixels (never the sizes, which would distort glyph shapes): a
            // fractional origin puts every glyph on a different sub-pixel phase, so each is filtered differently
            // and the text shimmers as it moves — and at small sizes that blur is what costs legibility.
            var baselineY = MathF.Round(
                position.Y + ((placed.Line + 1) * font.LineHeight * pixelSize)
                - ((font.LineHeight - font.Ascender) * pixelSize));
            var originX = MathF.Round(position.X + alignOffset + (placed.PenX * pixelSize));

            var glyph = placed.Glyph;
            var x0 = originX + (glyph.PlaneMin.X * pixelSize);
            var x1 = originX + (glyph.PlaneMax.X * pixelSize);
            var y0 = baselineY - (glyph.PlaneMax.Y * pixelSize);
            var y1 = baselineY - (glyph.PlaneMin.Y * pixelSize);

            // A whitespace glyph has a zero-area box: it advances the pen but must not cost a quad.
            if (x1 > x0 && y1 > y0)
            {
                drawList.Add(new UiQuad(
                    new Vector4(x0, y0, x1, y1), glyph.AtlasUv, rgba, UiQuad.FlagSdfGlyph));
            }
        }

        return extent;
    }

    /// <summary>
    /// Fills <paramref name="lineWidths"/> (in EM units) and returns the block extent, in ONE pass over the shaped
    /// glyphs. <paramref name="lineCount"/> is clamped to the scratch capacity.
    /// </summary>
    private static TextExtent MeasureShaped(
        ReadOnlySpan<PositionedGlyph> shaped, Span<float> lineWidths, FontAsset font, float pixelSize,
        ref int lineCount)
    {
        if (lineCount > lineWidths.Length)
        {
            lineCount = lineWidths.Length;
        }

        lineWidths[..lineCount].Clear();

        // Width of a line is the last glyph's pen position plus its advance. Measuring by ADVANCE (rather than by
        // the ink box) is what keeps a trailing space significant and columns aligned.
        for (var i = 0; i < shaped.Length; i++)
        {
            var line = shaped[i].Line;
            if (line >= lineCount)
            {
                continue;
            }

            var end = shaped[i].PenX + shaped[i].Glyph.Advance;
            if (end > lineWidths[line])
            {
                lineWidths[line] = end;
            }
        }

        var widest = 0f;
        for (var line = 0; line < lineCount; line++)
        {
            if (lineWidths[line] > widest)
            {
                widest = lineWidths[line];
            }
        }

        return new TextExtent(widest * pixelSize, lineCount * font.LineHeight * pixelSize, lineCount);
    }
}
