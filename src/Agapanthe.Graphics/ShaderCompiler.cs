using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Agapanthe.Core;
using Silk.NET.Shaderc;

namespace Agapanthe.Graphics;

/// <summary>
/// Compiles GLSL to SPIR-V at runtime via shaderc, caching results on disk keyed by
/// a hash of the source. Include resolution and watching included files is deferred to
/// milestone M8 (spec §3.6); the cache key currently hashes the raw source only.
/// </summary>
public sealed unsafe class ShaderCompiler : IDisposable
{
    private readonly Shaderc _shaderc;
    private readonly Compiler* _compiler;
    private readonly string _cacheDirectory;
    private bool _disposed;

    public ShaderCompiler(string? cacheDirectory = null)
    {
        _shaderc = Shaderc.GetApi();
        _compiler = _shaderc.CompilerInitialize();
        if (_compiler is null)
        {
            _shaderc.Dispose();
            throw new GraphicsException("Failed to initialize the shaderc compiler.");
        }

        _cacheDirectory = cacheDirectory ?? Path.Combine(AppContext.BaseDirectory, ".shadercache");
        Directory.CreateDirectory(_cacheDirectory);
        ResourceTracker.Register(nameof(ShaderCompiler));
    }

    ~ShaderCompiler() => ResourceTracker.ReportFinalizerLeak(nameof(ShaderCompiler));

    /// <summary>
    /// Compiles GLSL source to SPIR-V words. On a cache hit the cached blob is returned
    /// without invoking shaderc. <paramref name="sourceName"/> only labels diagnostics.
    /// </summary>
    public byte[] Compile(string source, ShaderStage stage, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cachePath = Path.Combine(_cacheDirectory, CacheKey(source, stage));
        if (File.Exists(cachePath))
        {
            return File.ReadAllBytes(cachePath);
        }

        var spirv = CompileWithShaderc(source, stage, sourceName);
        File.WriteAllBytes(cachePath, spirv);
        return spirv;
    }

    /// <summary>Compiles a GLSL file on disk, using its path as the diagnostic name.</summary>
    public byte[] CompileFile(string path, ShaderStage stage)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Compile(File.ReadAllText(path), stage, Path.GetFileName(path));
    }

    private byte[] CompileWithShaderc(string source, ShaderStage stage, string sourceName)
    {
        var kind = stage switch
        {
            ShaderStage.Vertex => ShaderKind.VertexShader,
            ShaderStage.Fragment => ShaderKind.FragmentShader,
            ShaderStage.Compute => ShaderKind.ComputeShader,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown shader stage."),
        };

        var options = _shaderc.CompileOptionsInitialize();
        var sourcePtr = (byte*)Marshal_StringToUtf8(source, out var sourceLength);
        var namePtr = (byte*)Marshal_StringToUtf8(sourceName, out _);
        var entryPtr = (byte*)Marshal_StringToUtf8("main", out _);
        CompilationResult* result = null;
        try
        {
            _shaderc.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
            _shaderc.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);

            result = _shaderc.CompileIntoSpv(
                _compiler, sourcePtr, (nuint)sourceLength, kind, namePtr, entryPtr, options);
            if (result is null)
            {
                throw new GraphicsException($"shaderc returned no result for '{sourceName}'.");
            }

            var status = _shaderc.ResultGetCompilationStatus(result);
            if (status != CompilationStatus.Success)
            {
                var error = SilkStringOrEmpty(_shaderc.ResultGetErrorMessage(result));
                throw new GraphicsException($"Shader compilation failed for '{sourceName}' ({status}):\n{error}");
            }

            var length = (int)_shaderc.ResultGetLength(result);
            var bytesPtr = _shaderc.ResultGetBytes(result);
            var spirv = new byte[length];
            new ReadOnlySpan<byte>(bytesPtr, length).CopyTo(spirv);
            return spirv;
        }
        finally
        {
            if (result is not null)
            {
                _shaderc.ResultRelease(result);
            }

            _shaderc.CompileOptionsRelease(options);
            NativeMemory.Free(sourcePtr);
            NativeMemory.Free(namePtr);
            NativeMemory.Free(entryPtr);
        }
    }

    private string CacheKey(string source, ShaderStage stage)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return $"{stage}_{Convert.ToHexString(hash)}.spv";
    }

    private static void* Marshal_StringToUtf8(string value, out int byteLength)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        var ptr = (byte*)NativeMemory.Alloc((nuint)(maxBytes + 1));
        var span = new Span<byte>(ptr, maxBytes + 1);
        byteLength = Encoding.UTF8.GetBytes(value, span);
        span[byteLength] = 0;
        return ptr;
    }

    private static string SilkStringOrEmpty(byte* utf8)
        => utf8 is null ? string.Empty : Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)utf8) ?? string.Empty;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_compiler is not null)
        {
            _shaderc.CompilerRelease(_compiler);
        }

        _shaderc.Dispose();
        ResourceTracker.Unregister(nameof(ShaderCompiler));
        GC.SuppressFinalize(this);
    }
}
