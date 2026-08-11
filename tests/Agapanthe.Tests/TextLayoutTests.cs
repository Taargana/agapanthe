using System.Numerics;
using Agapanthe.Assets.Font;
using Agapanthe.Ui;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free tests for shaping, layout and the draw list (UI-1). All of this is pure logic by design — the reason
/// Agapanthe.Ui exists as its own project is so it can be proven correct with no device, exactly like Assets.
/// </summary>
public class TextLayoutTests
{
    // A synthetic monospace-ish font: every glyph advances 0.5 em, ink box 0.4 × 0.7 em. Synthetic on purpose —
    // the assertions must hold regardless of what tables the shipped face happens to carry (a real monospace face
    // has no kerning at all, so kerning could not be tested against it).
    private const float Advance = 0.5f;

    private static FontAsset MakeFont(bool withKerning = false, bool withTofu = false, bool withSpace = true)
    {
        var codepoints = new List<uint>();
        for (var c = 'A'; c <= 'Z'; c++)
        {
            codepoints.Add(c);
        }

        if (withSpace)
        {
            codepoints.Add(' ');
        }

        if (withTofu)
        {
            codepoints.Add(0xFFFD);
        }

        codepoints.Sort(); // the format requires ascending codepoints

        var glyphs = new GlyphMetrics[codepoints.Count];
        for (var i = 0; i < codepoints.Count; i++)
        {
            var isSpace = codepoints[i] == ' ';
            glyphs[i] = new GlyphMetrics(
                codepoints[i],
                new Vector4(0f, 0f, 0.1f, 0.1f),
                // Space has a ZERO-AREA ink box: it advances the pen but must not emit a quad.
                isSpace ? Vector2.Zero : new Vector2(0.05f, 0f),
                isSpace ? Vector2.Zero : new Vector2(0.45f, 0.7f),
                Advance);
        }

        var kerns = withKerning
            ? new[] { new KernPair('A', 'V', -0.1f) }
            : [];

        return new FontAsset(
            new byte[4], 2, 2, glyphs, kerns,
            ascender: 0.8f, descender: -0.2f, lineHeight: 1f,
            sdfPixelRange: 4f, emPixelSize: 64f,
            whiteTexelUv: new Vector4(0.9f, 0.9f, 1f, 1f));
    }

    // ---- Measure ----------------------------------------------------------------------------------------------

    [Fact]
    public void Measure_SingleLine_IsAdvanceTimesCountTimesPixelSize()
    {
        // 4 glyphs × 0.5 em × 20 px/em = 40 px wide; one line of lineHeight 1 em = 20 px tall.
        var extent = TextLayout.Measure("ABCD", MakeFont(), pixelSize: 20f);

        Assert.Equal(40f, extent.Width, 3);
        Assert.Equal(20f, extent.Height, 3);
        Assert.Equal(1, extent.LineCount);
    }

    [Fact]
    public void Measure_Empty_IsZero()
    {
        var extent = TextLayout.Measure("", MakeFont(), pixelSize: 20f);

        Assert.Equal(0f, extent.Width);
        Assert.Equal(0, extent.LineCount);
    }

    [Fact]
    public void Measure_MultiLine_UsesWidestLineAndCountsLines()
    {
        // "AB" (2) / "ABCDE" (5) / "A" (1) → widest is 5 × 0.5 × 20 = 50 px, 3 lines × 20 px = 60 px.
        var extent = TextLayout.Measure("AB\nABCDE\nA", MakeFont(), pixelSize: 20f);

        Assert.Equal(50f, extent.Width, 3);
        Assert.Equal(60f, extent.Height, 3);
        Assert.Equal(3, extent.LineCount);
    }

    [Fact]
    public void Measure_TrailingNewline_OpensAnEmptyLine()
    {
        Assert.Equal(2, TextLayout.Measure("AB\n", MakeFont(), 20f).LineCount);
    }

    [Fact]
    public void Measure_CarriageReturn_IsIgnored()
    {
        // CRLF must count as ONE line break, not two.
        Assert.Equal(2, TextLayout.Measure("AB\r\nCD", MakeFont(), 20f).LineCount);
    }

    [Fact]
    public void Measure_AppliesKerning()
    {
        // "AV" with a -0.1 em pair: 2 advances (1.0) minus 0.1 = 0.9 em → 18 px at 20 px/em.
        var kerned = TextLayout.Measure("AV", MakeFont(withKerning: true), 20f);
        var plain = TextLayout.Measure("AV", MakeFont(withKerning: false), 20f);

        Assert.Equal(18f, kerned.Width, 3);
        Assert.Equal(20f, plain.Width, 3);
    }

    [Fact]
    public void Measure_KerningDoesNotCrossLines()
    {
        // The pair A,V exists but they are on different lines: no kerning may apply across the break.
        var extent = TextLayout.Measure("A\nV", MakeFont(withKerning: true), 20f);

        Assert.Equal(10f, extent.Width, 3); // widest line is a single 0.5 em glyph
    }

    // ---- Missing glyphs ---------------------------------------------------------------------------------------

    [Fact]
    public void MissingGlyph_WithoutTofu_SkipsQuadButKeepsAdvance()
    {
        // '@' is absent. Layout must stay aligned (advance preserved) and must NOT emit a quad for it.
        var font = MakeFont(withTofu: false);
        var list = new UiDrawList();

        var extent = TextLayout.DrawText(list, "A@B", font, Vector2.Zero, 20f, 0xFFFFFFFFu);

        Assert.Equal(30f, extent.Width, 3); // 3 advances, one of them from the missing glyph
        Assert.Equal(2, list.Count); // only A and B produced ink
    }

    [Fact]
    public void MissingGlyph_WithTofu_EmitsReplacementGlyph()
    {
        var list = new UiDrawList();

        TextLayout.DrawText(list, "A@B", MakeFont(withTofu: true), Vector2.Zero, 20f, 0xFFFFFFFFu);

        Assert.Equal(3, list.Count); // A, tofu, B
    }

    [Fact]
    public void MissingGlyph_NeverThrows()
    {
        // A frame must never die mid-draw over content: exotic codepoints, unpaired surrogates, control chars.
        var list = new UiDrawList();
        var exception = Record.Exception(
            () => TextLayout.DrawText(list, "你好\uD800", MakeFont(), Vector2.Zero, 20f, 0xFFu));

        Assert.Null(exception);
    }

    // ---- Quad generation --------------------------------------------------------------------------------------

    [Fact]
    public void DrawText_EmitsOneQuadPerInkedGlyph_SpacesCostNothing()
    {
        var list = new UiDrawList();

        TextLayout.DrawText(list, "A B", MakeFont(), Vector2.Zero, 20f, 0xFFFFFFFFu);

        Assert.Equal(2, list.Count); // the space has a zero-area box
    }

    [Fact]
    public void DrawText_QuadsCarryColourAndSdfFlag()
    {
        var list = new UiDrawList();

        TextLayout.DrawText(list, "A", MakeFont(), Vector2.Zero, 20f, 0x11223344u);

        var quad = list.Quads[0];
        Assert.Equal(0x11223344u, quad.Rgba);
        Assert.Equal(UiQuad.FlagSdfGlyph, quad.Flags & UiQuad.FlagSdfGlyph);
    }

    [Fact]
    public void DrawText_PlacesGlyphsLeftToRight_AndSecondLineBelowFirst()
    {
        var list = new UiDrawList();

        TextLayout.DrawText(list, "AB\nC", MakeFont(), new Vector2(100f, 200f), 20f, 0xFFu);

        var a = list.Quads[0];
        var b = list.Quads[1];
        var c = list.Quads[2];

        Assert.True(b.Rect.X > a.Rect.X, "second glyph must sit to the right of the first");
        Assert.Equal(a.Rect.Y, b.Rect.Y, 3); // same line → same vertical placement
        Assert.True(c.Rect.Y > a.Rect.Y, "second line must sit below the first");
        Assert.Equal(a.Rect.X, c.Rect.X, 3); // both lines start at the same left edge
    }

    [Fact]
    public void DrawText_StaysWithinItsMeasuredBox()
    {
        // Measure and DrawText must agree, or every layout built on Measure would be subtly wrong.
        var font = MakeFont();
        var list = new UiDrawList();
        var origin = new Vector2(50f, 60f);

        var extent = TextLayout.DrawText(list, "ABC\nDE", font, origin, 20f, 0xFFu);

        foreach (var quad in list.Quads)
        {
            Assert.True(quad.Rect.X >= origin.X - 0.01f, "glyph left edge escaped the box");
            Assert.True(quad.Rect.Z <= origin.X + extent.Width + 0.01f, "glyph right edge escaped the box");
            Assert.True(quad.Rect.Y >= origin.Y - 0.01f, "glyph top edge escaped the box");
            Assert.True(quad.Rect.W <= origin.Y + extent.Height + 0.01f, "glyph bottom edge escaped the box");
        }
    }

    // ---- Alignment --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TextAlign.Left)]
    [InlineData(TextAlign.Center)]
    [InlineData(TextAlign.Right)]
    public void Align_ShortLineMovesRelativeToTheLongest(TextAlign align)
    {
        // "ABCD" is 4 × 0.5 em × 20 px = 40 px wide; "A" is 10 px. Left → flush, centre → indented by half the
        // 30 px difference, right → by all of it.
        var list = new UiDrawList();
        TextLayout.DrawText(list, "ABCD\nA", MakeFont(), Vector2.Zero, 20f, 0xFFu, align);

        var firstLineStart = list.Quads[0].Rect.X;
        var shortLineStart = list.Quads[^1].Rect.X;
        var expectedOffset = align switch
        {
            TextAlign.Center => 15f, // (40 - 10) / 2
            TextAlign.Right => 30f, // 40 - 10
            _ => 0f,
        };

        Assert.Equal(firstLineStart + expectedOffset, shortLineStart, 3);
    }

    // ---- Draw list --------------------------------------------------------------------------------------------

    [Fact]
    public void DrawList_ClearKeepsCapacity_AndPreservesSubmissionOrder()
    {
        var list = new UiDrawList(initialCapacity: 1);
        var white = new Vector4(0.9f, 0.9f, 1f, 1f);

        list.AddRect(new Vector4(0f, 0f, 1f, 1f), white, 0x111111FFu);
        list.AddRect(new Vector4(2f, 2f, 3f, 3f), white, 0x222222FFu);

        Assert.Equal(2, list.Count);
        Assert.Equal(0x111111FFu, list.Quads[0].Rgba); // painter's algorithm: first added draws first
        Assert.Equal(0x222222FFu, list.Quads[1].Rgba);

        list.Clear();
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void DrawList_SolidRectHasNoSdfFlag()
    {
        var list = new UiDrawList();

        list.AddRect(new Vector4(0f, 0f, 4f, 4f), new Vector4(0.9f, 0.9f, 1f, 1f), 0xFFu);

        Assert.Equal(0u, list.Quads[0].Flags & UiQuad.FlagSdfGlyph);
    }

    // ---- CPU/GPU layout ---------------------------------------------------------------------------------------

    [Fact]
    public void UiQuad_MatchesTheStd430ArrayStride()
    {
        // The shader reads these as a std430 array, whose stride is the struct size rounded up to its largest
        // member alignment (vec4 = 16) → 48, not the 40 bytes the four logical fields occupy. A mismatch here would
        // desynchronise after the FIRST quad and every glyph would render its neighbour's data — silent, and
        // miserable to debug. The explicit reserved words keep both sides at 48.
        Assert.Equal(UiQuad.SizeInBytes, System.Runtime.CompilerServices.Unsafe.SizeOf<UiQuad>());
        Assert.Equal(0, UiQuad.SizeInBytes % 16); // std430 vec4 alignment
    }

    // ---- Zero allocation --------------------------------------------------------------------------------------

    [Fact]
    public void Measure_AllocatesNothingAfterWarmup()
    {
        var font = MakeFont();
        _ = TextLayout.Measure("warmup", font, 20f); // JIT + first touch

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = TextLayout.Measure("fps 142 · draws 2+4", font, 20f);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"Measure should allocate nothing, observed {allocated} bytes.");
    }

    [Fact]
    public void DrawText_AllocatesNothingAfterWarmup()
    {
        // The steady-state gate: a HUD line redrawn every frame must cost zero managed allocation, which is what
        // lets the overlay refresh continuously instead of only when its text changes.
        var font = MakeFont();
        var list = new UiDrawList(initialCapacity: 512);
        TextLayout.DrawText(list, "warmup", font, Vector2.Zero, 20f, 0xFFu); // JIT + grow the backing array
        list.Clear();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            list.Clear();
            TextLayout.DrawText(list, "FPS 142 DRAWS 2", font, Vector2.Zero, 20f, 0xFFu);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"DrawText should allocate nothing, observed {allocated} bytes.");
    }
}
