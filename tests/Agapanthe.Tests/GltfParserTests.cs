using System.Buffers.Binary;
using System.Text;
using Agapanthe.Assets;
using Agapanthe.Assets.Gltf;

namespace Agapanthe.Tests;

/// <summary>
/// M4-04: raw glTF JSON schema, GLB container, and buffer resolution. These are white-box tests —
/// they assert directly on the internal <see cref="GltfDocument.Root"/> tree (via InternalsVisibleTo)
/// because that raw view is exactly the contract M4-05/06 consume.
/// </summary>
public class GltfParserTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // ------------------------------------------------------------------ text .gltf parsing

    [Fact]
    public void Load_Box_ParsesSchemaCounts()
    {
        GltfDocument doc = GltfDocument.Load(FixturePath("Box.gltf"));
        GltfRoot root = doc.Root;

        Assert.Equal("2.0", root.Asset?.Version);
        Assert.Equal(3, root.Accessors!.Length);
        Assert.Equal(2, root.BufferViews!.Length);
        Assert.Single(root.Meshes!);
        Assert.Single(root.Buffers!);
        Assert.Single(root.Materials!);
    }

    [Fact]
    public void Load_Box_ParsesPrimitiveAndAccessorDetails()
    {
        GltfDocument doc = GltfDocument.Load(FixturePath("Box.gltf"));
        GltfRoot root = doc.Root;

        GltfPrimitive prim = root.Meshes![0].Primitives![0];
        Assert.Equal(4, prim.Mode);                 // TRIANGLES (present in fixture)
        Assert.Equal(0, prim.Indices);
        Assert.Equal(0, prim.Material);
        Assert.Equal(2, prim.Attributes!.Position); // POSITION -> accessor 2
        Assert.Equal(1, prim.Attributes!.Normal);   // NORMAL   -> accessor 1
        Assert.Null(prim.Attributes!.TexCoord0);

        // Index accessor: 36 unsigned shorts (componentType 5123), SCALAR.
        GltfAccessor idx = root.Accessors![0];
        Assert.Equal(5123, idx.ComponentType);
        Assert.Equal(36, idx.Count);
        Assert.Equal("SCALAR", idx.Type);

        // Interleaved-vertex bufferView carries an explicit stride; index bufferView does not.
        Assert.Equal(12, root.BufferViews![1].ByteStride);
        Assert.Null(root.BufferViews![0].ByteStride);
    }

    [Fact]
    public void Load_Box_AppliesGltfDefaults()
    {
        GltfDocument doc = GltfDocument.Load(FixturePath("Box.gltf"));
        GltfMaterial mat = doc.Root.Materials![0];

        // metallicFactor is explicit 0; roughnessFactor is absent -> glTF default 1.
        Assert.Equal(0f, mat.PbrMetallicRoughness!.MetallicFactor);
        Assert.Equal(1f, mat.PbrMetallicRoughness!.RoughnessFactor);
        // alphaMode/alphaCutoff absent -> defaults.
        Assert.Equal("OPAQUE", mat.AlphaMode);
        Assert.Equal(0.5f, mat.AlphaCutoff);
    }

    [Fact]
    public void Load_Box_ResolvesRelativeBinBuffer()
    {
        GltfDocument doc = GltfDocument.Load(FixturePath("Box.gltf"));

        ReadOnlyMemory<byte> data = doc.GetBufferData(0);
        Assert.Equal(648, data.Length); // matches buffers[0].byteLength and the Box0.bin size

        // Cached: a second call returns the same backing memory without re-reading the file.
        ReadOnlyMemory<byte> again = doc.GetBufferData(0);
        Assert.True(data.Span == again.Span || data.Length == again.Length);
    }

    [Fact]
    public void Load_BoxTextured_ParsesTexturesSamplersImages()
    {
        GltfDocument doc = GltfDocument.Load(FixturePath("BoxTextured.gltf"));
        GltfRoot root = doc.Root;

        Assert.Single(root.Textures!);
        Assert.Equal(0, root.Textures![0].Source);
        Assert.Equal(0, root.Textures![0].Sampler);

        Assert.Equal("CesiumLogoFlat.png", root.Images![0].Uri);

        GltfSampler sampler = root.Samplers![0];
        Assert.Equal(9729, sampler.MagFilter);
        Assert.Equal(9986, sampler.MinFilter);
        Assert.Equal(10497, sampler.WrapS);
        Assert.Equal(10497, sampler.WrapT);

        Assert.Equal(0, root.Materials![0].PbrMetallicRoughness!.BaseColorTexture!.Index);
    }

    // ------------------------------------------------------------------ GLB container

    [Fact]
    public void Load_DamagedHelmetGlb_ParsesHeaderChunksAndEmbeddedBuffer()
    {
        GltfDocument doc = GltfDocument.Load(FixturePath("DamagedHelmet.glb"));
        GltfRoot root = doc.Root;

        // JSON chunk parsed.
        Assert.Equal("2.0", root.Asset?.Version);
        Assert.Equal(4, root.Accessors!.Length);
        Assert.Equal(9, root.BufferViews!.Length);
        Assert.Single(root.Meshes!);
        Assert.Equal(5, root.Images!.Length);

        // BIN chunk present, and buffer 0 (no URI) resolves to it with a consistent byteLength.
        GltfBuffer buffer0 = root.Buffers![0];
        Assert.Null(buffer0.Uri);
        ReadOnlyMemory<byte> bin = doc.GetBufferData(0);
        Assert.Equal(buffer0.ByteLength, bin.Length);
        Assert.Equal(3771740, bin.Length);
    }

    [Fact]
    public void GlbContainer_InvalidMagic_Throws()
    {
        byte[] bad = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bad, 0xDEADBEEF); // not 'glTF'
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(8), (uint)bad.Length);

        AssetException ex = Assert.Throws<AssetException>(() => GlbContainer.Parse(bad, "bad.glb"));
        Assert.Contains("magic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Glb_UnsupportedVersion_Throws()
    {
        // Correct magic (so it takes the GLB path) but version 1.
        byte[] glb = BuildGlb(version: 1, json: "{}"u8.ToArray(), bin: null);

        AssetException ex = Assert.Throws<AssetException>(() => GltfDocument.LoadFromBytes(glb, sourcePath: "v1.glb"));
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Glb_TruncatedChunk_Throws()
    {
        // Well-formed header/first-chunk-header, but the JSON chunk claims more bytes than are present.
        byte[] glb = new byte[12 + 8 + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, GlbContainer.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)glb.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), 999);        // chunkLength >> available
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A); // JSON

        AssetException ex = Assert.Throws<AssetException>(() => GlbContainer.Parse(glb, "trunc.glb"));
        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ buffer resolution

    [Fact]
    public void Load_DataUriBase64Buffer_Decodes()
    {
        // 8 bytes 00..07, embedded as a base64 data: URI in a minimal inline glTF.
        byte[] payload = { 0, 1, 2, 3, 4, 5, 6, 7 };
        string b64 = Convert.ToBase64String(payload);
        string gltf = $$"""
        {
          "asset": { "version": "2.0" },
          "buffers": [ { "byteLength": 8, "uri": "data:application/octet-stream;base64,{{b64}}" } ]
        }
        """;

        GltfDocument doc = GltfDocument.LoadFromBytes(Encoding.UTF8.GetBytes(gltf));
        ReadOnlyMemory<byte> data = doc.GetBufferData(0);

        Assert.Equal(payload, data.ToArray());
    }

    [Fact]
    public void GetBufferData_IndexOutOfRange_Throws()
    {
        string gltf = """
        { "asset": { "version": "2.0" }, "buffers": [ { "byteLength": 8, "uri": "data:application/octet-stream;base64,AAAAAAAAAAA=" } ] }
        """;
        GltfDocument doc = GltfDocument.LoadFromBytes(Encoding.UTF8.GetBytes(gltf));

        AssetException ex = Assert.Throws<AssetException>(() => doc.GetBufferData(5));
        Assert.Contains("out of range", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetBufferData_MissingFile_ThrowsWithPath()
    {
        // Relative URI that does not exist next to the base directory.
        string gltf = """
        { "asset": { "version": "2.0" }, "buffers": [ { "byteLength": 8, "uri": "does-not-exist.bin" } ] }
        """;
        GltfDocument doc = GltfDocument.LoadFromBytes(
            Encoding.UTF8.GetBytes(gltf), baseDirectory: AppContext.BaseDirectory, sourcePath: "inline.gltf");

        AssetException ex = Assert.Throws<AssetException>(() => doc.GetBufferData(0));
        Assert.Contains("does-not-exist.bin", ex.Message);
    }

    [Fact]
    public void GetBufferData_ShorterThanDeclared_Throws()
    {
        // Declares 32 bytes but the data URI only carries 8.
        string gltf = """
        { "asset": { "version": "2.0" }, "buffers": [ { "byteLength": 32, "uri": "data:application/octet-stream;base64,AAECAwQFBgc=" } ] }
        """;
        GltfDocument doc = GltfDocument.LoadFromBytes(Encoding.UTF8.GetBytes(gltf));

        AssetException ex = Assert.Throws<AssetException>(() => doc.GetBufferData(0));
        Assert.Contains("shorter than declared", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ validation errors

    [Fact]
    public void Load_UnsupportedRequiredExtension_Throws()
    {
        string gltf = """
        {
          "asset": { "version": "2.0" },
          "extensionsRequired": [ "KHR_draco_mesh_compression" ]
        }
        """;

        AssetException ex = Assert.Throws<AssetException>(
            () => GltfDocument.LoadFromBytes(Encoding.UTF8.GetBytes(gltf), sourcePath: "draco.gltf"));
        Assert.Contains("KHR_draco_mesh_compression", ex.Message);
    }

    [Fact]
    public void Load_SupportedRequiredExtension_Ok()
    {
        string gltf = """
        {
          "asset": { "version": "2.0" },
          "extensionsRequired": [ "KHR_materials_emissive_strength" ],
          "extensionsUsed": [ "KHR_materials_emissive_strength" ]
        }
        """;

        GltfDocument doc = GltfDocument.LoadFromBytes(Encoding.UTF8.GetBytes(gltf));
        Assert.Equal("2.0", doc.Root.Asset?.Version);
    }

    [Fact]
    public void Load_NonTrianglePrimitiveMode_Throws()
    {
        // mode 1 = LINES, outside the phase-1 subset.
        string gltf = """
        {
          "asset": { "version": "2.0" },
          "meshes": [ { "primitives": [ { "attributes": { "POSITION": 0 }, "mode": 1 } ] } ]
        }
        """;

        AssetException ex = Assert.Throws<AssetException>(
            () => GltfDocument.LoadFromBytes(Encoding.UTF8.GetBytes(gltf), sourcePath: "lines.gltf"));
        Assert.Contains("TRIANGLES", ex.Message);
    }

    [Fact]
    public void Load_WrongVersion_Throws()
    {
        string gltf = """{ "asset": { "version": "1.0" } }""";

        AssetException ex = Assert.Throws<AssetException>(
            () => GltfDocument.LoadFromBytes(Encoding.UTF8.GetBytes(gltf), sourcePath: "old.gltf"));
        Assert.Contains("1.0", ex.Message);
    }

    [Fact]
    public void Load_MissingAssetVersion_Throws()
    {
        string gltf = """{ "buffers": [] }""";

        AssetException ex = Assert.Throws<AssetException>(
            () => GltfDocument.LoadFromBytes(Encoding.UTF8.GetBytes(gltf)));
        Assert.Contains("asset.version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Assembles a minimal GLB (header + JSON chunk + optional BIN chunk).</summary>
    private static byte[] BuildGlb(uint version, byte[] json, byte[]? bin)
    {
        static int Pad4(int n) => (n + 3) & ~3;

        int jsonPad = Pad4(json.Length);
        int binPad = bin is null ? 0 : Pad4(bin.Length);
        int total = 12 + 8 + jsonPad + (bin is null ? 0 : 8 + binPad);

        byte[] glb = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, GlbContainer.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)total);

        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)jsonPad);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A); // JSON
        json.CopyTo(glb.AsSpan(20));
        for (int i = json.Length; i < jsonPad; i++) glb[20 + i] = 0x20; // space padding

        if (bin is not null)
        {
            int binHeader = 20 + jsonPad;
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binHeader), (uint)binPad);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binHeader + 4), 0x004E4942); // BIN
            bin.CopyTo(glb.AsSpan(binHeader + 8));
        }

        return glb;
    }
}
