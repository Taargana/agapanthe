using System.Numerics;
using System.Text;
using Agapanthe.Assets;
using Agapanthe.Assets.Gltf;

namespace Agapanthe.Tests;

/// <summary>
/// M4-05: typed accessor decoding. Covers the two layout regimes with real Khronos fixtures — Box
/// (tightly packed, contiguous fast path) and BoxInterleaved (byteStride, strided path) — which are
/// the same cube and must decode to identical geometry, plus the out-of-subset error paths on
/// minimal inline glTF documents.
/// </summary>
public class AccessorReaderTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // Box.gltf / BoxInterleaved.gltf accessor indices (identical in both fixtures):
    //   0 = indices (SCALAR u16, count 36), 1 = NORMAL (VEC3 f32), 2 = POSITION (VEC3 f32).
    private const int IndicesAccessor = 0;
    private const int NormalAccessor = 1;
    private const int PositionAccessor = 2;

    // ------------------------------------------------------------------ Box (contiguous)

    [Fact]
    public void ReadVec3_BoxPositions_HasExpectedCountAndBounds()
    {
        var reader = new AccessorReader(GltfDocument.Load(FixturePath("Box.gltf")));

        Vector3[] positions = reader.ReadVec3(PositionAccessor);

        Assert.Equal(24, positions.Length);

        Vector3 min = positions[0], max = positions[0];
        foreach (Vector3 p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        // Matches accessor 2's declared min/max in the fixture.
        Assert.Equal(new Vector3(-0.5f, -0.5f, -0.5f), min);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), max);

        // First vertex, read straight out of Box0.bin (little-endian floats).
        Assert.Equal(new Vector3(-0.5f, -0.5f, 0.5f), positions[0]);
    }

    [Fact]
    public void ReadVec3_BoxNormals_HasExpectedCountAndBounds()
    {
        var reader = new AccessorReader(GltfDocument.Load(FixturePath("Box.gltf")));

        Vector3[] normals = reader.ReadVec3(NormalAccessor);

        Assert.Equal(24, normals.Length);
        Assert.Equal(new Vector3(0f, 0f, 1f), normals[0]);
        foreach (Vector3 n in normals)
        {
            Assert.Equal(1f, n.Length(), 5); // unit normals
        }
    }

    [Fact]
    public void ReadIndices_BoxU16_WidensToUInt()
    {
        var reader = new AccessorReader(GltfDocument.Load(FixturePath("Box.gltf")));

        uint[] indices = reader.ReadIndices(IndicesAccessor);

        Assert.Equal(36, indices.Length);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 2, 1 }, indices[..6]);
        Assert.All(indices, i => Assert.True(i < 24));
        Assert.Equal(23u, indices.Max());
    }

    // ------------------------------------------------------------------ BoxInterleaved (strided)

    [Fact]
    public void ReadVec3_InterleavedMatchesBox_ElementByElement()
    {
        var box = new AccessorReader(GltfDocument.Load(FixturePath("Box.gltf")));
        var interleaved = new AccessorReader(GltfDocument.Load(FixturePath("BoxInterleaved.gltf")));

        // BoxInterleaved packs NORMAL+POSITION at byteStride 24 (POSITION at accessor byteOffset 12);
        // decoding must yield exactly the same geometry as the tightly-packed Box.
        Assert.Equal(box.ReadVec3(PositionAccessor), interleaved.ReadVec3(PositionAccessor));
        Assert.Equal(box.ReadVec3(NormalAccessor), interleaved.ReadVec3(NormalAccessor));
        Assert.Equal(box.ReadIndices(IndicesAccessor), interleaved.ReadIndices(IndicesAccessor));
    }

    [Fact]
    public void ReadVec3_InterleavedPositions_Bounds()
    {
        var reader = new AccessorReader(GltfDocument.Load(FixturePath("BoxInterleaved.gltf")));

        Vector3[] positions = reader.ReadVec3(PositionAccessor);

        Assert.Equal(24, positions.Length);
        Assert.Equal(new Vector3(-0.5f, -0.5f, 0.5f), positions[0]);
    }

    // ------------------------------------------------------------------ synthetic inline glTF helpers

    /// <summary>
    /// Builds a one-buffer inline glTF whose single accessor/bufferView are described by
    /// <paramref name="accessor"/> and <paramref name="bufferView"/> JSON fragments over an 8-byte
    /// zero payload, for exercising the validation paths.
    /// </summary>
    private static GltfDocument InlineSingleAccessor(string accessor, string bufferView, int bufferBytes = 8)
    {
        string b64 = Convert.ToBase64String(new byte[bufferBytes]);
        string gltf = $$"""
        {
          "asset": { "version": "2.0" },
          "accessors": [ {{accessor}} ],
          "bufferViews": [ {{bufferView}} ],
          "buffers": [ { "byteLength": {{bufferBytes}}, "uri": "data:application/octet-stream;base64,{{b64}}" } ]
        }
        """;
        return GltfDocument.LoadFromBytes(Encoding.UTF8.GetBytes(gltf), sourcePath: "inline.gltf");
    }

    // ------------------------------------------------------------------ error paths

    [Fact]
    public void ReadVec3_NoBufferView_ThrowsSparse()
    {
        // Accessor with no bufferView == sparse-only data source (unsupported).
        GltfDocument doc = InlineSingleAccessor(
            accessor: """{ "componentType": 5126, "count": 1, "type": "VEC3" }""",
            bufferView: """{ "buffer": 0, "byteOffset": 0, "byteLength": 8 }""");
        var reader = new AccessorReader(doc);

        AssetException ex = Assert.Throws<AssetException>(() => reader.ReadVec3(0));
        Assert.Contains("sparse", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadVec3_TypeMismatch_Throws()
    {
        // SCALAR accessor read as VEC3.
        GltfDocument doc = InlineSingleAccessor(
            accessor: """{ "bufferView": 0, "componentType": 5126, "count": 1, "type": "SCALAR" }""",
            bufferView: """{ "buffer": 0, "byteOffset": 0, "byteLength": 8 }""");
        var reader = new AccessorReader(doc);

        AssetException ex = Assert.Throws<AssetException>(() => reader.ReadVec3(0));
        Assert.Contains("VEC3", ex.Message);
        Assert.Contains("SCALAR", ex.Message);
    }

    [Fact]
    public void ReadVec3_NonFloatComponentType_Throws()
    {
        // Normalized u16 VEC3 — right shape, wrong component type for phase 1.
        GltfDocument doc = InlineSingleAccessor(
            accessor: """{ "bufferView": 0, "componentType": 5123, "normalized": true, "count": 1, "type": "VEC3" }""",
            bufferView: """{ "buffer": 0, "byteOffset": 0, "byteLength": 8 }""");
        var reader = new AccessorReader(doc);

        AssetException ex = Assert.Throws<AssetException>(() => reader.ReadVec3(0));
        Assert.Contains("float", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadVec3_OverrunsBufferView_Throws()
    {
        // count 2 VEC3 floats = 24 bytes, but the view is only 8.
        GltfDocument doc = InlineSingleAccessor(
            accessor: """{ "bufferView": 0, "componentType": 5126, "count": 2, "type": "VEC3" }""",
            bufferView: """{ "buffer": 0, "byteOffset": 0, "byteLength": 8 }""",
            bufferBytes: 8);
        var reader = new AccessorReader(doc);

        AssetException ex = Assert.Throws<AssetException>(() => reader.ReadVec3(0));
        Assert.Contains("bufferView", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadIndices_UnsupportedComponentType_Throws()
    {
        // Signed short (5122) is not a valid index component type.
        GltfDocument doc = InlineSingleAccessor(
            accessor: """{ "bufferView": 0, "componentType": 5122, "count": 1, "type": "SCALAR" }""",
            bufferView: """{ "buffer": 0, "byteOffset": 0, "byteLength": 8 }""");
        var reader = new AccessorReader(doc);

        AssetException ex = Assert.Throws<AssetException>(() => reader.ReadIndices(0));
        Assert.Contains("indices", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadIndices_U8_WidensToUInt()
    {
        // u8 indices are accepted (common on small models): payload bytes 0,1,2,3 -> uint.
        string b64 = Convert.ToBase64String(new byte[] { 0, 1, 2, 3, 0, 0, 0, 0 });
        string gltf = $$"""
        {
          "asset": { "version": "2.0" },
          "accessors": [ { "bufferView": 0, "componentType": 5121, "count": 4, "type": "SCALAR" } ],
          "bufferViews": [ { "buffer": 0, "byteOffset": 0, "byteLength": 4 } ],
          "buffers": [ { "byteLength": 8, "uri": "data:application/octet-stream;base64,{{b64}}" } ]
        }
        """;
        var reader = new AccessorReader(GltfDocument.LoadFromBytes(Encoding.UTF8.GetBytes(gltf)));

        Assert.Equal(new uint[] { 0, 1, 2, 3 }, reader.ReadIndices(0));
    }

    [Fact]
    public void ReadVec3_AccessorIndexOutOfRange_Throws()
    {
        GltfDocument doc = InlineSingleAccessor(
            accessor: """{ "bufferView": 0, "componentType": 5126, "count": 1, "type": "VEC3" }""",
            bufferView: """{ "buffer": 0, "byteOffset": 0, "byteLength": 12 }""",
            bufferBytes: 12);
        var reader = new AccessorReader(doc);

        AssetException ex = Assert.Throws<AssetException>(() => reader.ReadVec3(99));
        Assert.Contains("out of range", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
