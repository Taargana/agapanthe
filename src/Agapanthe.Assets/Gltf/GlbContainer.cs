using System.Buffers.Binary;

namespace Agapanthe.Assets.Gltf;

/// <summary>
/// The two chunks carved out of a binary glTF (<c>.glb</c>) container. Both are zero-copy slices of
/// the original file bytes — no data is duplicated.
/// </summary>
internal readonly struct GlbChunks
{
    /// <summary>The mandatory JSON chunk payload (UTF-8, without the chunk header).</summary>
    public ReadOnlyMemory<byte> Json { get; init; }

    /// <summary>The optional BIN chunk payload; <see cref="ReadOnlyMemory{T}.IsEmpty"/> when absent.</summary>
    public ReadOnlyMemory<byte> Bin { get; init; }

    /// <summary>True when a BIN chunk was present (distinguishes "present but empty" is not needed here).</summary>
    public bool HasBin { get; init; }
}

/// <summary>
/// Strict parser for the GLB (binary glTF) container: 12-byte header (magic, version, length)
/// followed by length-prefixed, type-tagged chunks. Every structural violation surfaces as an
/// <see cref="AssetException"/> carrying the source path and a precise reason (spec §4 — no silent
/// fallback).
/// </summary>
internal static class GlbContainer
{
    /// <summary>Header magic: ASCII "glTF" read as a little-endian uint32.</summary>
    public const uint Magic = 0x46546C67;

    /// <summary>Chunk type "JSON" (little-endian uint32).</summary>
    private const uint ChunkTypeJson = 0x4E4F534A;

    /// <summary>Chunk type "BIN\0" (little-endian uint32).</summary>
    private const uint ChunkTypeBin = 0x004E4942;

    private const int HeaderSize = 12;
    private const int ChunkHeaderSize = 8;

    /// <summary>
    /// True when <paramref name="data"/> begins with the GLB magic. Used to detect <c>.glb</c> by
    /// content rather than extension (a header this cheap is safe to peek before committing to a path).
    /// </summary>
    public static bool LooksLikeGlb(ReadOnlySpan<byte> data)
        => data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    /// <summary>
    /// Parses and validates the GLB container, returning zero-copy slices of the JSON and (optional)
    /// BIN chunks. Throws <see cref="AssetException"/> on any structural error.
    /// </summary>
    public static GlbChunks Parse(ReadOnlyMemory<byte> data, string sourcePath)
    {
        ReadOnlySpan<byte> span = data.Span;

        if (span.Length < HeaderSize)
        {
            throw new AssetException(sourcePath, $"GLB too small: {span.Length} bytes, need at least {HeaderSize} for the header.");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (magic != Magic)
        {
            throw new AssetException(sourcePath, $"invalid GLB magic 0x{magic:X8} (expected 0x{Magic:X8} 'glTF').");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        if (version != 2)
        {
            throw new AssetException(sourcePath, $"unsupported GLB container version {version} (only version 2 is supported).");
        }

        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        if (declaredLength > (uint)span.Length)
        {
            throw new AssetException(sourcePath, $"GLB truncated: header declares {declaredLength} bytes but the file is only {span.Length}.");
        }

        // Scan chunks within the declared length. Chunk 0 must be JSON; an optional chunk 1 is BIN;
        // any further chunks (vendor extensions) are ignored per the spec.
        int total = (int)declaredLength;
        int offset = HeaderSize;

        ReadOnlyMemory<byte> json = default;
        bool haveJson = false;
        ReadOnlyMemory<byte> bin = default;
        bool haveBin = false;
        int chunkIndex = 0;

        while (offset + ChunkHeaderSize <= total)
        {
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 4)..]);
            int dataStart = offset + ChunkHeaderSize;

            long dataEnd = (long)dataStart + chunkLength;
            if (dataEnd > total)
            {
                throw new AssetException(sourcePath,
                    $"GLB chunk {chunkIndex} (type 0x{chunkType:X8}) is truncated: declares {chunkLength} bytes at offset {dataStart}, past the container end {total}.");
            }

            ReadOnlyMemory<byte> payload = data.Slice(dataStart, (int)chunkLength);

            if (chunkIndex == 0)
            {
                if (chunkType != ChunkTypeJson)
                {
                    throw new AssetException(sourcePath, $"GLB first chunk must be JSON (0x{ChunkTypeJson:X8}) but is 0x{chunkType:X8}.");
                }

                json = payload;
                haveJson = true;
            }
            else if (chunkType == ChunkTypeBin && !haveBin)
            {
                bin = payload;
                haveBin = true;
            }
            // else: unknown/duplicate chunk type — skipped per spec.

            offset = dataStart + (int)chunkLength;
            chunkIndex++;
        }

        if (!haveJson)
        {
            throw new AssetException(sourcePath, "GLB has no JSON chunk.");
        }

        return new GlbChunks { Json = json, Bin = bin, HasBin = haveBin };
    }
}
