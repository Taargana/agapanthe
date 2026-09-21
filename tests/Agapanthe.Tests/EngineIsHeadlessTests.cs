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
    // Contenu-3b: the GPU-free scene runtime. {Core, World, Assets} exactly — no Engine (it returns
    // PhysicsSettings, not a PhysicsSystem), no Rendering, no Graphics.
    [InlineData(
        "src/Agapanthe.Scene/Agapanthe.Scene.csproj", "Agapanthe.Assets", "Agapanthe.Core", "Agapanthe.World")]
    // The milestone's headline artifact. Its own csproj comment promises this file is guarded — so guard it.
    [InlineData(
        "samples/HeadlessSim/HeadlessSim.csproj",
        "Agapanthe.Core", "Agapanthe.Engine", "Agapanthe.Scene", "Agapanthe.World")]
    // Agapanthe.App milestone: the composition-root layer may reference the whole engine — EXCEPT Agapanthe.Platform.
    // App→Platform would drag Rendering/Graphics/Silk.NET.Vulkan transitively into Platform, a Vulkan-free leaf;
    // the concrete window is adapted in Agapanthe.Platform.App (EngineWindowAdapter, moved there in Slice-2 — it
    // used to live in samples/Sandbox), not named here. The MSBuild cycle only catches half of this (a re-added
    // ProjectReference the compiler elides stays green until first use) — the same one-commit blind spot MP-0a's
    // static allowlist exists to close.
    [InlineData(
        "src/Agapanthe.App/Agapanthe.App.csproj",
        "Agapanthe.Assets", "Agapanthe.Core", "Agapanthe.Engine", "Agapanthe.Engine.Render",
        "Agapanthe.Graphics", "Agapanthe.Rendering", "Agapanthe.Scene", "Agapanthe.Ui", "Agapanthe.World")]
    // Slice-2 (audit finding, both csharp-lowlevel and engine-architect): the one project a Platform reference AND
    // an App reference can meet without pulling Vulkan into App itself — its entire reason to exist is being that
    // single meeting point, so it needs the same static gate every other structural project got at its own
    // milestone (Agapanthe.Scene at Contenu-3b, Agapanthe.App at S30).
    [InlineData(
        "src/Agapanthe.Platform.App/Agapanthe.Platform.App.csproj",
        "Agapanthe.App", "Agapanthe.Assets", "Agapanthe.Core", "Agapanthe.Engine",
        "Agapanthe.Platform", "Agapanthe.Scene", "Agapanthe.World")]
    // Net-1: the network transport + wire codec lives in the SAME headless closure as Agapanthe.Engine — a
    // dedicated server links this with no GPU on the machine. Not World: nothing here names a World/Arch type
    // (World arrives transitively via Engine for any consumer that needs it, e.g. DedicatedServer itself) — an
    // audit finding caught the ProjectReference as dead, which the static allowlist would otherwise have made
    // MANDATORY forever (removing an unused reference would fail this very test). LiteNetLib is a
    // PackageReference (plain sockets, no Vulkan/GLFW); NetProjectFile_CarriesOnlyTheAllowedPackageReference
    // below is what actually constrains it — this Theory only sees ProjectReferences.
    [InlineData(
        "src/Agapanthe.Net/Agapanthe.Net.csproj",
        "Agapanthe.Core", "Agapanthe.Engine")]
    // Net-1: the seed of a real dedicated server (mirrors HeadlessSim's own entry above) — GameWorld +
    // SimulationHost + Agapanthe.Net, no window, no Vulkan device, no cooked content (it never resolves an
    // AssetKey to a GPU handle, only names one over the wire).
    [InlineData(
        "samples/DedicatedServer/DedicatedServer.csproj",
        "Agapanthe.Core", "Agapanthe.Engine", "Agapanthe.Net", "Agapanthe.World")]
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
        => AssertClosureIsGpuFree(typeof(SystemScheduler).Assembly, "Agapanthe.Engine");

    /// <summary>
    /// Net-1 (audit finding, engine-architect): the static allowlist above proves <c>Agapanthe.Net</c>'s
    /// <c>ProjectReference</c> set is exactly <c>{Core, Engine}</c>, but — MP-0a's own documented lesson, proven
    /// by mutation — static and closure are not redundant. Without this, a future <c>PackageReference</c> on
    /// <c>Agapanthe.Net</c> pulling something GPU-bound transitively into the dedicated server's closure would
    /// go undetected: only <c>Agapanthe.Engine</c> had a closure walk rooted on it before this test existed.
    /// </summary>
    [Fact]
    public void NetAssemblyClosure_ContainsNoGpuAssembly()
        => AssertClosureIsGpuFree(typeof(Agapanthe.Net.PacketCodec).Assembly, "Agapanthe.Net");

    private static void AssertClosureIsGpuFree(Assembly root, string label)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<string>();
        Walk(root, seen, unresolved);

        var offenders = seen
            .Where(n => ForbiddenAssemblies.Contains(n, StringComparer.Ordinal)
                || n.StartsWith("Silk.NET", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{label} must stay headless but its closure contains: {string.Join(", ", offenders)}. "
            + "It has to build and run on a machine with no Vulkan.");

        // A closure this test could not fully walk is a closure it cannot vouch for. Say so rather than pass on a
        // partial answer: a silently truncated walk is exactly how this kind of gate goes quietly green forever.
        Assert.True(
            unresolved.Count == 0,
            $"Could not load assemblies referenced by {label}, so its closure is unverified: {string.Join(", ", unresolved)}.");
    }

    /// <summary>
    /// Net-1 (audit finding, engine-architect): <c>Agapanthe.Net</c> is the first project ever allowed a
    /// <c>PackageReference</c> inside a headless closure (<see cref="EngineProjectFile_CarriesNoPackageReference"/>
    /// deliberately exempts it), but nothing constrained WHICH package — a future addition could pull anything,
    /// including something GPU-bound, with no gate noticing until <see cref="NetAssemblyClosure_ContainsNoGpuAssembly"/>
    /// happened to catch its transitive closure. This pins the allowlist to exactly what D3 approved.
    /// </summary>
    [Fact]
    public void NetProjectFile_CarriesOnlyTheAllowedPackageReference()
    {
        var csproj = Path.Combine(RepositoryRoot(), "src", "Agapanthe.Net", "Agapanthe.Net.csproj");
        var packages = XDocument.Load(csproj)
            .Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? "<no Include>")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "LiteNetLib" }, packages);
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

    /// <summary>Contenu-3a: <see cref="Agapanthe.App.SimSceneContext"/> is the "headless-safe" half of the scene
    /// contract, but it lives in <c>Agapanthe.App</c> (which legitimately references Graphics/Rendering), so the
    /// closure/allowlist checks above cannot guard it. This does: every member type it names — public AND internal,
    /// properties + method params/returns — must be GPU/window-free, so a future commit cannot quietly add a
    /// <c>GraphicsDevice</c> property and keep the split in name only.</summary>
    [Fact]
    public void SimSceneContext_NamesNoGpuOrWindowType()
    {
        var t = typeof(Agapanthe.App.SimSceneContext);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var prop in t.GetProperties(flags))
        {
            AssertAllowed(prop.PropertyType, $"SimSceneContext.{prop.Name}");
        }

        foreach (var method in t.GetMethods(flags))
        {
            AssertAllowed(method.ReturnType, $"SimSceneContext.{method.Name} return");
            foreach (var p in method.GetParameters())
            {
                AssertAllowed(p.ParameterType, $"SimSceneContext.{method.Name} param '{p.Name}'");
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
