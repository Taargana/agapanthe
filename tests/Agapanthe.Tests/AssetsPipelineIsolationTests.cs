using System.Reflection;
using System.Xml.Linq;
using Agapanthe.Assets;

namespace Agapanthe.Tests;

/// <summary>
/// Contenu-2 — the permanent guard that <c>Agapanthe.Assets.Pipeline</c> (glTF parser + tangent gen +
/// StbImageSharp-backed image decode) never becomes a dependency of a shipped project. The runtime reads cooked
/// <c>.agmodel</c> blobs; it must not carry the glTF/JSON parser or the cook-side surface in its AOT closure.
/// <para>
/// Two assertions, MP-0a pattern: the static one reads every project file (the one that fails at the right
/// commit — the C# compiler elides an unused <c>ProjectReference</c>), the dynamic one walks the built
/// <c>Agapanthe.Assets</c> assembly.
/// </para>
/// </summary>
public sealed class AssetsPipelineIsolationTests
{
    // Referencing Agapanthe.Assets.Pipeline OR tools/AssetCooker drags the glTF parser + StbImageSharp into a
    // project's closure. AssetCooker is on the forbidden list too: referencing it would pull Pipeline transitively
    // — the exact elision MP-0a's mutation test proved a build graph rots by.
    private static readonly string[] CookSideAssemblies = ["Agapanthe.Assets.Pipeline", "AssetCooker"];

    // Only these may reference the cook-side: the cook tool itself and this test project.
    private static readonly string[] AllowedToReferenceCookSide = ["AssetCooker", "Agapanthe.Tests"];

    [Fact]
    public void OnlyTheCookerAndTestsReferenceTheCookSide()
    {
        var offenders = new List<string>();
        foreach (var csproj in Directory.EnumerateFiles(RepositoryRoot(), "*.csproj", SearchOption.AllDirectories))
        {
            if (csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || csproj.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(csproj);
            var referenced = XDocument.Load(csproj)
                .Descendants("ProjectReference")
                .Select(r => Path.GetFileNameWithoutExtension((string?)r.Attribute("Include")) ?? string.Empty)
                .ToArray();

            if (referenced.Any(CookSideAssemblies.Contains) && !AllowedToReferenceCookSide.Contains(name))
            {
                offenders.Add(name);
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"These projects reference the cook-side ({string.Join(" / ", CookSideAssemblies)}) but must not: "
            + $"{string.Join(", ", offenders)}. Only {string.Join(", ", AllowedToReferenceCookSide)} may.");
    }

    [Fact]
    public void AgapantheAssetsDoesNotDeclareAPipelineProjectReference()
    {
        var csproj = Path.Combine(RepositoryRoot(), "src", "Agapanthe.Assets", "Agapanthe.Assets.csproj");
        var refs = XDocument.Load(csproj)
            .Descendants("ProjectReference")
            .Select(r => Path.GetFileNameWithoutExtension((string?)r.Attribute("Include")) ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("Agapanthe.Assets.Pipeline", refs);
        Assert.Equal(new[] { "Agapanthe.Core" }, refs);
    }

    [Fact]
    public void TheGltfParserDoesNotResolveFromAgapantheAssets()
    {
        // The runtime asset assembly must not carry the glTF parser: after the W3 move, GltfLoader / the
        // Agapanthe.Assets.Gltf.* types live only in the Pipeline assembly.
        var assets = typeof(AgModelFormat).Assembly;
        Assert.Equal("Agapanthe.Assets", assets.GetName().Name);

        Assert.Null(assets.GetType("Agapanthe.Assets.GltfLoader", throwOnError: false));
        Assert.Null(assets.GetType("Agapanthe.Assets.Gltf.GltfDocument", throwOnError: false));
        Assert.Null(assets.GetType("Agapanthe.Assets.ImageLoader", throwOnError: false));
        Assert.Null(assets.GetType("Agapanthe.Assets.TangentGenerator", throwOnError: false));

        // And the Assets assembly does not pull the cook-side transitively.
        var referenced = assets.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("Agapanthe.Assets.Pipeline", referenced);
        Assert.DoesNotContain("AssetCooker", referenced);
        Assert.DoesNotContain("Tomlyn", referenced);
    }

    [Fact]
    public void OnlyTheCookSideDeclaresATomlynPackageReference()
    {
        // Contenu-3b — TOML authoring is cook-time only. Tomlyn must stay a PackageReference on
        // Agapanthe.Assets.Pipeline alone; the runtime reads the compiled .agscene blob, never TOML.
        var offenders = new List<string>();
        foreach (var csproj in Directory.EnumerateFiles(RepositoryRoot(), "*.csproj", SearchOption.AllDirectories))
        {
            if (csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || csproj.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(csproj);
            if (name == "Agapanthe.Assets.Pipeline")
            {
                continue;
            }

            var packages = XDocument.Load(csproj)
                .Descendants("PackageReference")
                .Select(r => (string?)r.Attribute("Include") ?? string.Empty);

            if (packages.Any(p => p.Equals("Tomlyn", StringComparison.OrdinalIgnoreCase)))
            {
                offenders.Add(name);
            }
        }

        Assert.True(offenders.Count == 0, $"Tomlyn must stay cook-side only; found on: {string.Join(", ", offenders)}");
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agapanthe.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
