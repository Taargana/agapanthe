using Agapanthe.Assets.Model;
using Agapanthe.Core;

namespace Agapanthe.Assets;

/// <summary>
/// The runtime view of cooked content (Contenu-2): reads a <c>content.agmanifest</c> and turns an
/// <see cref="AssetKey"/> into a fully-decoded <see cref="ModelAsset"/> — no glTF parsing, no image decode, no
/// tangent generation at runtime. GPU-free; the host (<c>AppHost</c>) owns one and hands it to a scene recipe.
/// <para>
/// No cache: <c>ResourceRegistry.Load</c> is punctual and the GPU resources it mints are already the cache, so a
/// re-load re-reads the blob (matching the pre-cook <c>GltfLoader.Load</c> behaviour exactly). Not
/// <see cref="IDisposable"/> — it holds the parsed manifest and nothing else.
/// </para>
/// </summary>
public sealed class AssetCatalog
{
    /// <summary>The manifest file name, at the root of a content directory.</summary>
    public const string ManifestFileName = "content.agmanifest";

    private readonly string _contentRoot;
    private readonly IReadOnlyDictionary<AssetKey, ManifestEntry> _entries;
    private readonly AssetKey[] _keys;

    private AssetCatalog(string contentRoot, IReadOnlyDictionary<AssetKey, ManifestEntry> entries)
    {
        _contentRoot = contentRoot;
        _entries = entries;
        _keys = [.. entries.Keys.OrderBy(k => k.Value, StringComparer.Ordinal)]; // stable, ordinal — Contenu-3 may rely on it
    }

    /// <summary>A catalog with no content — every <see cref="LoadModel"/> fails with the "run a build" message.
    /// A host uses this when there is no manifest, so a fully-procedural scene still runs.</summary>
    public static AssetCatalog Empty { get; } =
        new("<none>", new Dictionary<AssetKey, ManifestEntry>());

    /// <summary>Opens the catalog rooted at <paramref name="contentRoot"/> (which must contain
    /// <see cref="ManifestFileName"/>).</summary>
    /// <exception cref="AssetException">No manifest at that root, or it is corrupt.</exception>
    public static AssetCatalog Open(string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        var manifestPath = Path.Combine(contentRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new AssetException(
                $"No cooked content at '{contentRoot}' (expected '{ManifestFileName}') — run a build.");
        }

        var entries = ContentManifest.Read(File.ReadAllBytes(manifestPath));
        return new AssetCatalog(contentRoot, entries);
    }

    /// <summary>Every key in the manifest — a Contenu-3 prerequisite and a cheap integrity sweep.</summary>
    public IReadOnlyCollection<AssetKey> Keys => _keys;

    /// <summary>True if <paramref name="key"/> names a cooked asset.</summary>
    public bool Contains(AssetKey key) => _entries.ContainsKey(key);

    /// <summary>Loads and decodes the model cooked under <paramref name="key"/>.</summary>
    /// <exception cref="AssetException"><paramref name="key"/> is not in the manifest, or names a non-model asset.</exception>
    /// <exception cref="AgModelException">The blob is missing or malformed.</exception>
    public ModelAsset LoadModel(AssetKey key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            throw new AssetException($"No asset '{key}' in the content manifest.");
        }

        if (entry.Kind != AssetKind.Model)
        {
            throw new AssetException($"Asset '{key}' is a {entry.Kind}, not a model.");
        }

        var blobPath = Path.Combine(_contentRoot, entry.BlobPath);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(blobPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AgModelException($"Cannot read the cooked blob for '{key}' at '{blobPath}'.", ex);
        }

        return AgModelFormat.Read(bytes);
    }
}
