using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Agapanthe.Assets.Pipeline;
using Agapanthe.Core;

namespace Agapanthe.Tests;

/// <summary>Contenu-2 — the runtime catalog: opening fails loudly on a missing manifest, and an absent /
/// wrong-kind key throws rather than returning something wrong. (The cook→load happy path is in
/// <see cref="AssetCookTests"/>, which needs the cooker.)</summary>
public sealed class AssetCatalogTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agcatalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ModelAsset TinyModel() => new()
    {
        Meshes = [new MeshAsset { Positions = [Vector3.Zero], Indices = [0, 0, 0], MaterialIndex = -1 }],
        Materials = [],
        Images = [],
        Name = "tiny",
    };

    // ── Test 6 — Open on a missing manifest ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Open_MissingManifest_ThrowsNamingThePath()
    {
        var dir = TempDir();
        try
        {
            var ex = Assert.Throws<AssetException>(() => AssetCatalog.Open(dir));
            Assert.Contains(dir, ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── Test 8 — unknown key / wrong kind ────────────────────────────────────────────────────────────────────

    [Fact]
    public void LoadModel_UnknownKey_Throws()
    {
        var dir = TempDir();
        try
        {
            var blob = Path.Combine(dir, "models", "a.agmodel");
            AgModelWriter.WriteFile(blob, TinyModel());
            var entry = new ManifestEntry(new AssetKey("models/a"), AssetKind.Model, "models/a.agmodel", Sha(blob));
            ContentManifestWriter.WriteFile(Path.Combine(dir, AssetCatalog.ManifestFileName), [entry]);

            var catalog = AssetCatalog.Open(dir);
            Assert.True(catalog.Contains(new AssetKey("models/a")));
            Assert.Single(catalog.Keys);
            Assert.Equal("tiny", catalog.LoadModel(new AssetKey("models/a")).Name);
            Assert.Throws<AssetException>(() => catalog.LoadModel(new AssetKey("models/missing")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadModel_WrongKind_Throws()
    {
        var dir = TempDir();
        try
        {
            var blob = Path.Combine(dir, "env", "sky.agmodel");
            AgModelWriter.WriteFile(blob, TinyModel());
            var entry = new ManifestEntry(new AssetKey("env/sky"), AssetKind.Environment, "env/sky.agmodel", Sha(blob));
            ContentManifestWriter.WriteFile(Path.Combine(dir, AssetCatalog.ManifestFileName), [entry]);

            var catalog = AssetCatalog.Open(dir);
            var ex = Assert.Throws<AssetException>(() => catalog.LoadModel(new AssetKey("env/sky")));
            Assert.Contains("Environment", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] Sha(string path)
        => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
}
