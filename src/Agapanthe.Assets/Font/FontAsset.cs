using System.Numerics;

namespace Agapanthe.Assets.Font;

/// <summary>
/// One glyph's placement data (UI-1). A blittable value type: the whole array is written to and read from
/// <c>.agfont</c> in a single bulk copy, exactly like the VS-1 world snapshot.
/// <para>
/// Coordinates are split by space. <see cref="AtlasUv"/> is normalised texture space (x0, y0, x1, y1) — where the
/// glyph lives in the atlas. <see cref="PlaneMin"/>/<see cref="PlaneMax"/> are in <b>em units</b> relative to the
/// pen position on the baseline, so a caller scales them by the desired pixel size; that is what lets one baked
/// atlas serve every size. <see cref="Advance"/> is likewise in em units.
/// </para>
/// </summary>
public readonly record struct GlyphMetrics(
    uint Codepoint,
    Vector4 AtlasUv,
    Vector2 PlaneMin,
    Vector2 PlaneMax,
    float Advance);

/// <summary>
/// A kerning adjustment between an ordered pair of codepoints, in em units (UI-1). Stored as a flat array sorted by
/// <c>(First, Second)</c> and looked up by binary search — not a <c>Dictionary</c>, which would allocate and hash on
/// a path that runs per glyph pair during layout.
/// </summary>
public readonly record struct KernPair(uint First, uint Second, float Amount);

/// <summary>
/// A baked font: a single-channel SDF atlas plus the metrics needed to lay text out (UI-1).
/// <para>
/// <b>Produced offline</b> by <c>tools/FontCooker</c> and consumed here — the runtime never parses a <c>.ttf</c> and
/// never links a font library. This mirrors the shader pipeline, where Release forbids runtime compilation and the
/// shaderc native library is stripped from the output entirely.
/// </para>
/// <para>
/// The atlas is <b>SDF</b>, so it is data rather than colour: it must be uploaded as a linear single-channel format
/// (<c>R8Unorm</c>) with <b>no mips</b> — mipping would blur the distance field into mush — and sampled linearly,
/// since that interpolation is precisely what antialiases the glyph edge.
/// </para>
/// </summary>
public sealed class FontAsset
{
    /// <summary>Sentinel <see cref="SdfPixelRange"/> meaning "this atlas holds plain coverage, not a distance
    /// field". Reserved by UI-1 so the documented bitmap fallback stays a cooker flag plus a shader branch, rather
    /// than a new format.</summary>
    public const float CoverageAtlasSentinel = 0f;

    public FontAsset(
        byte[] atlasPixels,
        int atlasWidth,
        int atlasHeight,
        GlyphMetrics[] glyphs,
        KernPair[] kernPairs,
        float ascender,
        float descender,
        float lineHeight,
        float sdfPixelRange,
        float emPixelSize,
        Vector4 whiteTexelUv)
    {
        AtlasPixels = atlasPixels;
        AtlasWidth = atlasWidth;
        AtlasHeight = atlasHeight;
        Glyphs = glyphs;
        KernPairs = kernPairs;
        Ascender = ascender;
        Descender = descender;
        LineHeight = lineHeight;
        SdfPixelRange = sdfPixelRange;
        EmPixelSize = emPixelSize;
        WhiteTexelUv = whiteTexelUv;
    }

    /// <summary>Single-channel (R8) atlas, tightly packed, <see cref="AtlasWidth"/> × <see cref="AtlasHeight"/> bytes.</summary>
    public byte[] AtlasPixels { get; }

    public int AtlasWidth { get; }

    public int AtlasHeight { get; }

    /// <summary>Glyphs, <b>sorted by <see cref="GlyphMetrics.Codepoint"/> ascending</b> — the order the cooker writes
    /// and the invariant <see cref="TryGetGlyph"/>'s binary search depends on.</summary>
    public GlyphMetrics[] Glyphs { get; }

    /// <summary>Kerning pairs sorted by <c>(First, Second)</c>. Empty when the source face carries no legacy
    /// <c>kern</c> table — the common case for modern libre fonts, which ship GPOS instead (see the milestone notes:
    /// v1 keeps the lookup and the seam, and real GPOS kerning arrives with shaping).</summary>
    public KernPair[] KernPairs { get; }

    /// <summary>Distance from the baseline to the top of the ascenders, in em units (positive, up).</summary>
    public float Ascender { get; }

    /// <summary>Distance from the baseline to the bottom of the descenders, in em units (negative, down).</summary>
    public float Descender { get; }

    /// <summary>Baseline-to-baseline distance in em units.</summary>
    public float LineHeight { get; }

    /// <summary>Width of the signed-distance ramp, in atlas texels. The fragment shader needs it to convert the
    /// sampled distance into a screen-space coverage. <see cref="CoverageAtlasSentinel"/> means plain coverage.</summary>
    public float SdfPixelRange { get; }

    /// <summary>The em size the atlas was rasterised at, in pixels. Diagnostic: the metrics are already normalised
    /// to em units, so rendering never divides by this.</summary>
    public float EmPixelSize { get; }

    /// <summary>UV rect of a fully-opaque texel reserved in the atlas, so solid rectangles (panel backgrounds,
    /// profiler bars) draw through the same atlas, the same pipeline and the same draw call as text.</summary>
    public Vector4 WhiteTexelUv { get; }

    /// <summary>True when the atlas holds coverage rather than a distance field (the documented fallback).</summary>
    public bool IsCoverageAtlas => SdfPixelRange == CoverageAtlasSentinel;

    /// <summary>
    /// Finds a glyph by codepoint. Binary search over the codepoint-sorted array — allocation-free, and returns
    /// <see langword="false"/> for a codepoint the atlas does not carry rather than throwing: a missing glyph is a
    /// content condition that must never take down a frame mid-draw.
    /// </summary>
    public bool TryGetGlyph(uint codepoint, out GlyphMetrics glyph)
    {
        var glyphs = Glyphs;
        var lo = 0;
        var hi = glyphs.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var candidate = glyphs[mid].Codepoint;
            if (candidate == codepoint)
            {
                glyph = glyphs[mid];
                return true;
            }

            if (candidate < codepoint)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        glyph = default;
        return false;
    }

    /// <summary>
    /// The kerning adjustment for an ordered pair, in em units, or <c>0</c> when the pair is not kerned. Binary
    /// search over the sorted pair array; allocation-free, and trivially <c>0</c> when the font has no kern table.
    /// </summary>
    public float GetKerning(uint first, uint second)
    {
        var pairs = KernPairs;
        var lo = 0;
        var hi = pairs.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var pair = pairs[mid];
            if (pair.First == first && pair.Second == second)
            {
                return pair.Amount;
            }

            // Ordered by (First, Second), so compare the composite key the same way the cooker sorted it.
            var before = pair.First < first || (pair.First == first && pair.Second < second);
            if (before)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return 0f;
    }
}
