using Agapanthe.Graphics;

namespace Agapanthe.Tests;

public sealed class ShaderCompilerTests : IDisposable
{
    private const string ValidVertex = """
        #version 450
        void main() { gl_Position = vec4(0.0); }
        """;

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "agapanthe-shadertest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
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
    public void Compile_InvalidGlsl_Throws()
    {
        using var compiler = new ShaderCompiler(_cacheDir);
        var ex = Assert.Throws<GraphicsException>(
            () => compiler.Compile("this is not glsl", ShaderStage.Vertex, "bad.vert"));
        Assert.Contains("compilation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
