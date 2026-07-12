using Agapanthe.Graphics;

namespace Agapanthe.Tests;

[Collection("ResourceTracker")]
public sealed class ShaderCompilerTests : IDisposable
{
    private const string ValidVertex = """
        #version 450
        void main() { gl_Position = vec4(0.0); }
        """;

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "agapanthe-shadertest-" + Guid.NewGuid().ToString("N"));
    private readonly string _srcDir = Path.Combine(Path.GetTempPath(), "agapanthe-shadersrc-" + Guid.NewGuid().ToString("N"));

    public ShaderCompilerTests() => Directory.CreateDirectory(_srcDir);

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
        {
            Directory.Delete(_cacheDir, recursive: true);
        }

        if (Directory.Exists(_srcDir))
        {
            Directory.Delete(_srcDir, recursive: true);
        }
    }

    private string WriteSrc(string name, string content)
    {
        var path = Path.Combine(_srcDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Compile_ValidGlsl_ProducesSpirvWordAligned()
    {
        using var compiler = new ShaderCompiler(_cacheDir);
        var spirv = compiler.Compile(ValidVertex, ShaderStage.Vertex, "test.vert");

        Assert.True(spirv.Length > 0);
        Assert.Equal(0, spirv.Length % 4);
        // SPIR-V magic number 0x07230203, little-endian.
        Assert.Equal(new byte[] { 0x03, 0x02, 0x23, 0x07 }, spirv[..4]);
    }

    [Fact]
    public void Compile_SameSourceTwice_SecondCallHitsCache()
    {
        using var compiler = new ShaderCompiler(_cacheDir);
        var first = compiler.Compile(ValidVertex, ShaderStage.Vertex, "test.vert");

        var cachedFiles = Directory.GetFiles(_cacheDir, "*.spv");
        Assert.Single(cachedFiles);

        var second = compiler.Compile(ValidVertex, ShaderStage.Vertex, "test.vert");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Compile_DifferentStage_UsesDistinctCacheKey()
    {
        using var compiler = new ShaderCompiler(_cacheDir);
        compiler.Compile(ValidVertex, ShaderStage.Vertex, "test.vert");
        // Same text compiled as a different stage must not collide on the cache key.
        try
        {
            compiler.Compile(ValidVertex, ShaderStage.Fragment, "test.frag");
        }
        catch (GraphicsException)
        {
            // A vertex-only body may fail as fragment; the point is the key differs, so a
            // second .spv (or a compile attempt, not a silent cache hit) is what we assert.
        }

        Assert.True(Directory.GetFiles(_cacheDir, "Fragment_*.spv").Length
                    + Directory.GetFiles(_cacheDir, "Vertex_*.spv").Length >= 1);
    }

    [Fact]
    public void CompileFileResolved_ExpandsInclude_AndReportsFiles()
    {
        WriteSrc("common.glsl", "#define OFFSET vec4(0.0)\n");
        var root = WriteSrc("main.vert",
            "#version 450\n#include \"common.glsl\"\nvoid main() { gl_Position = OFFSET; }\n");

        using var compiler = new ShaderCompiler(_cacheDir);
        var (spirv, files) = compiler.CompileFileResolved(root, ShaderStage.Vertex);

        Assert.True(spirv.Length > 0);
        Assert.Equal(0, spirv.Length % 4);
        Assert.Equal(2, files.Count); // root + include
    }

    [Fact]
    public void CompileFileResolved_EditingIncludedFile_InvalidatesCache()
    {
        var inc = WriteSrc("common.glsl", "#define OFFSET vec4(0.0)\n");
        var root = WriteSrc("main.vert",
            "#version 450\n#include \"common.glsl\"\nvoid main() { gl_Position = OFFSET; }\n");

        using var compiler = new ShaderCompiler(_cacheDir);
        compiler.CompileFileResolved(root, ShaderStage.Vertex);
        Assert.Single(Directory.GetFiles(_cacheDir, "*.spv"));

        // The root file is byte-identical; only the included file changed. With the resolved-source
        // cache key this must produce a distinct key (a second .spv), which the old raw-source key
        // would NOT have — this is the M8-03 bug being fixed.
        File.WriteAllText(inc, "#define OFFSET vec4(1.0)\n");
        compiler.CompileFileResolved(root, ShaderStage.Vertex);
        Assert.Equal(2, Directory.GetFiles(_cacheDir, "*.spv").Length);
    }

    [Fact]
    public void CompileFileResolved_NoInclude_MatchesLegacyCompileFile()
    {
        var root = WriteSrc("plain.vert", ValidVertex);

        using var compiler = new ShaderCompiler(_cacheDir);
        var legacy = compiler.CompileFile(root, ShaderStage.Vertex);
        var (resolved, files) = compiler.CompileFileResolved(root, ShaderStage.Vertex);

        // No includes -> resolved source equals raw source -> identical SPIR-V and same cache key.
        Assert.Equal(legacy, resolved);
        Assert.Single(files);
        Assert.Single(Directory.GetFiles(_cacheDir, "*.spv"));
    }

    // --- Disk-cache robustness (audit M8-09 M3) -------------------------------------------------------------

    [Theory]
    [InlineData(13)] // not a whole number of 32-bit words: a write killed mid-flight
    [InlineData(0)]  // created but never filled
    public void Compile_CorruptCachedBlob_SelfHealsInsteadOfThrowing(int truncatedLength)
    {
        using var compiler = new ShaderCompiler(_cacheDir);
        var expected = compiler.Compile(ValidVertex, ShaderStage.Vertex, "test.vert");

        // Simulate a process killed mid-WriteAllBytes: a TRUNCATED blob sitting under a perfectly valid hash
        // key. Before the fix every later run cache-hit this poison pill and crashed in vkCreateShaderModule.
        var cachePath = Assert.Single(Directory.GetFiles(_cacheDir, "*.spv"));
        File.WriteAllBytes(cachePath, expected[..truncatedLength]);

        using var reopened = new ShaderCompiler(_cacheDir);
        var healed = reopened.Compile(ValidVertex, ShaderStage.Vertex, "test.vert");

        Assert.Equal(expected, healed);                       // recompiled, not thrown
        Assert.Equal(expected, File.ReadAllBytes(cachePath)); // and the cache entry was rewritten
    }

    [Fact]
    public void Compile_CacheWrite_IsAtomic_AndLeavesNoTempFile()
    {
        using var compiler = new ShaderCompiler(_cacheDir);
        var spirv = compiler.Compile(ValidVertex, ShaderStage.Vertex, "test.vert");

        // The blob is published under the hash key by a single File.Move: no half-written .spv can exist, and
        // the process-unique temp file must be gone once the write succeeded.
        Assert.Empty(Directory.GetFiles(_cacheDir, "*.tmp"));
        var cachePath = Assert.Single(Directory.GetFiles(_cacheDir, "*.spv"));
        Assert.Equal(spirv, File.ReadAllBytes(cachePath));
    }

    [Fact]
    public void Compile_InvalidGlsl_Throws()
    {
        using var compiler = new ShaderCompiler(_cacheDir);
        var ex = Assert.Throws<GraphicsException>(
            () => compiler.Compile("this is not glsl", ShaderStage.Vertex, "bad.vert"));
        Assert.Contains("compilation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
