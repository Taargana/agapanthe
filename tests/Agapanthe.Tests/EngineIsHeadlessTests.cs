using System.Reflection;
using System.Xml.Linq;
using Agapanthe.Engine;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0a — the permanent guard that <c>Agapanthe.Engine</c> stays runnable with no GPU.
/// <para>
/// The engine cap (backlog §4quater) rests on "topology is a deployment choice, never an architecture choice": the
/// same simulation code runs on a client and on a dedicated server, and only authority differs. That is a property
/// of the BUILD GRAPH, and build graphs rot by accident — someone needs a draw count in a gameplay system, adds a
/// ProjectReference, and the dedicated server needs Vulkan installed. Nothing else in the suite would notice.
/// </para>
/// <para>
/// <b>Two assertions, because one is not enough.</b> The static one reads the project file; the dynamic one walks
/// the built assembly. See <see cref="EngineProjectFile_ReferencesOnlyCoreAndWorld"/> for why the static check is
/// the one that bites in time.
/// </para>
/// </summary>
public sealed class EngineIsHeadlessTests
{
    private static readonly string[] ForbiddenAssemblies =
    [
        "Agapanthe.Graphics",   // Vulkan
        "Agapanthe.Rendering",  // owns the Renderer, hence Graphics
        "Agapanthe.Platform",   // GLFW: a server has no window
        "Agapanthe.Engine.Render",
    ];

    /// <summary>
    /// The project file declares exactly <c>{Agapanthe.Core, Agapanthe.World}</c> and nothing else.
    /// <para>
    /// <b>This is the assertion that fails at the right commit.</b> The C# compiler elides references that no type
    /// actually uses, so a re-added <c>ProjectReference</c> would leave the assembly closure clean until some later
    /// commit first used a type from it — and the failure would then be blamed on that innocent change instead of
    /// on the one that reopened the door. Reading the project file has no such blind spot.
    /// </para>
    /// </summary>
    /// <remarks>
    /// It is an <b>allowlist</b>, not a Vulkan blocklist, and that is the stronger statement: it also stops an
    /// audio, networking or editor assembly from quietly becoming a dependency of the simulation.
    /// </remarks>
    [Theory]
    // The simulation itself.
    [InlineData("src/Agapanthe.Engine/Agapanthe.Engine.csproj", "Agapanthe.Core", "Agapanthe.World")]
    // The deeper invariant: World is what makes Engine headless in the first place.
    [InlineData("src/Agapanthe.World/Agapanthe.World.csproj", "Agapanthe.Core")]
    // The milestone's headline artifact. Its own csproj comment promises this file is guarded — so guard it.
    [InlineData(
        "samples/HeadlessSim/HeadlessSim.csproj", "Agapanthe.Core", "Agapanthe.Engine", "Agapanthe.World")]
    public void ProjectFile_ReferencesExactlyTheAllowedProjects(string relativePath, params string[] allowed)
    {
        var csproj = Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(csproj), $"Project file not found at '{csproj}'.");

        var referenced = XDocument.Load(csproj)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(
                (e.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(allowed.OrderBy(n => n, StringComparer.Ordinal).ToArray(), referenced);
    }

    /// <summary>
    /// <c>Agapanthe.Engine</c> carries <b>no package reference at all</b>.
    /// <para>
    /// Its project file says "DO NOT ADD … OR ANY Silk.NET PACKAGE HERE", and until this test that sentence was
    /// only half enforced: the reference check above reads <c>ProjectReference</c> and would have let a
    /// <c>PackageReference</c> straight through — reopening the very one-commit blind spot the static check exists
    /// to close. Asserting "none at all" rather than "no Silk.NET" is both simpler and stronger; Arch reaches the
    /// simulation through World, with <c>PrivateAssets="compile"</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void EngineProjectFile_CarriesNoPackageReference()
    {
        var csproj = Path.Combine(RepositoryRoot(), "src", "Agapanthe.Engine", "Agapanthe.Engine.csproj");
        var packages = XDocument.Load(csproj)
            .Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? "<no Include>")
            .ToArray();

        Assert.True(
            packages.Length == 0,
            $"Agapanthe.Engine must carry no package reference; found: {string.Join(", ", packages)}.");
    }

    /// <summary>
    /// The built assembly's <b>recursive</b> reference closure contains nothing GPU-bound and no Silk.NET.
    /// <para>
    /// Complements the static check rather than replacing it: this one catches a forbidden dependency that arrives
    /// by some other route — a package reference, or a permitted project that itself grew a reference to Graphics.
    /// </para>
    /// </summary>
    [Fact]
    public void EngineAssemblyClosure_ContainsNoGpuAssembly()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<string>();
        Walk(typeof(SystemScheduler).Assembly, seen, unresolved);

        var offenders = seen
            .Where(n => ForbiddenAssemblies.Contains(n, StringComparer.Ordinal)
                || n.StartsWith("Silk.NET", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Agapanthe.Engine must stay headless but its closure contains: {string.Join(", ", offenders)}. "
            + "The simulation has to build and run on a machine with no Vulkan — see Agapanthe.Engine.csproj.");

        // A closure this test could not fully walk is a closure it cannot vouch for. Say so rather than pass on a
        // partial answer: a silently truncated walk is exactly how this kind of gate goes quietly green forever.
        Assert.True(
            unresolved.Count == 0,
            $"Could not load referenced assemblies, so the closure is unverified: {string.Join(", ", unresolved)}.");
    }

    /// <summary>The simulation half must not even be able to NAME a render type: a public surface mentioning one
    /// would drag the dependency back in the moment an application used that member.</summary>
    [Fact]
    public void EngineAssembly_ExposesNoTypeFromAForbiddenAssembly()
    {
        var engine = typeof(SystemScheduler).Assembly;

        foreach (var type in engine.GetExportedTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly))
            {
                AssertAllowed(method.ReturnType, $"{type.Name}.{method.Name} return type");
                foreach (var p in method.GetParameters())
                {
                    AssertAllowed(p.ParameterType, $"{type.Name}.{method.Name} parameter '{p.Name}'");
                }
            }
        }
    }

    private static void AssertAllowed(Type type, string where)
    {
        var owner = type.Assembly.GetName().Name ?? string.Empty;
        Assert.False(
            ForbiddenAssemblies.Contains(owner, StringComparer.Ordinal)
            || owner.StartsWith("Silk.NET", StringComparison.Ordinal),
            $"{where} is '{type.Name}' from '{owner}', which the headless engine must not name.");
    }

    private static void Walk(Assembly assembly, HashSet<string> seen, List<string> unresolved)
    {
        foreach (var reference in assembly.GetReferencedAssemblies())
        {
            var name = reference.Name ?? string.Empty;
            // The BCL is not interesting and is enormous: stop at the Agapanthe/Silk boundary.
            if (!name.StartsWith("Agapanthe", StringComparison.Ordinal)
                && !name.StartsWith("Silk.NET", StringComparison.Ordinal))
            {
                continue;
            }

            if (!seen.Add(name))
            {
                continue;
            }

            try
            {
                Walk(Assembly.Load(reference), seen, unresolved);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                unresolved.Add(name);
            }
        }
    }

    // The test binaries live several levels below the repository root; find it by the solution file rather than by
    // counting "..", which breaks the moment the target framework or configuration path changes.
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
