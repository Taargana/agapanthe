using System.Text.Json;

namespace Agapanthe.Assets.Gltf;

/// <summary>
/// The low-level glTF 2.0 loader (M4-04): the raw JSON schema, GLB container handling, and buffer
/// resolution, with nothing above it. It deliberately stops short of geometry extraction (M4-05) and
/// material/node mapping (M4-06); it exposes a clean, validated raw view those layers build on.
/// <para>
/// The typed JSON tree is available (internally) as <see cref="Root"/>; binary buffer payloads are
/// resolved on demand through <see cref="GetBufferData"/>, transparently across the three glTF
/// storage forms: a sibling <c>.bin</c> file, an inline <c>data:</c> URI, or the GLB BIN chunk.
/// </para>
/// </summary>
public sealed class GltfDocument
{
    /// <summary>Extensions this loader understands; anything else in <c>extensionsRequired</c> is a hard error.</summary>
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.Ordinal)
    {
        "KHR_materials_emissive_strength",
    };

    private const string InMemoryPath = "<in-memory glTF>";

    private readonly string _sourcePath;
    private readonly string? _baseDirectory;
    private readonly ReadOnlyMemory<byte> _glbBin;
    private readonly bool _hasGlbBin;

    // Lazily-filled, one slot per buffer, so repeated M4-05 accessor reads don't re-read files or
    // re-decode base64. ReadOnlyMemory is a struct, so a nullable box marks "not yet resolved".
    private readonly ReadOnlyMemory<byte>?[] _bufferCache;

    /// <summary>The raw, typed glTF JSON tree. Internal: it is the loader's contract with M4-05/06.</summary>
    internal GltfRoot Root { get; }

    /// <summary>Source path (or a placeholder for in-memory loads), as used in error messages.</summary>
    public string SourcePath => _sourcePath;

    private GltfDocument(GltfRoot root, string sourcePath, string? baseDirectory, ReadOnlyMemory<byte> glbBin, bool hasGlbBin)
    {
        Root = root;
        _sourcePath = sourcePath;
        _baseDirectory = baseDirectory;
        _glbBin = glbBin;
        _hasGlbBin = hasGlbBin;
        _bufferCache = new ReadOnlyMemory<byte>?[root.Buffers?.Length ?? 0];
    }

    /// <summary>
    /// Loads a glTF asset from disk. The container form (<c>.glb</c> vs text <c>.gltf</c>) is detected
    /// from the file's magic bytes, not its extension. Relative buffer/image URIs resolve against the
    /// file's directory.
    /// </summary>
    /// <exception cref="AssetException">The file is missing, malformed, or uses an out-of-subset feature.</exception>
    public static GltfDocument Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AssetException(path, $"cannot read glTF file: {ex.Message}", ex);
        }

        return LoadFromBytes(bytes, Path.GetDirectoryName(Path.GetFullPath(path)), path);
    }

    /// <summary>
    /// Loads a glTF asset from an in-memory buffer, detecting <c>.glb</c> vs <c>.gltf</c> by magic.
    /// </summary>
    /// <param name="data">The raw file bytes (kept alive by the returned document for zero-copy GLB BIN access).</param>
    /// <param name="baseDirectory">Directory used to resolve relative buffer/image URIs; null forbids relative file URIs.</param>
    /// <param name="sourcePath">Path used in diagnostics; defaults to a placeholder.</param>
    public static GltfDocument LoadFromBytes(ReadOnlyMemory<byte> data, string? baseDirectory = null, string? sourcePath = null)
    {
        string path = sourcePath ?? InMemoryPath;

        ReadOnlyMemory<byte> jsonBytes;
        ReadOnlyMemory<byte> glbBin = default;
        bool hasGlbBin = false;

        if (GlbContainer.LooksLikeGlb(data.Span))
        {
            GlbChunks chunks = GlbContainer.Parse(data, path);
            jsonBytes = chunks.Json;
            glbBin = chunks.Bin;
            hasGlbBin = chunks.HasBin;
        }
        else
        {
            jsonBytes = data;
        }

        GltfRoot root = DeserializeRoot(jsonBytes.Span, path);
        Validate(root, path);

        return new GltfDocument(root, path, baseDirectory, glbBin, hasGlbBin);
    }

    /// <summary>Loads a glTF asset from a stream. The stream is fully read into memory first.</summary>
    public static GltfDocument LoadFromStream(Stream stream, string? baseDirectory = null, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        // GetBuffer avoids a copy but may over-allocate; slice to the written length.
        return LoadFromBytes(ms.GetBuffer().AsMemory(0, (int)ms.Length), baseDirectory, sourcePath);
    }

    private static GltfRoot DeserializeRoot(ReadOnlySpan<byte> json, string sourcePath)
    {
        GltfRoot? root;
        try
        {
            root = JsonSerializer.Deserialize(json, GltfJsonContext.Default.GltfRoot);
        }
        catch (JsonException ex)
        {
            throw new AssetException(sourcePath, $"malformed glTF JSON: {ex.Message}", ex);
        }

        if (root is null)
        {
            throw new AssetException(sourcePath, "glTF JSON is empty or literal null.");
        }

        return root;
    }

    /// <summary>Structural validation that must hold before M4-05/06 touch the tree.</summary>
    private static void Validate(GltfRoot root, string sourcePath)
    {
        // asset.version — required by glTF, and we only speak 2.x.
        string? version = root.Asset?.Version;
        if (string.IsNullOrEmpty(version))
        {
            throw new AssetException(sourcePath, "missing required 'asset.version'.");
        }

        if (!version.StartsWith("2.", StringComparison.Ordinal) && version != "2")
        {
            throw new AssetException(sourcePath, $"unsupported glTF version '{version}' (expected 2.x).");
        }

        // extensionsRequired — anything outside our known set is fatal (spec: no silent fallback).
        if (root.ExtensionsRequired is { } required)
        {
            foreach (string ext in required)
            {
                if (!SupportedExtensions.Contains(ext))
                {
                    throw new AssetException(sourcePath, $"required glTF extension '{ext}' is not supported.");
                }
            }
        }

        // Primitive topology — M4-05 only accepts TRIANGLES (mode 4); reject the rest early and clearly.
        if (root.Meshes is { } meshes)
        {
            for (int m = 0; m < meshes.Length; m++)
            {
                GltfPrimitive[]? prims = meshes[m].Primitives;
                if (prims is null)
                {
                    continue;
                }

                for (int p = 0; p < prims.Length; p++)
                {
                    int mode = prims[p].Mode;
                    if (mode != 4)
                    {
                        throw new AssetException(sourcePath,
                            $"mesh {m} primitive {p} uses topology mode {mode}; only 4 (TRIANGLES) is supported.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resolves and returns the raw bytes of buffer <paramref name="bufferIndex"/>, sliced to its
    /// declared <c>byteLength</c>. Handles the three storage forms: relative <c>.bin</c> file (read
    /// from the base directory), inline <c>data:...;base64,...</c> URI, and the GLB BIN chunk for the
    /// URI-less buffer 0. Results are cached per buffer.
    /// </summary>
    /// <exception cref="AssetException">Index out of range, missing/short data, or an unsupported URI form.</exception>
    public ReadOnlyMemory<byte> GetBufferData(int bufferIndex)
    {
        GltfBuffer[]? buffers = Root.Buffers;
        if (buffers is null || (uint)bufferIndex >= (uint)buffers.Length)
        {
            int count = buffers?.Length ?? 0;
            throw new AssetException(_sourcePath, $"buffer index {bufferIndex} out of range (document has {count} buffer(s)).");
        }

        if (_bufferCache[bufferIndex] is { } cached)
        {
            return cached;
        }

        GltfBuffer buffer = buffers[bufferIndex];
        int byteLength = buffer.ByteLength;
        ReadOnlyMemory<byte> raw = ResolveBufferBytes(buffer, bufferIndex);

        if (raw.Length < byteLength)
        {
            throw new AssetException(_sourcePath,
                $"buffer {bufferIndex} is shorter than declared: {raw.Length} bytes available, byteLength is {byteLength}.");
        }

        ReadOnlyMemory<byte> result = raw.Length == byteLength ? raw : raw.Slice(0, byteLength);
        _bufferCache[bufferIndex] = result;
        return result;
    }

    private ReadOnlyMemory<byte> ResolveBufferBytes(GltfBuffer buffer, int bufferIndex)
    {
        string? uri = buffer.Uri;

        // No URI → the GLB embedded buffer, which must be buffer 0.
        if (string.IsNullOrEmpty(uri))
        {
            if (!_hasGlbBin)
            {
                throw new AssetException(_sourcePath, $"buffer {bufferIndex} has no URI and there is no GLB BIN chunk to satisfy it.");
            }

            if (bufferIndex != 0)
            {
                throw new AssetException(_sourcePath, $"only buffer 0 may use the GLB BIN chunk, but buffer {bufferIndex} has no URI.");
            }

            return _glbBin;
        }

        // data: URI (base64 only — the only encoding glTF permits for buffers).
        if (uri.StartsWith("data:", StringComparison.Ordinal))
        {
            return DecodeDataUri(uri, bufferIndex);
        }

        // Otherwise a relative file path, resolved against the base directory.
        if (_baseDirectory is null)
        {
            throw new AssetException(_sourcePath, $"buffer {bufferIndex} references file '{uri}' but no base directory is available to resolve it.");
        }

        string relative = Uri.UnescapeDataString(uri);
        string fullPath = Path.Combine(_baseDirectory, relative);
        if (!File.Exists(fullPath))
        {
            throw new AssetException(_sourcePath, $"buffer {bufferIndex} file not found: '{fullPath}'.");
        }

        try
        {
            return File.ReadAllBytes(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AssetException(_sourcePath, $"buffer {bufferIndex} file '{fullPath}' could not be read: {ex.Message}", ex);
        }
    }

    private ReadOnlyMemory<byte> DecodeDataUri(string uri, int bufferIndex)
    {
        const string marker = ";base64,";
        int idx = uri.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            throw new AssetException(_sourcePath, $"buffer {bufferIndex} uses an unsupported data URI (only ';base64,' payloads are supported).");
        }

        string payload = uri[(idx + marker.Length)..];
        try
        {
            return Convert.FromBase64String(payload);
        }
        catch (FormatException ex)
        {
            throw new AssetException(_sourcePath, $"buffer {bufferIndex} has an invalid base64 data URI payload.", ex);
        }
    }
}
