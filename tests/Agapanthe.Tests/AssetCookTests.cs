using System.Security.Cryptography;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Agapanthe.Assets.Pipeline;
using Agapanthe.Core;

namespace Agapanthe.Tests;

/// <summary>Contenu-2 — the cook: a cooked model decodes back to exactly what <see cref="GltfLoader"/> produces
/// (the fidelity gate — this is why the render does not change), the cooker is incremental and deterministic,
/// keys are content-relative, and a compression drift is caught by a literal blob-hash pin.</summary>
public sealed class AssetCookTests
{
    // Pinned 2026-09-08 on .NET SDK 10.0.103 (JIT). This is the ONLY guard that a silent DeflateStream / payload
    // change is visible. Re-pin policy: if this fails, first confirm CookFidelity_* still pass (the decoded model
    // is unchanged → the render is unchanged), then re-pin here and note the new SDK version.
    private const string DamagedHelmetBlobSha256 =
        "2c6483c260c1d4f88b0ea8aa9f6106d19955bee41f069022568ef1d963b0aa97";

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static (string Root, string Out) NewCookDirs()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "agcook-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(baseDir, "content");
        Directory.CreateDirectory(Path.Combine(root, "models"));
        return (root, Path.Combine(baseDir, "out"));
    }

    // The cooker globs *.glb only (self-contained). Both real Sandbox models are .glb.
    private static void PlaceModel(string root, string fixtureName, string relativeKey)
    {
        var dest = Path.Combine(root, relativeKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(Fixture(fixtureName), dest, overwrite: true);
    }

    private static void AssertModelsEqual(ModelAsset expected, ModelAsset actual)
    {
        Assert.Equal(expected.Meshes.Count, actual.Meshes.Count);
        Assert.Equal(expected.Materials.Count, actual.Materials.Count);
        Assert.Equal(expected.Images.Count, actual.Images.Count);

        for (var m = 0; m < expected.Meshes.Count; m++)
        {
            var e = expected.Meshes[m];
            var a = actual.Meshes[m];
            Assert.Equal(e.Positions, a.Positions);           // bit-exact through MemoryMarshal
            Assert.Equal(e.Normals, a.Normals);
            Assert.Equal(e.Tangents, a.Tangents);
            Assert.Equal(e.Uvs, a.Uvs);
            Assert.Equal(e.Indices, a.Indices);
            Assert.Equal(e.MaterialIndex, a.MaterialIndex);
            Assert.Equal(e.WorldTransform, a.WorldTransform);
        }

        for (var i = 0; i < expected.Materials.Count; i++)
        {
            var e = expected.Materials[i];
            var a = actual.Materials[i];
            Assert.Equal(e.BaseColorFactor, a.BaseColorFactor);
            Assert.Equal(e.MetallicFactor, a.MetallicFactor);
            Assert.Equal(e.RoughnessFactor, a.RoughnessFactor);
            Assert.Equal(e.EmissiveFactor, a.EmissiveFactor);
            Assert.Equal(e.EmissiveStrength, a.EmissiveStrength);
            Assert.Equal(e.AlphaMode, a.AlphaMode);
            Assert.Equal(e.AlphaCutoff, a.AlphaCutoff);
            Assert.Equal(e.BaseColorImage, a.BaseColorImage);
            Assert.Equal(e.NormalImage, a.NormalImage);
            Assert.Equal(e.MetallicRoughnessImage, a.MetallicRoughnessImage);
            Assert.Equal(e.TextureSettings, a.TextureSettings);
        }

        for (var i = 0; i < expected.Images.Count; i++)
        {
            Assert.Equal(expected.Images[i].Width, actual.Images[i].Width);
            Assert.Equal(expected.Images[i].Height, actual.Images[i].Height);
            Assert.Equal(expected.Images[i].IsSrgb, actual.Images[i].IsSrgb);
            Assert.Equal(expected.Images[i].Rgba8Pixels, actual.Images[i].Rgba8Pixels);
        }
    }

    // ── Test 7 — cook a fixture, open, load ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Cook_Then_Open_Then_LoadModel()
    {
        var (root, outDir) = NewCookDirs();
        try
        {
            PlaceModel(root, "DamagedHelmet.glb", "models/DamagedHelmet.glb");
            var summary = CookRunner.Cook(root, outDir);
            Assert.Equal(1, summary.Cooked);
            Assert.True(summary.TotalBlobBytes > 0);

            var catalog = AssetCatalog.Open(outDir);
            Assert.True(catalog.Contains(new AssetKey("models/DamagedHelmet.glb")));
            Assert.Single(catalog.Keys);
            var model = catalog.LoadModel(new AssetKey("models/DamagedHelmet.glb"));
            Assert.NotEmpty(model.Meshes);
            Assert.Equal(5, model.Images.Count);

            // A .gltf dropped under content/ is ignored (not silently cooked with untracked deps).
            PlaceModel(root, "MetalRoughSpheres.glb", "models/spheres.glb");
            File.Copy(Fixture("Box.gltf"), Path.Combine(root, "models", "ignored.gltf"), overwrite: true);
            Assert.Equal(1, CookRunner.Cook(root, outDir).Cooked); // only spheres.glb; DamagedHelmet skipped, .gltf ignored
            Assert.False(AssetCatalog.Open(outDir).Contains(new AssetKey("models/ignored.gltf")));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    // ── Test 9 — the fidelity gate ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("DamagedHelmet.glb", "models/DamagedHelmet.glb")]
    [InlineData("MetalRoughSpheres.glb", "models/MetalRoughSpheres.glb")]
    public void CookFidelity_DecodedModelEqualsGltfLoader(string fixture, string key)
    {
        var (root, outDir) = NewCookDirs();
        try
        {
            PlaceModel(root, fixture, key);
            CookRunner.Cook(root, outDir);

            var expected = GltfLoader.Load(Fixture(fixture));
            var actual = AssetCatalog.Open(outDir).LoadModel(new AssetKey(key));
            AssertModelsEqual(expected, actual);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    // ── Test 3b — the compressed blob hash is pinned ─────────────────────────────────────────────────────────

    [Fact]
    public void CookedDamagedHelmet_BlobHashIsPinned()
    {
        var (root, outDir) = NewCookDirs();
        try
        {
            PlaceModel(root, "DamagedHelmet.glb", "models/DamagedHelmet.glb");
            CookRunner.Cook(root, outDir);

            var blob = File.ReadAllBytes(Path.Combine(outDir, "models", "DamagedHelmet.glb.agmodel"));
            Assert.True(
                DamagedHelmetBlobSha256 == Convert.ToHexStringLower(SHA256.HashData(blob)),
                "Cooked .agmodel bytes differ from the pin. This is EXPECTED on a different .NET SDK / OS "
                + "(DeflateStream output is not contractually stable across them) — it is a canary, not a corruption. "
                + "Confirm CookFidelity_DecodedModelEqualsGltfLoader still passes (the decoded model — hence the render "
                + "— is unchanged), then re-pin DamagedHelmetBlobSha256 and note the SDK/OS.");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    // ── Test 10 — incremental + deterministic ────────────────────────────────────────────────────────────────

    [Fact]
    public void Cook_IsIncremental_AndManifestIsStable()
    {
        var (root, outDir) = NewCookDirs();
        try
        {
            PlaceModel(root, "DamagedHelmet.glb", "models/DamagedHelmet.glb");
            PlaceModel(root, "MetalRoughSpheres.glb", "models/MetalRoughSpheres.glb");

            var first = CookRunner.Cook(root, outDir);
            Assert.Equal(2, first.Cooked);
            var manifest1 = File.ReadAllBytes(Path.Combine(outDir, AssetCatalog.ManifestFileName));

            var second = CookRunner.Cook(root, outDir);
            Assert.Equal(0, second.Cooked);
            Assert.Equal(2, second.Skipped);
            Assert.Equal(manifest1, File.ReadAllBytes(Path.Combine(outDir, AssetCatalog.ManifestFileName)));

            // Rewrite one source's BYTES: flip a byte near the very end (the BIN chunk is last in a .glb, so this is
            // buffer data — the container still parses, the SHA changes).
            var helmet = Path.Combine(root, "models", "DamagedHelmet.glb");
            var bytes = File.ReadAllBytes(helmet);
            bytes[^64] ^= 0xFF;
            File.WriteAllBytes(helmet, bytes);
            var third = CookRunner.Cook(root, outDir);
            Assert.Equal(1, third.Cooked);
            Assert.Equal(1, third.Skipped);

            // Delete a source → its orphan blob is pruned, not shipped forever.
            File.Delete(helmet);
            CookRunner.Cook(root, outDir);
            Assert.False(File.Exists(Path.Combine(outDir, "models", "DamagedHelmet.glb.agmodel")));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    // ── Test 11 — key from content-relative path; duplicate key fails ─────────────────────────────────────────

    [Fact]
    public void Cook_DerivesKeyFromContentRelativePath()
    {
        var (root, outDir) = NewCookDirs();
        try
        {
            PlaceModel(root, "DamagedHelmet.glb", "models/sub/deep.glb");
            CookRunner.Cook(root, outDir);
            Assert.True(AssetCatalog.Open(outDir).Contains(new AssetKey("models/sub/deep.glb")));
            Assert.Equal("models/sub/deep.glb", AssetCatalog.Open(outDir).Keys.Single().Value);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }
}
