using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("FontCooker")]
[assembly: InternalsVisibleTo("Agapanthe.Tests")]

namespace Agapanthe.Assets.Font;

/// <summary>
/// The <c>.agfont</c> container: a header plus blittable metric arrays plus the raw atlas payload (UI-1).
/// <para>
/// <b>Why binary and not JSON.</b> Same reasoning as the VS-1 world snapshot: the metrics are already blittable
/// structs, so a bulk copy needs neither reflection nor a source generator, it is AOT-safe by construction, and —
/// the property that matters most here — the output is <b>deterministic</b>, which turns "the cooker is
/// reproducible" into a one-line byte-comparison test. Little-endian is assumed (every target is LE) and the
/// scalars are written explicitly LE so a big-endian port would fail loudly at the magic rather than silently.
/// </para>
/// <para>
/// Layout:
/// <code>
/// magic "AGFT" (4) | version u32 | atlasWidth u32 | atlasHeight u32
/// ascender f32 | descender f32 | lineHeight f32 | sdfPixelRange f32 | emPixelSize f32
/// whiteTexelUv f32*4
/// glyphCount u32 | kernCount u32
/// GlyphMetrics[glyphCount]  (blittable, codepoint-ascending)
/// KernPair[kernCount]       (blittable, sorted by (First, Second))
/// atlas bytes[atlasWidth * atlasHeight]  (R8, tightly packed)
/// </code>
/// </para>
/// <para>
/// The <b>writer is internal</b> — only <c>tools/FontCooker</c> (and the round-trip tests) may produce a font;
/// applications only ever read one. The <b>reader is public</b>: it is the runtime path.
/// </para>
/// </summary>
public static class FontAssetFormat
{
    private static ReadOnlySpan<byte> Magic => "AGFT"u8;

    /// <summary>Container version. Bump when the layout changes; <see cref="Read"/> refuses anything else outright
    /// rather than guessing, exactly like the VS-1 snapshot header.</summary>
    public const uint Version = 1;

    // Header is fixed-size, so a truncated file is caught before any array is sized from its counts.
    private const int HeaderBytes =
        4 // magic
        + 4 // version
        + 4 + 4 // atlas w/h
        + (4 * 5) // ascender, descender, lineHeight, sdfPixelRange, emPixelSize
        + (4 * 4) // whiteTexelUv
        + 4 + 4; // glyphCount, kernCount

    /// <summary>
    /// Reads a font from a <c>.agfont</c> byte blob. Every structural expectation is checked before it is trusted:
    /// a corrupt file throws <see cref="FontAssetException"/> with a reason, never returns a half-built asset and
    /// never reads out of bounds.
    /// </summary>
    public static FontAsset Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes)
        {
            throw new FontAssetException(
                $"Font file is truncated: {bytes.Length} bytes, need at least {HeaderBytes} for the header.");
        }

        if (!bytes[..4].SequenceEqual(Magic))
        {
            throw new FontAssetException("Not a .agfont file (bad magic; expected 'AGFT').");
        }

        var offset = 4;
        var version = ReadU32(bytes, ref offset);
        if (version != Version)
        {
            throw new FontAssetException(
                $"Unsupported .agfont version {version} (this build reads version {Version}).");
        }

        var atlasWidth = (int)ReadU32(bytes, ref offset);
        var atlasHeight = (int)ReadU32(bytes, ref offset);
        var ascender = ReadF32(bytes, ref offset);
        var descender = ReadF32(bytes, ref offset);
        var lineHeight = ReadF32(bytes, ref offset);
        var sdfPixelRange = ReadF32(bytes, ref offset);
        var emPixelSize = ReadF32(bytes, ref offset);
        var whiteTexelUv = new Vector4(
            ReadF32(bytes, ref offset),
            ReadF32(bytes, ref offset),
            ReadF32(bytes, ref offset),
            ReadF32(bytes, ref offset));
        var glyphCount = ReadU32(bytes, ref offset);
        var kernCount = ReadU32(bytes, ref offset);

        if (atlasWidth <= 0 || atlasHeight <= 0)
        {
            throw new FontAssetException($"Font atlas has non-positive dimensions {atlasWidth}×{atlasHeight}.");
        }

        // Size everything from the counts, then require the file to be EXACTLY that long. Checking the total up
        // front means a hostile or truncated file cannot make us allocate a huge array on a bogus count.
        var glyphBytes = (long)glyphCount * Unsafe.SizeOf<GlyphMetrics>();
        var kernBytes = (long)kernCount * Unsafe.SizeOf<KernPair>();
        var atlasBytes = (long)atlasWidth * atlasHeight;
        var expected = HeaderBytes + glyphBytes + kernBytes + atlasBytes;
        if (bytes.Length != expected)
        {
            throw new FontAssetException(
                $"Font file length {bytes.Length} does not match its header "
                + $"({glyphCount} glyphs, {kernCount} kern pairs, {atlasWidth}×{atlasHeight} atlas → expected {expected}).");
        }

        var glyphs = new GlyphMetrics[glyphCount];
        MemoryMarshal.Cast<byte, GlyphMetrics>(bytes.Slice(offset, (int)glyphBytes)).CopyTo(glyphs);
        offset += (int)glyphBytes;

        var kernPairs = new KernPair[kernCount];
        MemoryMarshal.Cast<byte, KernPair>(bytes.Slice(offset, (int)kernBytes)).CopyTo(kernPairs);
        offset += (int)kernBytes;

        var atlas = bytes.Slice(offset, (int)atlasBytes).ToArray();

        // The reader's own invariant: TryGetGlyph/GetKerning binary-search these arrays, so an unsorted file would
        // silently return wrong glyphs instead of failing. Cheap to verify, impossible to debug otherwise.
        for (var i = 1; i < glyphs.Length; i++)
        {
            if (glyphs[i - 1].Codepoint >= glyphs[i].Codepoint)
            {
                throw new FontAssetException(
                    $"Font glyphs are not sorted by ascending codepoint (index {i}: "
                    + $"U+{glyphs[i - 1].Codepoint:X4} then U+{glyphs[i].Codepoint:X4}).");
            }
        }

        // Same invariant, same reason: GetKerning binary-searches this array by (First, Second), so an unsorted
        // file would silently return the WRONG adjustment rather than fail.
        for (var i = 1; i < kernPairs.Length; i++)
        {
            var previous = kernPairs[i - 1];
            var current = kernPairs[i];
            if (previous.First > current.First
                || (previous.First == current.First && previous.Second >= current.Second))
            {
                throw new FontAssetException(
                    $"Font kern pairs are not sorted by (First, Second) at index {i}.");
            }
        }

        // Non-finite metrics would propagate NaN through the layout and into the vertex buffer, where nothing
        // downstream checks. A degenerate face is a content error and must surface here, at the boundary.
        if (!float.IsFinite(ascender) || !float.IsFinite(descender) || !float.IsFinite(lineHeight)
            || !float.IsFinite(sdfPixelRange) || !float.IsFinite(emPixelSize) || lineHeight <= 0f)
        {
            throw new FontAssetException(
                $"Font metrics are not finite or non-positive (ascender {ascender}, descender {descender}, "
                + $"lineHeight {lineHeight}, sdfPixelRange {sdfPixelRange}, emPixelSize {emPixelSize}).");
        }

        return new FontAsset(
            atlas, atlasWidth, atlasHeight, glyphs, kernPairs,
            ascender, descender, lineHeight, sdfPixelRange, emPixelSize, whiteTexelUv);
    }

    /// <summary>
    /// Writes a font to the <c>.agfont</c> layout. <b>Internal</b>: producing fonts is the cooker's job, not an
    /// application's. Deterministic — the same asset always yields the same bytes, which is what the reproducibility
    /// test asserts.
    /// </summary>
    internal static void Write(Stream stream, FontAsset font)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(font);

        stream.Write(Magic);
        WriteU32(stream, Version);
        WriteU32(stream, (uint)font.AtlasWidth);
        WriteU32(stream, (uint)font.AtlasHeight);
        WriteF32(stream, font.Ascender);
        WriteF32(stream, font.Descender);
        WriteF32(stream, font.LineHeight);
        WriteF32(stream, font.SdfPixelRange);
        WriteF32(stream, font.EmPixelSize);
        WriteF32(stream, font.WhiteTexelUv.X);
        WriteF32(stream, font.WhiteTexelUv.Y);
        WriteF32(stream, font.WhiteTexelUv.Z);
        WriteF32(stream, font.WhiteTexelUv.W);
        WriteU32(stream, (uint)font.Glyphs.Length);
        WriteU32(stream, (uint)font.KernPairs.Length);

        stream.Write(MemoryMarshal.AsBytes<GlyphMetrics>(font.Glyphs));
        stream.Write(MemoryMarshal.AsBytes<KernPair>(font.KernPairs));
        stream.Write(font.AtlasPixels);
    }

    private static uint ReadU32(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static float ReadF32(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteF32(Stream stream, float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        stream.Write(buffer);
    }
}
