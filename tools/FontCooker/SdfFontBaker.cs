using System.Numerics;
using Agapanthe.Assets.Font;
using StbTrueTypeSharp;

namespace FontCooker;

/// <summary>
/// Rasterises a TrueType face into a single-channel SDF atlas plus metrics (UI-1).
/// <para>
/// <b>Deterministic by construction</b>: the charset is walked in ascending codepoint order, glyphs are packed in a
/// total order (height descending, codepoint ascending to break ties), and nothing consults a clock, a hash seed or
/// the file system. Running the cooker twice on the same input therefore yields byte-identical output — which is
/// exactly what the reproducibility gate asserts.
/// </para>
/// </summary>
internal static class SdfFontBaker
{
    /// <summary>Em size the atlas is rasterised at. High enough that the field survives being scaled down to the
    /// ~13 px a debug overlay uses; the metrics are normalised to em units, so nothing else depends on it.</summary>
    public const float EmPixelSize = 64f;

    /// <summary>
    /// Width of the signed-distance ramp, in atlas texels.
    /// <para>
    /// <b>Why 8 and not 4.</b> The stored field saturates at <c>|d| = 128/255 ≈ 0.5</c>, reached one spread away
    /// from the edge. The fragment shader antialiases with <c>fwidth</c>, which at render size <c>P</c> is roughly
    /// <c>(EmPixelSize / P) × (128/spread) / 255</c> texels-per-pixel. Once that exceeds the stored amplitude the
    /// smoothstep can no longer reach 0 or 1: glyph cores stop short of full opacity and — far more visible — the
    /// empty part of the padded quad never reaches zero, painting a faint rectangle around every glyph.
    /// With spread 4 that threshold sat at ~16 px, so the 11 px debug line was visibly washed out and boxed.
    /// Spread 8 halves the slope and pushes the threshold to ~8 px, below anything the engine draws.
    /// </para>
    /// </summary>
    public const int SdfSpread = 8;

    /// <summary>Empty texels kept around each glyph so neighbouring distance fields cannot bleed into each other.</summary>
    public const int GlyphPadding = SdfSpread + 1;

    /// <summary>The value a texel exactly on the glyph edge gets. 128 puts the edge at the midpoint of the byte
    /// range, giving equal room to encode "inside" and "outside".</summary>
    private const byte OnEdgeValue = 128;

    /// <summary>Side of the opaque square reserved at the atlas origin so solid rectangles can share this atlas.</summary>
    private const int WhiteTexelSize = 2;

    private const int MinAtlasSize = 64;
    private const int MaxAtlasSize = 2048;

    public static unsafe FontAsset Bake(byte[] ttf, IReadOnlyList<uint> codepoints)
    {
        var font = new StbTrueType.stbtt_fontinfo();
        fixed (byte* ttfPtr = ttf)
        {
            var offset = StbTrueType.stbtt_GetFontOffsetForIndex(ttfPtr, 0);
            if (offset < 0 || StbTrueType.stbtt_InitFont(font, ttfPtr, offset) == 0)
            {
                throw new InvalidOperationException("Not a usable TrueType font (stbtt_InitFont rejected it).");
            }

            // ScaleForPixelHeight maps the ascent-to-descent box onto EmPixelSize, so "pixel size" downstream means
            // that box's height — close enough to what a user calls a font size, and self-consistent because every
            // metric below is divided by the same EmPixelSize to reach em units.
            var scale = StbTrueType.stbtt_ScaleForPixelHeight(font, EmPixelSize);
            if (!float.IsFinite(scale) || scale <= 0f)
            {
                // ScaleForPixelHeight divides by (ascent − descent); a degenerate face makes that zero and every
                // metric downstream becomes Inf/NaN, which would be written straight into the .agfont. Fail here.
                throw new InvalidOperationException(
                    $"Font has degenerate vertical metrics (computed scale {scale}); cannot rasterise it.");
            }

            int ascentUnits, descentUnits, lineGapUnits;
            StbTrueType.stbtt_GetFontVMetrics(font, &ascentUnits, &descentUnits, &lineGapUnits);
            var ascender = ascentUnits * scale / EmPixelSize;
            var descender = descentUnits * scale / EmPixelSize; // negative: below the baseline
            var lineHeight = (ascentUnits - descentUnits + lineGapUnits) * scale / EmPixelSize;

            var baked = RasterizeGlyphs(font, scale, codepoints);
            try
            {
                var atlasSize = ChooseAtlasSize(baked);
                var atlas = new byte[atlasSize * atlasSize];
                var glyphs = PackAndBlit(baked, atlas, atlasSize);

                // Reserved opaque block at the origin, written AFTER packing (packing already skipped this area).
                for (var y = 0; y < WhiteTexelSize; y++)
                {
                    for (var x = 0; x < WhiteTexelSize; x++)
                    {
                        atlas[(y * atlasSize) + x] = 255;
                    }
                }

                // Sample the CENTRE of the reserved block, so bilinear filtering can never pick up a neighbour.
                var whiteUv = new Vector4(
                    0.5f / atlasSize, 0.5f / atlasSize,
                    (WhiteTexelSize - 0.5f) / atlasSize, (WhiteTexelSize - 0.5f) / atlasSize);

                return new FontAsset(
                    atlas, atlasSize, atlasSize, glyphs, ExtractKerning(font, scale, codepoints),
                    ascender, descender, lineHeight,
                    sdfPixelRange: SdfSpread, emPixelSize: EmPixelSize, whiteTexelUv: whiteUv);
            }
            finally
            {
                FreeBitmaps(baked);
            }
        }
    }

    // One rasterised glyph, still owning its unmanaged SDF bitmap.
    private sealed unsafe class BakedGlyph
    {
        public uint Codepoint;
        public byte* Bitmap;
        public int Width;
        public int Height;
        public int OffsetX; // pen-relative, pixels, x right
        public int OffsetY; // pen-relative, pixels, y DOWN from the baseline (stb convention)
        public float Advance; // em units
        public int PackedX;
        public int PackedY;
    }

    private static unsafe List<BakedGlyph> RasterizeGlyphs(
        StbTrueType.stbtt_fontinfo font, float scale, IReadOnlyList<uint> codepoints)
    {
        var baked = new List<BakedGlyph>(codepoints.Count);
        try
        {
            RasterizeInto(baked, font, scale, codepoints);
        }
        catch
        {
            // Bake's try/finally only starts once this method RETURNS, so an exception mid-loop would strand every
            // bitmap allocated so far. The process is about to die either way, but "every native handle has a
            // deterministic destruction path" is a project rule, not a best effort.
            FreeBitmaps(baked);
            throw;
        }

        return baked;
    }

    private static unsafe void RasterizeInto(
        List<BakedGlyph> baked, StbTrueType.stbtt_fontinfo font, float scale, IReadOnlyList<uint> codepoints)
    {
        foreach (var codepoint in codepoints) // caller guarantees ascending order
        {
            if (StbTrueType.stbtt_FindGlyphIndex(font, (int)codepoint) == 0)
            {
                continue; // the face does not carry this codepoint — skip it rather than baking a blank
            }

            int advanceUnits, leftSideBearing;
            StbTrueType.stbtt_GetCodepointHMetrics(font, (int)codepoint, &advanceUnits, &leftSideBearing);

            int width, height, offsetX, offsetY;
            var bitmap = StbTrueType.stbtt_GetCodepointSDF(
                font, scale, (int)codepoint, GlyphPadding, OnEdgeValue, OnEdgeValue / (float)SdfSpread,
                &width, &height, &offsetX, &offsetY);

            // A whitespace glyph has no outline: stb returns null. It still advances the pen, so it is kept with a
            // zero-area box — the layout emits no quad for it.
            baked.Add(new BakedGlyph
            {
                Codepoint = codepoint,
                Bitmap = bitmap,
                Width = bitmap != null ? width : 0,
                Height = bitmap != null ? height : 0,
                OffsetX = bitmap != null ? offsetX : 0,
                OffsetY = bitmap != null ? offsetY : 0,
                Advance = advanceUnits * scale / EmPixelSize,
            });
        }
    }

    // Releases every SDF bitmap stb allocated. Safe to call twice: the pointers are not nulled, but the list is
    // only ever freed once (either by the rasterize failure path or by Bake's finally).
    private static unsafe void FreeBitmaps(List<BakedGlyph> baked)
    {
        foreach (var glyph in baked)
        {
            if (glyph.Bitmap != null)
            {
                StbTrueType.stbtt_FreeSDF(glyph.Bitmap, null);
            }
        }
    }

    // Smallest power-of-two square that fits every glyph, found by trying sizes rather than estimating: shelf
    // packing's real efficiency is hard to predict, and a wrong guess would either waste memory or fail late.
    private static int ChooseAtlasSize(List<BakedGlyph> baked)
    {
        for (var size = MinAtlasSize; size <= MaxAtlasSize; size *= 2)
        {
            if (TryPack(baked, size, blit: false, atlas: null))
            {
                return size;
            }
        }

        throw new InvalidOperationException(
            $"Charset does not fit in a {MaxAtlasSize}×{MaxAtlasSize} atlas at em {EmPixelSize} px. "
            + "Reduce the charset or lower the em size — a silently truncated atlas would be far worse.");
    }

    private static unsafe GlyphMetrics[] PackAndBlit(List<BakedGlyph> baked, byte[] atlas, int atlasSize)
    {
        if (!TryPack(baked, atlasSize, blit: true, atlas))
        {
            throw new InvalidOperationException("Atlas packing failed after its size was already validated.");
        }

        // Metrics come out in ASCENDING CODEPOINT order — the invariant FontAsset's binary search relies on, and
        // one the reader re-verifies.
        var ordered = baked.OrderBy(g => g.Codepoint).ToList();
        var glyphs = new GlyphMetrics[ordered.Count];
        for (var i = 0; i < ordered.Count; i++)
        {
            var g = ordered[i];
            Vector4 uv;
            Vector2 planeMin, planeMax;
            if (g.Width == 0 || g.Height == 0)
            {
                uv = Vector4.Zero;
                planeMin = Vector2.Zero;
                planeMax = Vector2.Zero; // zero-area: advances the pen, emits no quad
            }
            else
            {
                uv = new Vector4(
                    g.PackedX / (float)atlasSize,
                    g.PackedY / (float)atlasSize,
                    (g.PackedX + g.Width) / (float)atlasSize,
                    (g.PackedY + g.Height) / (float)atlasSize);

                // stb gives pen-relative offsets with Y pointing DOWN; the plane is Y-UP, hence the sign flip.
                planeMin = new Vector2(g.OffsetX / EmPixelSize, -(g.OffsetY + g.Height) / EmPixelSize);
                planeMax = new Vector2((g.OffsetX + g.Width) / EmPixelSize, -g.OffsetY / EmPixelSize);
            }

            glyphs[i] = new GlyphMetrics(g.Codepoint, uv, planeMin, planeMax, g.Advance);
        }

        return glyphs;
    }

    // Shelf packing: rows of decreasing height. Simple, deterministic and more than good enough for a couple of
    // hundred glyphs — a skyline packer would buy a few percent of area for a lot more code.
    private static unsafe bool TryPack(List<BakedGlyph> baked, int atlasSize, bool blit, byte[]? atlas)
    {
        // Total order: tallest first, codepoint ascending to break ties, so the layout never depends on List order.
        var order = baked
            .Where(g => g.Width > 0 && g.Height > 0)
            .OrderByDescending(g => g.Height)
            .ThenBy(g => g.Codepoint)
            .ToList();

        // Start the first shelf clear of the reserved white block at the origin.
        var penX = WhiteTexelSize + 1;
        var penY = 0;
        var shelfHeight = WhiteTexelSize + 1;

        foreach (var g in order)
        {
            if (g.Width > atlasSize || g.Height > atlasSize)
            {
                return false;
            }

            if (penX + g.Width > atlasSize)
            {
                penX = 0; // new shelf
                penY += shelfHeight;
                shelfHeight = 0;
            }

            if (penY + g.Height > atlasSize)
            {
                return false;
            }

            g.PackedX = penX;
            g.PackedY = penY;

            if (blit && atlas != null)
            {
                for (var row = 0; row < g.Height; row++)
                {
                    var source = new ReadOnlySpan<byte>(g.Bitmap + (row * g.Width), g.Width);
                    source.CopyTo(atlas.AsSpan(((penY + row) * atlasSize) + penX, g.Width));
                }
            }

            penX += g.Width;
            if (g.Height > shelfHeight)
            {
                shelfHeight = g.Height;
            }
        }

        return true;
    }

    /// <summary>
    /// Extracts pair kerning from the face's <b>legacy <c>kern</c> table</b>, restricted to the baked charset.
    /// <para>
    /// Most modern libre faces — including monospace ones, which have no kerning to begin with — ship GPOS instead
    /// and will yield an empty table here. That is expected and fine: the format, the lookup and the layout seam all
    /// stay in place, so a face that <i>does</i> carry a kern table gets kerning for free, and real GPOS support
    /// arrives later with shaping.
    /// </para>
    /// </summary>
    private static unsafe KernPair[] ExtractKerning(
        StbTrueType.stbtt_fontinfo font, float scale, IReadOnlyList<uint> codepoints)
    {
        var length = StbTrueType.stbtt_GetKerningTableLength(font);
        if (length <= 0)
        {
            return [];
        }

        // The table keys on GLYPH indices; the runtime keys on codepoints. Build the reverse map for our charset.
        var glyphToCodepoint = new Dictionary<int, uint>(codepoints.Count);
        foreach (var codepoint in codepoints)
        {
            var glyph = StbTrueType.stbtt_FindGlyphIndex(font, (int)codepoint);
            if (glyph != 0)
            {
                glyphToCodepoint.TryAdd(glyph, codepoint);
            }
        }

        var entries = new StbTrueType.stbtt_kerningentry[length];
        int written;
        fixed (StbTrueType.stbtt_kerningentry* entriesPtr = entries)
        {
            written = StbTrueType.stbtt_GetKerningTable(font, entriesPtr, length);
        }

        var pairs = new List<KernPair>(written);
        for (var i = 0; i < written; i++)
        {
            var entry = entries[i];
            if (entry.advance == 0
                || !glyphToCodepoint.TryGetValue(entry.glyph1, out var first)
                || !glyphToCodepoint.TryGetValue(entry.glyph2, out var second))
            {
                continue;
            }

            pairs.Add(new KernPair(first, second, entry.advance * scale / EmPixelSize));
        }

        // Sorted by (First, Second) — the order FontAsset.GetKerning binary-searches.
        pairs.Sort(static (a, b) => a.First != b.First
            ? a.First.CompareTo(b.First)
            : a.Second.CompareTo(b.Second));
        return [.. pairs];
    }
}
