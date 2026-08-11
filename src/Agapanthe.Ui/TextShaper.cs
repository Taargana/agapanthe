using System.Buffers;
using System.Text;
using Agapanthe.Assets.Font;

namespace Agapanthe.Ui;

/// <summary>
/// One glyph placed on a line: which glyph, and where its pen sat (UI-1). The output of shaping and the input of
/// quad generation — keeping them separate is what lets a real shaper replace <see cref="TextShaper"/> later.
/// </summary>
/// <param name="Glyph">The resolved glyph metrics, already looked up in the font.</param>
/// <param name="PenX">Pen X at this glyph, in em units from the start of the line.</param>
/// <param name="Line">Zero-based line index (incremented at every <c>\n</c>).</param>
public readonly record struct PositionedGlyph(GlyphMetrics Glyph, float PenX, int Line);

/// <summary>
/// Turns text into positioned glyphs (UI-1).
/// <para>
/// <b>This is a seam, deliberately.</b> Today it does the simple thing: decode to <see cref="Rune"/>s, map each one
/// to a glyph, advance the pen, apply pair kerning. That is correct for Latin, Cyrillic and Greek, where one
/// codepoint is one glyph. It is <i>not</i> correct for Arabic or Devanagari, which need contextual forms, mandatory
/// ligatures and mark positioning — a real shaping engine (HarfBuzz).
/// </para>
/// <para>
/// The reason this type exists at all, rather than the layout walking characters inline, is that swapping in such an
/// engine must not force a rewrite of every caller: the contract <c>text → PositionedGlyph[]</c> stays, only the
/// implementation behind it changes. Decoding via <see cref="Rune"/> rather than <c>char</c> is part of the same
/// discipline — it means a surrogate pair (emoji, rare CJK) is one codepoint here, not two broken halves.
/// </para>
/// </summary>
public static class TextShaper
{
    /// <summary>
    /// Shapes <paramref name="text"/> into <paramref name="destination"/>, returning the number of glyphs written
    /// and the number of lines. Allocation-free: the caller owns the buffer.
    /// <para>
    /// A codepoint the font does not carry falls back to the replacement glyph if the font has one, and is otherwise
    /// <b>skipped while still advancing the pen</b> by the space width — text stays readable and aligned, and a
    /// missing glyph never throws in the middle of a frame.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> if everything fitted in <paramref name="destination"/>; <c>false</c> if it was truncated.</returns>
    public static bool Shape(
        ReadOnlySpan<char> text,
        FontAsset font,
        Span<PositionedGlyph> destination,
        out int glyphCount,
        out int lineCount)
    {
        ArgumentNullException.ThrowIfNull(font);

        glyphCount = 0;
        lineCount = text.IsEmpty ? 0 : 1;

        var penX = 0f;
        var line = 0;
        var previous = 0u; // 0 = no previous codepoint, so no kerning on the first glyph of a line
        var index = 0;
        var fitted = true;

        while (index < text.Length)
        {
            if (Rune.DecodeFromUtf16(text[index..], out var rune, out var consumed) != OperationStatus.Done)
            {
                // Unpaired surrogate or malformed input: consume it as the replacement character rather than
                // aborting. Text rendering must degrade, never fail.
                rune = Rune.ReplacementChar;
            }

            index += consumed;
            var codepoint = (uint)rune.Value;

            if (codepoint == '\n')
            {
                line++;
                lineCount = line + 1;
                penX = 0f;
                previous = 0u;
                continue;
            }

            if (codepoint == '\r')
            {
                continue; // CRLF: the \n does the work
            }

            if (!font.TryGetGlyph(codepoint, out var glyph))
            {
                // Tofu (U+FFFD) if the font ships one, otherwise skip but keep the pen moving so columns stay aligned.
                if (!font.TryGetGlyph((uint)Rune.ReplacementChar.Value, out glyph))
                {
                    penX += MissingAdvance(font);
                    previous = 0u;
                    continue;
                }
            }

            if (previous != 0u)
            {
                penX += font.GetKerning(previous, codepoint);
            }

            if (glyphCount < destination.Length)
            {
                destination[glyphCount] = new PositionedGlyph(glyph, penX, line);
                glyphCount++;
            }
            else
            {
                fitted = false;
            }

            penX += glyph.Advance;
            previous = codepoint;
        }

        return fitted;
    }

    /// <summary>
    /// Advance used for a codepoint the font cannot render at all, in em units: the space glyph's advance, or half
    /// the line height when even space is missing (a degenerate font, but the layout must still produce something).
    /// </summary>
    internal static float MissingAdvance(FontAsset font)
        => font.TryGetGlyph(' ', out var space) ? space.Advance : font.LineHeight * 0.5f;
}
