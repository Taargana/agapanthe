using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Agapanthe.Core;
using Silk.NET.Shaderc;

namespace Agapanthe.Graphics;

/// <summary>
/// Compiles GLSL to SPIR-V at runtime via shaderc, caching results on disk keyed by a
/// hash of the source. <see cref="CompileFileResolved"/> resolves <c>#include</c> directives
/// (via <see cref="ShaderIncludeResolver"/>) before hashing, so the cache key is the hash of
/// the fully expanded source — editing an included file therefore invalidates the cache
/// (spec §3.6/§4). The legacy <see cref="CompileFile"/> hashes raw file text and does not
/// expand includes; callers migrate to <see cref="CompileFileResolved"/> in M8-04.
/// </summary>
public sealed unsafe class ShaderCompiler : IDisposable
{
    // shaderc is loaded LAZILY (Phase 2 rule §2.1-2): a fully pre-cooked cache never triggers a compile, so
    // the native shaderc library is never loaded. Only the first real cache miss initializes it. _shaderc is
    // null until then; _shaderc != null implies _compiler is set (see EnsureShaderc's publish order).
    private readonly Lock _shadercLock = new();
    private Shaderc? _shaderc;
    private Compiler* _compiler;
    private readonly string _cacheDirectory;
    private bool _disposed;

    public ShaderCompiler(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(AppContext.BaseDirectory, ".shadercache");
        Directory.CreateDirectory(_cacheDirectory);
        ResourceTracker.Register(nameof(ShaderCompiler));
    }

    /// <summary>
    /// Lazily initializes shaderc on the first real compilation (cache miss). Double-checked under a lock so a
    /// warm cache never pays for it and the native library is never loaded when every shader is pre-cooked.
    /// <c>_compiler</c> is assigned before <c>_shaderc</c> is published, so a non-null <c>_shaderc</c> observed
    /// without the lock always implies a valid compiler.
    /// </summary>
    private void EnsureShaderc()
    {
        if (_shaderc is not null)
        {
            return;
        }

        lock (_shadercLock)
        {
            if (_shaderc is not null)
            {
                return;
            }

            var api = Shaderc.GetApi();
            var compiler = api.CompilerInitialize();
            if (compiler is null)
            {
                api.Dispose();
                throw new GraphicsException("Failed to initialize the shaderc compiler.");
            }

            _compiler = compiler;
            _shaderc = api; // publish last

            // Visible proof of the lazy path: this line appears only on a real cache miss. A fully pre-cooked
            // cache (Phase 2 rule §2.1-2) never logs it — shaderc is never loaded.
            Log.Info("ShaderCompiler: cache miss — loading shaderc for runtime GLSL compilation.");
        }
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
        if (TryReadCache(cachePath, out var cached))
        {
            return cached;
        }

        var spirv = CompileWithShaderc(source, stage, sourceName);
        WriteCache(cachePath, spirv);
        return spirv;
    }

    /// <summary>
    /// Reads a cached SPIR-V blob, <b>validating</b> it (audit M8-09 M3). A blob truncated by a killed process
    /// used to be a permanent poison pill: the hash key still matched, so every later run hit the cache and
    /// then crashed in <c>ShaderModule</c> / <c>vkCreateShaderModule</c> until the cache was deleted by hand.
    /// A blob that is empty, not a whole number of 32-bit words, or unreadable is now rejected (warn) and the
    /// caller simply recompiles and rewrites the entry — self-healing, never fatal.
    /// </summary>
    private static bool TryReadCache(string cachePath, out byte[] spirv)
    {
        spirv = [];
        if (!File.Exists(cachePath))
        {
            return false;
        }

        byte[] blob;
        try
        {
            blob = File.ReadAllBytes(cachePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"ShaderCompiler: cannot read cached SPIR-V '{cachePath}' ({ex.Message}); recompiling.");
            return false;
        }

        if (blob.Length == 0 || blob.Length % 4 != 0)
        {
            Log.Warn(
                $"ShaderCompiler: cached SPIR-V '{cachePath}' is corrupt ({blob.Length} bytes, not a non-empty " +
                "multiple of 4 — a truncated write); recompiling and rewriting it.");
            return false;
        }

        spirv = blob;
        return true;
    }

    /// <summary>
    /// Writes the cache entry <b>atomically</b> (audit M8-09 M3): the blob goes to a temporary file unique to
    /// this process (two Sandbox instances hot-reloading the same shader must not share it), then a single
    /// <c>File.Move(overwrite)</c> — atomic on NTFS and ext4 — publishes it under the hash key. A process killed
    /// mid-write therefore leaves a stray <c>.tmp</c>, never a truncated <c>.spv</c> under a valid key.
    /// A cache-write failure is a warning, never a compilation failure.
    /// </summary>
    private static void WriteCache(string cachePath, byte[] spirv)
    {
        var tempPath = $"{cachePath}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tempPath, spirv);
            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"ShaderCompiler: failed to write the SPIR-V cache entry '{cachePath}' ({ex.Message}).");
            // Best effort cleanup: a leftover .tmp is inert anyway (only the hash key is ever read back).
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Compiles a GLSL file on disk, using its path as the diagnostic name.</summary>
    /// <remarks>
    /// Legacy path: hashes the raw file text and does NOT expand <c>#include</c> directives.
    /// New callers should prefer <see cref="CompileFileResolved"/>.
    /// </remarks>
    public byte[] CompileFile(string path, ShaderStage stage)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Compile(File.ReadAllText(path), stage, Path.GetFileName(path));
    }

    /// <summary>
    /// Resolves <c>#include</c> directives in the file at <paramref name="path"/>, compiles the
    /// expanded source, and returns both the SPIR-V and the deduplicated absolute list of every
    /// file that contributed (root + includes) — the set a file watcher must observe. The disk
    /// cache is keyed by a hash of the <em>resolved</em> source, so editing an included file
    /// invalidates the cache. For a file with no includes the result is identical to
    /// <see cref="CompileFile"/>.
    /// </summary>
    public (byte[] Spirv, IReadOnlyList<string> Files) CompileFileResolved(string path, ShaderStage stage)
    {
        ArgumentNullException.ThrowIfNull(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var resolved = ShaderIncludeResolver.Resolve(path);
        var spirv = Compile(resolved.Source, stage, Path.GetFileName(path));
        return (spirv, resolved.Files);
    }

    private byte[] CompileWithShaderc(string source, ShaderStage stage, string sourceName)
    {
        EnsureShaderc();
        var sc = _shaderc!; // EnsureShaderc guarantees non-null (and _compiler set)

        var kind = stage switch
        {
            ShaderStage.Vertex => ShaderKind.VertexShader,
            ShaderStage.Fragment => ShaderKind.FragmentShader,
            ShaderStage.Compute => ShaderKind.ComputeShader,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown shader stage."),
        };

        var options = sc.CompileOptionsInitialize();
        if (options is null)
        {
            throw new GraphicsException($"shaderc failed to allocate compile options for '{sourceName}'.");
        }

        byte* sourcePtr = null;
        byte* namePtr = null;
        byte* entryPtr = null;
        CompilationResult* result = null;
        var sourceLength = 0;
        try
        {
            // Allocated inside the try so a mid-way OOM still hits the finally and frees the rest.
            sourcePtr = (byte*)Marshal_StringToUtf8(source, out sourceLength);
            namePtr = (byte*)Marshal_StringToUtf8(sourceName, out _);
            entryPtr = (byte*)Marshal_StringToUtf8("main", out _);

            sc.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
            sc.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);

            result = sc.CompileIntoSpv(
                _compiler, sourcePtr, (nuint)sourceLength, kind, namePtr, entryPtr, options);
            if (result is null)
            {
                throw new GraphicsException($"shaderc returned no result for '{sourceName}'.");
            }

            var status = sc.ResultGetCompilationStatus(result);
            if (status != CompilationStatus.Success)
            {
                var error = SilkStringOrEmpty(sc.ResultGetErrorMessage(result));
                throw new GraphicsException($"Shader compilation failed for '{sourceName}' ({status}):\n{error}");
            }

            var length = (int)sc.ResultGetLength(result);
            var bytesPtr = sc.ResultGetBytes(result);
            var spirv = new byte[length];
            new ReadOnlySpan<byte>(bytesPtr, length).CopyTo(spirv);
            return spirv;
        }
        finally
        {
            if (result is not null)
            {
                sc.ResultRelease(result);
            }

            sc.CompileOptionsRelease(options);
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

        // shaderc may never have been initialized (a fully pre-cooked cache never compiles): only release it
        // if EnsureShaderc actually ran.
        if (_shaderc is not null)
        {
            if (_compiler is not null)
            {
                _shaderc.CompilerRelease(_compiler);
            }

            _shaderc.Dispose();
        }

        ResourceTracker.Unregister(nameof(ShaderCompiler));
        GC.SuppressFinalize(this);
    }
}
