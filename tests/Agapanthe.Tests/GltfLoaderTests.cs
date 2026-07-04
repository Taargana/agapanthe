using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;

namespace Agapanthe.Tests;

public class GltfLoaderTests
{
    private const float Tolerance = 1e-5f;

    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Box_LoadsOneMeshWithMaterial()
    {
        var model = GltfLoader.Load(Fixture("Box.gltf"));

        var mesh = Assert.Single(model.Meshes);
        Assert.Equal(24, mesh.Positions.Length);
        Assert.Equal(24, mesh.Normals.Length);
        Assert.Equal(36, mesh.Indices.Length);
        Assert.Equal(0, mesh.MaterialIndex);

        var material = Assert.Single(model.Materials);
        Assert.Equal("Red", material.Name);
        Assert.Equal(0f, material.MetallicFactor);
        Assert.True((material.BaseColorFactor - new Vector4(0.8f, 0f, 0f, 1f)).Length() < 1e-3f);
        Assert.Empty(model.Images);
    }

    [Fact]
    public void Box_NodeMatrixIsAppliedAsWorldTransform()
    {
        // Box.gltf: the root node carries the COLLADA Z-up → glTF Y-up matrix, columns
        // (1,0,0), (0,0,-1), (0,1,0) — i.e. local (0,1,0) maps to world (0,0,-1).
        var model = GltfLoader.Load(Fixture("Box.gltf"));
        var world = model.Meshes[0].WorldTransform;

        var mapped = Vector3.Transform(Vector3.UnitY, world);
        Assert.True((mapped - new Vector3(0f, 0f, -1f)).Length() < Tolerance,
            $"expected (0,0,-1), got {mapped}");

        // The transformed unit cube must stay within ±0.5 in every axis (pure rotation).
        foreach (var p in model.Meshes[0].Positions)
        {
            var w = Vector3.Transform(p, world);
            Assert.InRange(MathF.Max(MathF.Abs(w.X), MathF.Max(MathF.Abs(w.Y), MathF.Abs(w.Z))), 0f, 0.5f + Tolerance);
        }
    }

    [Fact]
    public void BoxTextured_DecodesBaseColorImageAsSrgb()
    {
        var model = GltfLoader.Load(Fixture("BoxTextured.gltf"));

        var material = Assert.Single(model.Materials);
        Assert.True(material.BaseColorImage >= 0, "base color image expected");

        var image = model.Images[material.BaseColorImage];
        Assert.Equal(256, image.Width);
        Assert.Equal(256, image.Height);
        Assert.True(image.IsSrgb);
        Assert.Equal(256 * 256 * 4, image.Rgba8Pixels.Length);
    }

    [Fact]
    public void DamagedHelmet_LoadsGeometryMaterialAndEmbeddedImages()
    {
        var model = GltfLoader.Load(Fixture("DamagedHelmet.glb"));

        var mesh = Assert.Single(model.Meshes);
        Assert.True(mesh.Positions.Length > 0);
        Assert.Equal(mesh.Positions.Length, mesh.Normals.Length);
        Assert.Equal(mesh.Positions.Length, mesh.Uvs.Length);
        Assert.True(mesh.Indices.Length > 0 && mesh.Indices.Length % 3 == 0);

        var material = Assert.Single(model.Materials);
        Assert.True(material.BaseColorImage >= 0);
        Assert.True(material.NormalImage >= 0);
        Assert.True(material.MetallicRoughnessImage >= 0);

        // DamagedHelmet ships without TANGENT; the loader must have generated them because the
        // material has a normal map. Every tangent must be unit-length, ⊥ N, w = ±1.
        Assert.Equal(mesh.Positions.Length, mesh.Tangents.Length);
        for (var i = 0; i < mesh.Tangents.Length; i += 997) // sample, full sweep is slow-ish
        {
            var t = new Vector3(mesh.Tangents[i].X, mesh.Tangents[i].Y, mesh.Tangents[i].Z);
            Assert.True(MathF.Abs(t.Length() - 1f) < 1e-3f, $"non-unit tangent at {i}");
            Assert.True(MathF.Abs(Vector3.Dot(t, mesh.Normals[i])) < 1e-3f, $"tangent not ⊥ normal at {i}");
            Assert.True(MathF.Abs(mesh.Tangents[i].W) == 1f);
        }

        // GLB images: decoded from buffer views, sRGB decided per slot.
        Assert.True(model.Images[material.BaseColorImage].IsSrgb);
        Assert.False(model.Images[material.NormalImage].IsSrgb);
        Assert.False(model.Images[material.MetallicRoughnessImage].IsSrgb);
        Assert.All(model.Images, img => Assert.True(img.Width > 0 && img.Height > 0));
    }

    [Fact]
    public void TrsNode_TranslationLandsInWorldTransform()
    {
        // Minimal inline model: one triangle under a node with a TRS translation.
        const string json = """
        {
          "asset": { "version": "2.0" },
          "scene": 0,
          "scenes": [ { "nodes": [0] } ],
          "nodes": [ { "mesh": 0, "translation": [10, 20, 30], "scale": [2, 2, 2] } ],
          "meshes": [ { "primitives": [ { "attributes": { "POSITION": 0 } } ] } ],
          "accessors": [
            { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3",
              "min": [0,0,0], "max": [1,1,0] }
          ],
          "bufferViews": [ { "buffer": 0, "byteOffset": 0, "byteLength": 36 } ],
          "buffers": [ { "uri": "data:application/octet-stream;base64,BASE64", "byteLength": 36 } ]
        }
        """;

        var positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        var bytes = new byte[36];
        Buffer.BlockCopy(positions, 0, bytes, 0, 36);
        var payload = json.Replace("BASE64", Convert.ToBase64String(bytes));

        var dir = Path.Combine(Path.GetTempPath(), "agapanthe-gltf-tests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "trs.gltf");
        File.WriteAllText(path, payload);

        var model = GltfLoader.Load(path);
        var mesh = Assert.Single(model.Meshes);

        // Row-vector S*R*T: point (1,0,0) scaled by 2 then translated → (12, 20, 30).
        var w = Vector3.Transform(new Vector3(1, 0, 0), mesh.WorldTransform);
        Assert.True((w - new Vector3(12f, 20f, 30f)).Length() < Tolerance, $"got {w}");

        // No indices in the primitive → sequential.
        Assert.Equal(new uint[] { 0, 1, 2 }, mesh.Indices);
        Assert.Equal(-1, mesh.MaterialIndex);
        // No material → no normal map → no tangent generation.
        Assert.Empty(mesh.Tangents);
    }

    [Fact]
    public void MissingPosition_Throws()
    {
        const string json = """
        {
          "asset": { "version": "2.0" },
          "scene": 0,
          "scenes": [ { "nodes": [0] } ],
          "nodes": [ { "mesh": 0 } ],
          "meshes": [ { "primitives": [ { "attributes": { } } ] } ]
        }
        """;

        var dir = Path.Combine(Path.GetTempPath(), "agapanthe-gltf-tests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "no-position.gltf");
        File.WriteAllText(path, json);

        var e = Assert.Throws<AssetException>(() => GltfLoader.Load(path));
        Assert.Contains("POSITION", e.Message);
    }
}
