using System.Numerics;
using Agapanthe.Assets.Font;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free tests for the <c>.agfont</c> container and the font lookups (UI-1). Mirrors the VS-1 snapshot suite:
/// a round-trip, a determinism check, and a robustness battery proving a corrupt file fails loudly with a reason
/// instead of producing a half-built asset or reading out of bounds.
/// </summary>
public class FontAssetTests
{
    // A small synthetic font. Deliberately NOT a real face: the kerning assertions must prove the lookup works
    // regardless of whether the shipped monospace face carries a legacy `kern` table (modern libre fonts usually
    // ship GPOS instead, which UI-1 does not read). See the milestone notes on the kerning seam.
    private static FontAsset MakeFont(int atlasWidth = 4, int atlasHeight = 2)
    {
        var atlas = new byte[atlasWidth * atlasHeight];
        for (var i = 0; i < atlas.Length; i++)
        {
            atlas[i] = (byte)(i * 7); // arbitrary but deterministic
        }

        // Codepoint-ascending, as the format requires: 'A' (65), 'V' (86), 'a' (97).
        var glyphs = new[]
        {
            new GlyphMetrics(65, new Vector4(0f, 0f, 0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(0.6f, 0.7f), 0.6f),
            new GlyphMetrics(86, new Vector4(0.5f, 0f, 1f, 0.5f), new Vector2(0f, 0f), new Vector2(0.6f, 0.7f), 0.6f),
            new GlyphMetrics(97, new Vector4(0f, 0.5f, 0.5f, 1f), new Vector2(0f, 0f), new Vector2(0.5f, 0.5f), 0.5f),
        };

        // Sorted by (First, Second), as the binary search requires.
        var kerns = new[]
        {
            new KernPair(65, 86, -0.08f), // "AV" tucks together
            new KernPair(86, 65, -0.06f), // "VA" too, by a different amount
        };

        return new FontAsset(
            atlas, atlasWidth, atlasHeight, glyphs, kerns,
            ascender: 0.8f, descender: -0.2f, lineHeight: 1.2f,
            sdfPixelRange: 4f, emPixelSize: 64f,
            whiteTexelUv: new Vector4(0.9f, 0.9f, 1f, 1f));
    }

    private static byte[] Serialize(FontAsset font)
    {
        using var stream = new MemoryStream();
        FontAssetFormat.Write(stream, font);
        return stream.ToArray();
    }

    // ---- Round-trip -------------------------------------------------------------------------------------------

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var original = MakeFont();

        var restored = FontAssetFormat.Read(Serialize(original));

        Assert.Equal(original.AtlasWidth, restored.AtlasWidth);
        Assert.Equal(original.AtlasHeight, restored.AtlasHeight);
        Assert.Equal(original.AtlasPixels, restored.AtlasPixels);
        Assert.Equal(original.Glyphs, restored.Glyphs);
        Assert.Equal(original.KernPairs, restored.KernPairs);
        Assert.Equal(original.Ascender, restored.Ascender);
        Assert.Equal(original.Descender, restored.Descender);
        Assert.Equal(original.LineHeight, restored.LineHeight);
        Assert.Equal(original.SdfPixelRange, restored.SdfPixelRange);
        Assert.Equal(original.EmPixelSize, restored.EmPixelSize);
        Assert.Equal(original.WhiteTexelUv, restored.WhiteTexelUv);
    }

    [Fact]
    public void Write_IsDeterministic_SameAssetSameBytes()
    {
        // The whole point of a binary container: "the cooker is reproducible" becomes a byte comparison.
        Assert.Equal(Serialize(MakeFont()), Serialize(MakeFont()));
    }

    [Fact]
    public void RoundTrip_SurvivesEmptyKernTable()
    {
        // The expected case for a monospace face: no legacy `kern` table at all. The lookup must still work and
        // simply return zero, so layout code needs no special case.
        var font = MakeFont();
        var noKern = new FontAsset(
            font.AtlasPixels, font.AtlasWidth, font.AtlasHeight, font.Glyphs, [],
            font.Ascender, font.Descender, font.LineHeight, font.SdfPixelRange, font.EmPixelSize, font.WhiteTexelUv);

        var restored = FontAssetFormat.Read(Serialize(noKern));

        Assert.Empty(restored.KernPairs);
        Assert.Equal(0f, restored.GetKerning(65, 86));
    }

    // ---- Lookups ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(65u)]
    [InlineData(86u)]
    [InlineData(97u)]
    public void TryGetGlyph_FindsEveryPresentCodepoint(uint codepoint)
    {
        Assert.True(MakeFont().TryGetGlyph(codepoint, out var glyph));
        Assert.Equal(codepoint, glyph.Codepoint);
    }

    [Theory]
    [InlineData(64u)] // just below the first
    [InlineData(70u)] // between two
    [InlineData(200u)] // past the last
    public void TryGetGlyph_ReturnsFalseForMissingCodepoint(uint codepoint)
    {
        // Missing glyphs are a CONTENT condition, never an exception: a font that lacks 'é' must not take down a
        // frame mid-draw.
        Assert.False(MakeFont().TryGetGlyph(codepoint, out _));
    }

    [Fact]
    public void GetKerning_ReturnsPairAmount_AndIsDirectional()
    {
        var font = MakeFont();

        Assert.Equal(-0.08f, font.GetKerning(65, 86));
        Assert.Equal(-0.06f, font.GetKerning(86, 65)); // the reverse pair is a DIFFERENT adjustment
        Assert.Equal(0f, font.GetKerning(65, 97)); // unkerned pair
        Assert.Equal(0f, font.GetKerning(1000, 1001)); // neither codepoint present
    }

    [Fact]
    public void CoverageSentinel_FlagsANonSdfAtlas()
    {
        var font = MakeFont();
        Assert.False(font.IsCoverageAtlas); // sdfPixelRange = 4

        var coverage = new FontAsset(
            font.AtlasPixels, font.AtlasWidth, font.AtlasHeight, font.Glyphs, font.KernPairs,
            font.Ascender, font.Descender, font.LineHeight,
            sdfPixelRange: FontAsset.CoverageAtlasSentinel, font.EmPixelSize, font.WhiteTexelUv);

        Assert.True(coverage.IsCoverageAtlas);
    }

    // ---- Robustness -------------------------------------------------------------------------------------------

    [Fact]
    public void Read_RejectsBadMagic()
    {
        var bytes = Serialize(MakeFont());
        bytes[1] = (byte)'X';

        var ex = Assert.Throws<FontAssetException>(() => FontAssetFormat.Read(bytes));
        Assert.Contains("magic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RejectsUnsupportedVersion()
    {
        var bytes = Serialize(MakeFont());
        bytes[4] = 99; // version is the u32 right after the 4-byte magic

        var ex = Assert.Throws<FontAssetException>(() => FontAssetFormat.Read(bytes));
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RejectsTruncatedHeader()
    {
        Assert.Throws<FontAssetException>(() => FontAssetFormat.Read(Serialize(MakeFont())[..10]));
    }

    [Fact]
    public void Read_RejectsTruncatedPayload()
    {
        // Header intact, body cut short — the length check must catch it before any slice reads out of bounds.
        var bytes = Serialize(MakeFont());
        Assert.Throws<FontAssetException>(() => FontAssetFormat.Read(bytes[..(bytes.Length - 3)]));
    }

    [Fact]
    public void Read_RejectsInconsistentCounts()
    {
        // Claim a huge glyph count: the expected-length check must reject it rather than trying to allocate.
        // Header layout: magic(4) + version(4) + atlasW(4) + atlasH(4) + 5 floats(20) + whiteTexelUv(16) = 52.
        const int glyphCountOffset = 52;
        var bytes = Serialize(MakeFont());
        BitConverter.TryWriteBytes(bytes.AsSpan(glyphCountOffset, 4), 100000u);

        var ex = Assert.Throws<FontAssetException>(() => FontAssetFormat.Read(bytes));
        Assert.Contains("does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RejectsUnsortedGlyphs()
    {
        // Unsorted glyphs would make the binary search silently return the WRONG glyph — far worse than a throw.
        var font = MakeFont();
        var scrambled = new[] { font.Glyphs[2], font.Glyphs[0], font.Glyphs[1] };
        var bad = new FontAsset(
            font.AtlasPixels, font.AtlasWidth, font.AtlasHeight, scrambled, font.KernPairs,
            font.Ascender, font.Descender, font.LineHeight, font.SdfPixelRange, font.EmPixelSize, font.WhiteTexelUv);

        var ex = Assert.Throws<FontAssetException>(() => FontAssetFormat.Read(Serialize(bad)));
        Assert.Contains("sorted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
