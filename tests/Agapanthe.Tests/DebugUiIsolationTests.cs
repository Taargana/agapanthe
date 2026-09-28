using System.Xml.Linq;

namespace Agapanthe.Tests;

/// <summary>
/// ImGui debug-overlay spec, D9 — the permanent guard that <c>Hexa.NET.ImGui</c> (and its native <c>cimgui</c>)
/// never becomes a dependency of any project other than <c>Agapanthe.DebugUi</c> itself. Patron
/// <c>AssetsPipelineIsolationTests.OnlyTheCookSideDeclaresATomlynPackageReference</c> — scans every real
/// <c>.csproj</c> in the repo (not just the allowlisted few), so a future project accidentally adding the
/// package is caught here rather than only showing up as an unexpectedly large Master publish output.
/// </summary>
public sealed class DebugUiIsolationTests
{
    [Fact]
    public void OnlyDebugUiDeclaresAHexaNetImGuiPackageReference()
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
            if (name == "Agapanthe.DebugUi")
            {
                continue;
            }

            var packages = XDocument.Load(csproj)
                .Descendants("PackageReference")
                .Select(r => (string?)r.Attribute("Include") ?? string.Empty);

            if (packages.Any(p => p.StartsWith("Hexa.NET.ImGui", StringComparison.OrdinalIgnoreCase)))
            {
                offenders.Add(name);
            }
        }

        Assert.True(offenders.Count == 0, $"Hexa.NET.ImGui must stay on Agapanthe.DebugUi only; found on: {string.Join(", ", offenders)}");
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
