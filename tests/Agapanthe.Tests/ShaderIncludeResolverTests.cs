using Agapanthe.Graphics;

namespace Agapanthe.Tests;

public sealed class ShaderIncludeResolverTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "agapanthe-includetest-" + Guid.NewGuid().ToString("N"));

    public ShaderIncludeResolverTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Resolve_NoIncludes_ReturnsSourceVerbatim()
    {
        const string body = "#version 450\r\nvoid main() {}\r\n";
        var root = Write("plain.vert", body);

        var resolved = ShaderIncludeResolver.Resolve(root);

        // Byte-for-byte identical (incl. CRLF) so the no-include path stays SPIR-V-stable.
        Assert.Equal(body, resolved.Source);
        Assert.Single(resolved.Files);
        Assert.Equal(Path.GetFullPath(root), resolved.Files[0]);
    }

    [Fact]
    public void Resolve_SimpleInclude_InsertsContent()
    {
        Write("common.glsl", "const float K = 0.5;\n");
        var root = Write("main.vert", "#version 450\n#include \"common.glsl\"\nvoid main() {}\n");

        var resolved = ShaderIncludeResolver.Resolve(root);

        Assert.Contains("const float K = 0.5;", resolved.Source);
        Assert.DoesNotContain("#include", resolved.Source);
        // Directive replaced in place: version still precedes the injected content.
        Assert.True(resolved.Source.IndexOf("#version", StringComparison.Ordinal)
                    < resolved.Source.IndexOf("const float K", StringComparison.Ordinal));
        Assert.Equal(2, resolved.Files.Count);
    }

    [Fact]
    public void Resolve_NestedIncludes_ExpandsTransitivelyAndListsAllFiles()
    {
        Write("c.glsl", "int c() { return 3; }\n");
        Write("b.glsl", "#include \"c.glsl\"\nint b() { return 2; }\n");
        var root = Write("a.vert", "#version 450\n#include \"b.glsl\"\nint a() { return 1; }\n");

        var resolved = ShaderIncludeResolver.Resolve(root);

        Assert.Contains("int c()", resolved.Source);
        Assert.Contains("int b()", resolved.Source);
        Assert.Contains("int a()", resolved.Source);
        // c() (deepest) is emitted before b() (its includer).
        Assert.True(resolved.Source.IndexOf("int c()", StringComparison.Ordinal)
                    < resolved.Source.IndexOf("int b()", StringComparison.Ordinal));
        Assert.Equal(3, resolved.Files.Count);
        Assert.Contains(Path.GetFullPath(Path.Combine(_dir, "c.glsl")), resolved.Files);
    }

    [Fact]
    public void Resolve_RelativePathFromIncludingFile()
    {
        // b.glsl lives in a subfolder and includes a sibling; resolution is relative to b, not root.
        Write("sub/dep.glsl", "int dep() { return 7; }\n");
        Write("sub/b.glsl", "#include \"dep.glsl\"\n");
        var root = Write("main.vert", "#include \"sub/b.glsl\"\n");

        var resolved = ShaderIncludeResolver.Resolve(root);

        Assert.Contains("int dep()", resolved.Source);
        Assert.Equal(3, resolved.Files.Count);
    }

    [Fact]
    public void Resolve_DuplicateInclude_ExpandedTwiceButListedOnce()
    {
        Write("d.glsl", "// dupe\n");
        var root = Write("main.vert", "#include \"d.glsl\"\n#include \"d.glsl\"\n");

        var resolved = ShaderIncludeResolver.Resolve(root);

        // C text semantics: two directives -> two expansions.
        var count = resolved.Source.Split("// dupe").Length - 1;
        Assert.Equal(2, count);
        // But the watcher list is deduplicated.
        Assert.Equal(2, resolved.Files.Count);
    }

    [Fact]
    public void Resolve_Cycle_Throws()
    {
        Write("a.glsl", "#include \"b.glsl\"\n");
        Write("b.glsl", "#include \"a.glsl\"\n");
        var root = Write("a.glsl", "#include \"b.glsl\"\n");

        var ex = Assert.Throws<GraphicsException>(() => ShaderIncludeResolver.Resolve(root));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_MissingInclude_Throws()
    {
        var root = Write("main.vert", "#include \"does-not-exist.glsl\"\n");

        var ex = Assert.Throws<GraphicsException>(() => ShaderIncludeResolver.Resolve(root));
        Assert.Contains("does-not-exist.glsl", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_MissingRoot_Throws()
        => Assert.Throws<GraphicsException>(
            () => ShaderIncludeResolver.Resolve(Path.Combine(_dir, "nope.vert")));

    [Fact]
    public void Resolve_MalformedInclude_Throws()
    {
        var root = Write("main.vert", "#include <system.glsl>\n");
        Assert.Throws<GraphicsException>(() => ShaderIncludeResolver.Resolve(root));
    }

    [Fact]
    public void Resolve_EditingIncludeContent_ChangesResolvedSource()
    {
        var inc = Write("common.glsl", "#define VALUE 0.0\n");
        var root = Write("main.vert", "#version 450\n#include \"common.glsl\"\nvoid main() {}\n");

        var before = ShaderIncludeResolver.Resolve(root).Source;
        File.WriteAllText(inc, "#define VALUE 1.0\n");
        var after = ShaderIncludeResolver.Resolve(root).Source;

        // Root bytes are untouched, yet the resolved source differs -> the bug (raw-source
        // cache key) would have missed this edit; the resolved-source key catches it.
        Assert.NotEqual(before, after);
    }

    // --- Shared OS-aware path comparer (audit M8-09 M2) ------------------------------------------------------

    [Fact]
    public void PathComparer_IsCaseSensitive_ExactlyWhenTheFilesystemIs()
    {
        var differOnlyByCase = ShaderIncludeResolver.PathComparer.Equals(
            "/shaders/Common.glsl", "/shaders/common.glsl");

        // Windows: same file -> equal. Linux/macOS-with-case-sensitive-FS: two distinct files -> not equal.
        Assert.Equal(OperatingSystem.IsWindows(), differOnlyByCase);
    }

    [Fact]
    public void PathComparer_DedupsSourceFiles_ConsistentlyWithTheFileToPassMapping()
    {
        // The dedup ReloadablePass.CompileModules performs on the passes' resolved SourceFiles, and the
        // membership test Renderer.PollShaderReload performs on them, must use THIS comparer — otherwise on a
        // case-sensitive filesystem a legitimately distinct `Common.glsl` would be dropped from the watch set
        // by a hard-coded OrdinalIgnoreCase dedup while the mapping would still match it (the M2 finding).
        var files = new HashSet<string>(ShaderIncludeResolver.PathComparer)
        {
            "/shaders/Common.glsl",
            "/shaders/common.glsl",
        };

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, files.Count);
        Assert.Contains("/shaders/common.glsl", files, ShaderIncludeResolver.PathComparer);
    }

    [Fact]
    public void Resolve_SameIncludeFromTwoFiles_IsListedOnce()
    {
        Write("common.glsl", "#define VALUE 0.0\n");
        Write("b.glsl", "#include \"common.glsl\"\n");
        var root = Write("main.vert",
            "#version 450\n#include \"common.glsl\"\n#include \"b.glsl\"\nvoid main() {}\n");

        var resolved = ShaderIncludeResolver.Resolve(root);

        // Files is the watch set: deduplicated (root + common + b), first-seen order, root first.
        Assert.Equal(3, resolved.Files.Count);
        Assert.Equal(Path.GetFullPath(root), resolved.Files[0]);
        Assert.Equal(resolved.Files.Count, resolved.Files.Distinct(ShaderIncludeResolver.PathComparer).Count());
    }
}
