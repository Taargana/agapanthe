using Agapanthe.Assets;
using Agapanthe.Assets.Pipeline;
using Agapanthe.Core;

namespace Agapanthe.Tests;

/// <summary>Contenu-3c — <see cref="CookRunner"/>'s new procedural-generator phase: a `content/procedural/*.toml`
/// cooks into an ordinary <see cref="AssetKind.Model"/> blob (indistinguishable from a glTF-sourced one), with
/// its own pure-TOML-hash incrementality (no source-model dependency, unlike scenes).</summary>
public sealed class CookRunnerProceduralTests
{
    private static (string Root, string Out) NewCookDirs()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "agcookproc-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(baseDir, "content");
        Directory.CreateDirectory(Path.Combine(root, "procedural"));
        return (root, Path.Combine(baseDir, "out"));
    }

    private static void WriteProcedural(string root, string relativeKey, string toml)
    {
        var dest = Path.Combine(root, relativeKey.Replace('/', Path.DirectorySeparatorChar) + ".toml");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, toml);
    }

    private const string SphereToml = """
        generator = "uv_sphere"
        radius = 3.0
        segments = 8
        rings = 4
        """;

    [Fact]
    public void Cook_ProceduralToml_ProducesAModelKindBlob()
    {
        var (root, outDir) = NewCookDirs();
        try
        {
            WriteProcedural(root, "procedural/probe", SphereToml);
            var summary = CookRunner.Cook(root, outDir);
            Assert.Equal(1, summary.Cooked);

            var catalog = AssetCatalog.Open(outDir);
            var key = new AssetKey("procedural/probe");
            Assert.True(catalog.Contains(key));
            var model = catalog.LoadModel(key); // AssetKind.Model — LoadModel must not distinguish origin
            Assert.Single(model.Meshes);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    [Fact]
    public void Cook_ProceduralToml_IsIncremental()
    {
        var (root, outDir) = NewCookDirs();
        try
        {
            WriteProcedural(root, "procedural/probe", SphereToml);
            var first = CookRunner.Cook(root, outDir);
            Assert.Equal(1, first.Cooked);

            var second = CookRunner.Cook(root, outDir);
            Assert.Equal(0, second.Cooked);
            Assert.Equal(1, second.Skipped);

            WriteProcedural(root, "procedural/probe", SphereToml.Replace("3.0", "5.0"));
            var third = CookRunner.Cook(root, outDir);
            Assert.Equal(1, third.Cooked);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    [Fact]
    public void Cook_ProceduralToml_KeyDropsExtension_KeepsSubdirectory()
    {
        var (root, outDir) = NewCookDirs();
        try
        {
            WriteProcedural(root, "procedural/bodies/planet-surface", SphereToml);
            CookRunner.Cook(root, outDir);
            Assert.True(AssetCatalog.Open(outDir).Contains(new AssetKey("procedural/bodies/planet-surface")));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    [Fact]
    public void Cook_SceneReferencingProceduralModel_Compiles()
    {
        var (root, outDir) = NewCookDirs();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scenes"));
            WriteProcedural(root, "procedural/probe", SphereToml);
            File.WriteAllText(Path.Combine(root, "scenes", "test.toml"), """
                name = "test"
                [[entity]]
                model = "procedural/probe"
                """);

            var summary = CookRunner.Cook(root, outDir);
            Assert.True(summary.Cooked >= 2); // the procedural model + the scene

            var catalog = AssetCatalog.Open(outDir);
            var def = catalog.LoadScene(new AssetKey("scenes/test"));
            Assert.Single(def.Entities);
            Assert.Equal(new AssetKey("procedural/probe"), def.Entities[0].Model);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }
}
