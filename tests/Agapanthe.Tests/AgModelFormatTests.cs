using System.Buffers.Binary;
using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Agapanthe.Assets.Pipeline;

namespace Agapanthe.Tests;

/// <summary>Contenu-2 — the <c>.agmodel</c> blob: byte-identical round-trip, same-process determinism, and every
/// corruption rejected with a typed <see cref="AgModelException"/> (never an OOB read, never a half-built asset).</summary>
public sealed class AgModelFormatTests
{
    private static ModelAsset SampleModel()
    {
        var full = new MeshAsset
        {
            Positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Tangents = [new(1, 0, 0, 1), new(1, 0, 0, 1), new(1, 0, 0, 1)],
            Uvs = [new(0, 0), new(1, 0), new(0, 1)],
            Indices = [0, 1, 2],
            MaterialIndex = 0,
            WorldTransform = Matrix4x4.CreateTranslation(3, 4, 5),
            Name = "full-mesh",
        };
        var bare = new MeshAsset
        {
            Positions = [new(0, 0, 0), new(0, 0, 1)],
            Indices = [0, 1, 0],
            MaterialIndex = 1,
            Name = "bare-mesh",
        };

        var textured = new MaterialAsset
        {
            BaseColorFactor = new Vector4(0.2f, 0.4f, 0.6f, 1f),
            MetallicFactor = 0.1f,
            RoughnessFactor = 0.8f,
            NormalScale = 0.9f,
            OcclusionStrength = 0.7f,
            EmissiveFactor = new Vector3(0.1f, 0.2f, 0.3f),
            EmissiveStrength = 2.5f,
            AlphaMode = AlphaMode.Mask,
            AlphaCutoff = 0.33f,
            BaseColorImage = 0,
            NormalImage = 1,
            MetallicRoughnessImage = -1,
            OcclusionImage = -1,
            EmissiveImage = -1,
            TextureSettings = new TextureSettings(
                TextureWrap.ClampToEdge, TextureWrap.MirroredRepeat, TextureFilter.Nearest, TextureFilter.Linear),
            Name = "textured",
        };
        var plain = new MaterialAsset { BaseColorFactor = Vector4.One, Name = "plain" };

        var img0 = new ImageAsset { Rgba8Pixels = Enumerable.Range(0, 2 * 2 * 4).Select(i => (byte)i).ToArray(), Width = 2, Height = 2, IsSrgb = true };
        var img1 = new ImageAsset { Rgba8Pixels = new byte[1 * 3 * 4], Width = 1, Height = 3, IsSrgb = false };

        return new ModelAsset
        {
            Meshes = [full, bare],
            Materials = [textured, plain],
            Images = [img0, img1],
            Name = "sample",
        };
    }

    private static byte[] Write(ModelAsset model)
    {
        using var ms = new MemoryStream();
        AgModelWriter.Write(ms, model);
        return ms.ToArray();
    }

    // ── Test 1 — round-trip is byte-identical + field-by-field ────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_IsByteIdentical_AndFieldByField()
    {
        var original = SampleModel();
        var bytes = Write(original);

        var restored = AgModelFormat.Read(bytes);
        Assert.Equal(bytes, Write(restored)); // strongest check

        Assert.Equal("sample", restored.Name);
        Assert.Equal(2, restored.Meshes.Count);
        Assert.Equal(2, restored.Materials.Count);
        Assert.Equal(2, restored.Images.Count);

        var full = restored.Meshes[0];
        Assert.Equal(original.Meshes[0].Positions, full.Positions);
        Assert.Equal(original.Meshes[0].Normals, full.Normals);
        Assert.Equal(original.Meshes[0].Tangents, full.Tangents);
        Assert.Equal(original.Meshes[0].Uvs, full.Uvs);
        Assert.Equal(original.Meshes[0].Indices, full.Indices);
        Assert.Equal(original.Meshes[0].WorldTransform, full.WorldTransform);
        Assert.Equal(0, full.MaterialIndex);
        Assert.Equal(string.Empty, full.Name); // dropped

        var bare = restored.Meshes[1];
        Assert.Empty(bare.Normals);
        Assert.Empty(bare.Tangents);
        Assert.Empty(bare.Uvs);
        Assert.Equal(1, bare.MaterialIndex);

        var mat = restored.Materials[0];
        Assert.Equal(original.Materials[0].BaseColorFactor, mat.BaseColorFactor);
        Assert.Equal(original.Materials[0].EmissiveFactor, mat.EmissiveFactor);
        Assert.Equal(AlphaMode.Mask, mat.AlphaMode);
        Assert.Equal(0.33f, mat.AlphaCutoff);
        Assert.Equal(1, mat.NormalImage);
        Assert.Equal(original.Materials[0].TextureSettings, mat.TextureSettings);
        Assert.Equal(string.Empty, mat.Name); // dropped

        Assert.Equal(original.Images[0].Rgba8Pixels, restored.Images[0].Rgba8Pixels);
        Assert.True(restored.Images[0].IsSrgb);
        Assert.False(restored.Images[1].IsSrgb);
        Assert.Equal(1, restored.Images[1].Width);
        Assert.Equal(3, restored.Images[1].Height);
    }

    // ── Test 2 — same-process determinism ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_IsDeterministic_SameProcess()
    {
        var model = SampleModel();
        Assert.Equal(Write(model), Write(model));
    }

    // ── Test 3 — corruption is rejected ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Read_RejectsBadMagic()
    {
        var bytes = Write(SampleModel());
        bytes[0] ^= 0xFF;
        Assert.Throws<AgModelException>(() => AgModelFormat.Read(bytes));
    }

    [Fact]
    public void Read_RejectsUnknownVersion()
    {
        var bytes = Write(SampleModel());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 999);
        Assert.Throws<AgModelException>(() => AgModelFormat.Read(bytes));
    }

    [Fact]
    public void Read_RejectsTruncatedContainer()
    {
        var bytes = Write(SampleModel());
        Assert.Throws<AgModelException>(() => AgModelFormat.Read(bytes.AsSpan(0, 6).ToArray()));
        Assert.Throws<AgModelException>(() => AgModelFormat.Read(bytes.AsSpan(0, bytes.Length - 4).ToArray()));
    }

    [Fact]
    public void Read_RejectsPayloadLongerThanHeaderClaims()
    {
        var bytes = Write(SampleModel());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 4); // claim a 4-byte payload
        Assert.Throws<AgModelException>(() => AgModelFormat.Read(bytes));
    }

    [Fact]
    public void Read_RejectsNonFinitePosition()
    {
        var model = new ModelAsset
        {
            Meshes = [new MeshAsset { Positions = [new(float.NaN, 0, 0)], Indices = [0, 0, 0], MaterialIndex = -1 }],
            Materials = [],
            Images = [],
            Name = "nan",
        };
        // The writer does not reject it (it only checks stream lengths); the reader must, at the boundary.
        var bytes = Write(model);
        Assert.Throws<AgModelException>(() => AgModelFormat.Read(bytes));
    }

    // Wraps a raw payload in an AGMD container (deflated) so a test can craft a hostile payload directly.
    private static byte[] Container(byte[] payload)
    {
        using var ms = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        ms.Write("AGMD"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, AgModelFormat.Version);
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)payload.Length);
        ms.Write(u32);
        using (var deflate = new System.IO.Compression.DeflateStream(
                   ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(payload);
        }

        return ms.ToArray();
    }

    [Fact]
    public void Read_RejectsForgedCount_WithoutAllocating()
    {
        // Payload: name length 0, then meshCount = ~2 billion. `Reader.ReadCount` must reject it (count > bytes
        // remaining) with an AgModelException — never a `new MeshAsset[2_000_000_000]` OutOfMemoryException.
        var payload = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), 0x7FFF_FFFF);

        Assert.Throws<AgModelException>(() => AgModelFormat.Read(Container(payload)));
    }

    [Fact]
    public void Read_RejectsTruncationMidArray()
    {
        // name(0) | meshCount 1 | vertexCount 3 | indexCount 0 | materialIndex -1 | mask 0 | worldTransform(64)
        // then only 1 Vector3 where 3 were promised.
        using var ms = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        ms.WriteByte(0); ms.WriteByte(0);                                  // name length 0
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 1); ms.Write(u32);   // meshCount
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 3); ms.Write(u32);   // vertexCount
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); ms.Write(u32);   // indexCount
        BinaryPrimitives.WriteInt32LittleEndian(u32, -1); ms.Write(u32);   // materialIndex
        ms.WriteByte(0);                                                   // streamMask
        ms.Write(new byte[64]);                                            // worldTransform
        ms.Write(new byte[12]);                                            // only 1 of the 3 promised positions

        Assert.Throws<AgModelException>(() => AgModelFormat.Read(Container(ms.ToArray())));
    }

    [Fact]
    public void Read_RejectsMaterialIndexOutOfRange()
    {
        var model = new ModelAsset
        {
            Meshes = [new MeshAsset { Positions = [Vector3.Zero], Indices = [0, 0, 0], MaterialIndex = 5 }],
            Materials = [new MaterialAsset { Name = "only" }],
            Images = [],
            Name = "oor",
        };
        var bytes = Write(model);
        Assert.Throws<AgModelException>(() => AgModelFormat.Read(bytes));
    }
}
