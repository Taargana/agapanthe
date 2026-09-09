using System.Buffers.Binary;
using Agapanthe.Core;

namespace Agapanthe.Assets;

/// <summary>What kind of cooked asset a manifest entry points at. Only <see cref="Model"/> is produced today;
/// the others are reserved so a forward-compatible manifest still lists its models (Contenu-2b).</summary>
public enum AssetKind : byte
{
    Model = 0,
    Environment = 1,
    Font = 2,
}

/// <summary>One row of the content manifest: an <see cref="AssetKey"/>, what it is, where its cooked blob sits
/// (relative to the content root), and the blob's SHA-256 (written + shipped for a future
/// <c>VerifyContentHashes</c>; the runtime does not read it yet — a cheap diff key meanwhile).
/// <para>NB: the generated record-struct equality compares <see cref="ContentHash"/> <b>by reference</b>. Entries
/// are only ever dictionary <i>values</i> (keyed by <see cref="Key"/>), never compared — do not start.</para></summary>
public readonly record struct ManifestEntry(AssetKey Key, AssetKind Kind, string BlobPath, byte[] ContentHash);

/// <summary>
/// The <c>content.agmanifest</c> index (Contenu-2): every cooked asset, keyed by <see cref="AssetKey"/>. Binary,
/// deterministic (entries sorted ordinal by key), versioned — same stance as <c>.agmodel</c> / <c>.agfont</c> /
/// the VS-1 snapshot. Read at runtime by <see cref="AssetCatalog"/>; written by <c>Agapanthe.Assets.Pipeline</c>.
/// <para>
/// Layout: <c>magic "AGMN" (4) | version u32 | entryCount u32</c>, then per entry
/// <c>keyLen u16 + key UTF-8 | kind u8 | blobPathLen u16 + blob path UTF-8 | contentHash byte[32]</c>.
/// The cooker's build state (source hashes, cooker version) lives in a separate, non-shipped <c>.cookstate</c>
/// sidecar — the runtime manifest never carries a source hash.
/// </para>
/// </summary>
public static class ContentManifest
{
    private static ReadOnlySpan<byte> Magic => "AGMN"u8;

    /// <summary>Manifest version. Bump on a layout change; <see cref="Read"/> refuses anything else.</summary>
    public const uint Version = 1;

    /// <summary>SHA-256 width.</summary>
    public const int ContentHashBytes = 32;

    private const int HeaderBytes = 4 + 4 + 4; // magic | version | entryCount

    /// <summary>
    /// Reads a manifest. Every structural expectation is checked before it is trusted: a corrupt file throws
    /// <see cref="AssetException"/> with a reason, never returns a partial map and never reads out of bounds.
    /// Keys must be strictly ascending ordinal (a forged / reordered manifest is corruption).
    /// </summary>
    public static IReadOnlyDictionary<AssetKey, ManifestEntry> Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes)
        {
            throw new AssetException(
                $"content.agmanifest is truncated: {bytes.Length} bytes, need at least {HeaderBytes} for the header.");
        }

        if (!bytes[..4].SequenceEqual(Magic))
        {
            throw new AssetException("Not a content manifest (bad magic; expected 'AGMN').");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        if (version != Version)
        {
            throw new AssetException(
                $"Unsupported content manifest version {version} (this build reads version {Version}).");
        }

        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4));
        var map = new Dictionary<AssetKey, ManifestEntry>((int)Math.Min(entryCount, 1 << 16));
        var offset = HeaderBytes;
        var previousKey = (string?)null;

        for (var i = 0u; i < entryCount; i++)
        {
            var key = ReadKey(bytes, ref offset, i);
            var kind = (AssetKind)ReadByte(bytes, ref offset, i);
            var blobPathLen = ReadU16(bytes, ref offset, i);
            var blobPath = ReadUtf8(bytes, ref offset, blobPathLen, i);
            var hash = ReadHash(bytes, ref offset, i);

            // The blob path is spliced onto the content root at load (AssetCatalog) — validate it like the key,
            // so a forged manifest cannot make the runtime read outside the content tree (the spec's threat model).
            if (Path.IsPathRooted(blobPath)
                || blobPath.Replace('\\', '/').Split('/').Any(s => s is ".." or "."))
            {
                throw new AssetException($"content.agmanifest entry {i} has an unsafe blob path '{blobPath}'.");
            }

            if (previousKey is not null && string.CompareOrdinal(previousKey, key.Value) >= 0)
            {
                throw new AssetException(
                    $"content.agmanifest entries are not strictly ascending at index {i} ('{previousKey}' then '{key}').");
            }

            if (!map.TryAdd(key, new ManifestEntry(key, kind, blobPath, hash)))
            {
                throw new AssetException($"content.agmanifest has a duplicate key '{key}'.");
            }

            previousKey = key.Value;
        }

        if (offset != bytes.Length)
        {
            throw new AssetException(
                $"content.agmanifest has {bytes.Length - offset} trailing byte(s) after {entryCount} entries.");
        }

        return map;
    }

    /// <summary>Writes the entries to a manifest, sorted ordinal by key. Deterministic. Internal — producing a
    /// manifest is the cooker's job.</summary>
    internal static void Write(Stream stream, IReadOnlyList<ManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);

        var sorted = entries.OrderBy(e => e.Key.Value, StringComparer.Ordinal).ToArray();

        stream.Write(Magic);
        WriteU32(stream, Version);
        WriteU32(stream, checked((uint)sorted.Length));

        foreach (var entry in sorted)
        {
            if (entry.ContentHash.Length != ContentHashBytes)
            {
                throw new ArgumentException(
                    $"manifest entry '{entry.Key}' has a {entry.ContentHash.Length}-byte hash, expected {ContentHashBytes}.");
            }

            WriteUtf8(stream, entry.Key.Value ?? throw new ArgumentException("a manifest entry key is AssetKey.None."));
            stream.WriteByte((byte)entry.Kind);
            WriteUtf8(stream, entry.BlobPath);
            stream.Write(entry.ContentHash);
        }
    }

    private static AssetKey ReadKey(ReadOnlySpan<byte> bytes, ref int offset, uint index)
    {
        var text = ReadUtf8(bytes, ref offset, ReadU16(bytes, ref offset, index), index);
        try
        {
            return new AssetKey(text);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new AssetException($"content.agmanifest entry {index} has an invalid key '{text}'.", ex);
        }
    }

    private static ushort ReadU16(ReadOnlySpan<byte> bytes, ref int offset, uint index)
    {
        Require(bytes, offset, 2, index);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
        offset += 2;
        return value;
    }

    private static byte ReadByte(ReadOnlySpan<byte> bytes, ref int offset, uint index)
    {
        Require(bytes, offset, 1, index);
        return bytes[offset++];
    }

    private static string ReadUtf8(ReadOnlySpan<byte> bytes, ref int offset, ushort length, uint index)
    {
        Require(bytes, offset, length, index);
        var text = System.Text.Encoding.UTF8.GetString(bytes.Slice(offset, length));
        offset += length;
        return text;
    }

    private static byte[] ReadHash(ReadOnlySpan<byte> bytes, ref int offset, uint index)
    {
        Require(bytes, offset, ContentHashBytes, index);
        var hash = bytes.Slice(offset, ContentHashBytes).ToArray();
        offset += ContentHashBytes;
        return hash;
    }

    private static void Require(ReadOnlySpan<byte> bytes, int offset, int count, uint index)
    {
        if (offset + count > bytes.Length)
        {
            throw new AssetException($"content.agmanifest ends mid-entry {index} (need {count} bytes at offset {offset}).");
        }
    }

    private static void WriteU32(Stream s, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        s.Write(buffer);
    }

    private static void WriteUtf8(Stream s, string value)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(value);
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(len, checked((ushort)utf8.Length));
        s.Write(len);
        s.Write(utf8);
    }
}
