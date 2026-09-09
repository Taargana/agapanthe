using System.Buffers.Binary;
using Agapanthe.Assets;
using Agapanthe.Assets.Pipeline;
using Agapanthe.Core;

namespace Agapanthe.Tests;

/// <summary>Contenu-2 — the <c>content.agmanifest</c> index: round-trips, comes back sorted, and rejects a
/// forged / reordered / duplicate manifest as corruption (the Contenu-1 W4 symmetry with "Duplicate GlobalId").</summary>
public sealed class ContentManifestTests
{
    private static byte[] Hash(byte seed) => Enumerable.Range(0, ContentManifest.ContentHashBytes).Select(i => (byte)(i + seed)).ToArray();

    private static ManifestEntry Entry(string key, byte seed = 0)
        => new(new AssetKey(key), AssetKind.Model, key + ".agmodel", Hash(seed));

    private static byte[] Write(params ManifestEntry[] entries)
    {
        using var ms = new MemoryStream();
        ContentManifestWriter.Write(ms, entries);
        return ms.ToArray();
    }

    // ── Test 4 — round-trip, keys ascending ──────────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_PreservesEntries_SortedByKey()
    {
        // Deliberately out of order on input.
        var bytes = Write(Entry("models/c", 3), Entry("models/a", 1), Entry("models/b", 2));
        var map = ContentManifest.Read(bytes);

        Assert.Equal(3, map.Count);
        Assert.Equal("models/a.agmodel", map[new AssetKey("models/a")].BlobPath);
        Assert.Equal(AssetKind.Model, map[new AssetKey("models/b")].Kind);
        Assert.Equal(Hash(3), map[new AssetKey("models/c")].ContentHash);

        // Re-writing the read-back set reproduces the exact bytes (ordinal sort is stable).
        Assert.Equal(bytes, Write([.. map.Values]));
    }

    // ── Test 5 — corruption is rejected ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Read_RejectsBadMagic()
    {
        var bytes = Write(Entry("models/a"));
        bytes[1] ^= 0xFF;
        Assert.Throws<AssetException>(() => ContentManifest.Read(bytes));
    }

    [Fact]
    public void Read_RejectsUnknownVersion()
    {
        var bytes = Write(Entry("models/a"));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 7);
        Assert.Throws<AssetException>(() => ContentManifest.Read(bytes));
    }

    [Fact]
    public void Read_RejectsNonAscendingKeys()
    {
        // Craft a manifest with two entries whose payload keys are descending. Entries are: [magic|ver|count=2]
        // then key "b" then key "a" — hand-write it so the writer's sort can't fix it.
        using var ms = new MemoryStream();
        ms.Write("AGMN"u8);
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, ContentManifest.Version);
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 2);
        ms.Write(u32);
        var u16 = new byte[2];
        foreach (var key in new[] { "b", "a" })
        {
            var k = System.Text.Encoding.UTF8.GetBytes(key);
            BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)k.Length);
            ms.Write(u16);
            ms.Write(k);
            ms.WriteByte((byte)AssetKind.Model);
            BinaryPrimitives.WriteUInt16LittleEndian(u16, 0);
            ms.Write(u16); // empty blob path
            ms.Write(new byte[ContentManifest.ContentHashBytes]);
        }

        Assert.Throws<AssetException>(() => ContentManifest.Read(ms.ToArray()));
    }

    [Fact]
    public void Read_RejectsKeyTheCtorRejects()
    {
        using var ms = new MemoryStream();
        ms.Write("AGMN"u8);
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, ContentManifest.Version);
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 1);
        ms.Write(u32);
        var bad = System.Text.Encoding.UTF8.GetBytes("../escape");
        Span<byte> u16 = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)bad.Length);
        ms.Write(u16);
        ms.Write(bad);
        ms.WriteByte((byte)AssetKind.Model);
        BinaryPrimitives.WriteUInt16LittleEndian(u16, 0);
        ms.Write(u16);
        ms.Write(new byte[ContentManifest.ContentHashBytes]);

        Assert.Throws<AssetException>(() => ContentManifest.Read(ms.ToArray()));
    }

    [Fact]
    public void Write_RejectsWrongSizeHash()
        => Assert.Throws<ArgumentException>(
            () => Write(new ManifestEntry(new AssetKey("models/a"), AssetKind.Model, "x", new byte[8])));
}
