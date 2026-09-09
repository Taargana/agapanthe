using System.Security.Cryptography;
using System.Text;
using Agapanthe.Assets;
using Agapanthe.Core;

namespace Agapanthe.Assets.Pipeline;

/// <summary>How a cook run went.</summary>
public readonly record struct CookSummary(int Cooked, int Skipped, long TotalBlobBytes)
{
    public int Total => Cooked + Skipped;
}

/// <summary>
/// The offline asset cook (Contenu-2): globs source models under a content root, cooks each into a
/// <c>.agmodel</c> blob, and writes the <c>content.agmanifest</c> that <see cref="AssetCatalog"/> reads. Same
/// stance as <c>tools/FontCooker</c> — a dev-machine step, invoked by an MSBuild target via <c>dotnet exec</c>.
/// <para>
/// <b>Incremental by content hash</b>: a source whose bytes and the cooker version match the last run (recorded
/// in a non-shipped <c>.cookstate</c> sidecar) is skipped if its blob still exists. The MSBuild stamp on top is
/// mtime-based and coarse; this layer is the fine one — an mtime touch with unchanged bytes re-runs the cooker
/// but re-cooks nothing.
/// </para>
/// </summary>
public static class CookRunner
{
    /// <summary>Bump when the <c>.agmodel</c> payload layout or the glTF decode changes — every blob then
    /// re-cooks on the next run regardless of source hashes.</summary>
    public const string CookerVersion = "contenu2-1";

    private const string StateFileName = ".cookstate";

    /// <summary>Cooks every <c>*.glb</c> under <paramref name="contentRoot"/> into <paramref name="outputDir"/>
    /// (blobs + <c>content.agmanifest</c>). Deterministic: sources are processed in ordinal path order and the
    /// manifest is sorted ordinal by key.</summary>
    /// <exception cref="AssetException">A source is malformed, or two sources map to one key.</exception>
    public static CookSummary Cook(string contentRoot, string outputDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);

        if (!Directory.Exists(contentRoot))
        {
            throw new AssetException($"content root '{contentRoot}' does not exist.");
        }

        Directory.CreateDirectory(outputDir);

        // Only self-contained .glb are cooked. A .gltf with external .bin/image siblings would need per-dependency
        // hash tracking for the incremental skip to be correct (Contenu-2b + a GltfDocument URI-enumeration API) —
        // globbing it now would let a sibling edit ship a stale blob silently, the exact bug class this defers to
        // avoid. A .gltf dropped under content/ is simply skipped (visible, not silent).
        var sources = Directory
            .EnumerateFiles(contentRoot, "*.glb", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var state = ReadState(Path.Combine(outputDir, StateFileName));
        var freshState = new List<string>();
        var entries = new List<ManifestEntry>(sources.Count);
        var byKey = new Dictionary<AssetKey, string>();
        var cooked = 0;
        var skipped = 0;
        var totalBlobBytes = 0L;

        foreach (var source in sources)
        {
            var key = AssetKey.FromContentPath(contentRoot, source);
            if (!byKey.TryAdd(key, source))
            {
                throw new AssetException(
                    $"two sources map to key '{key}': '{byKey[key]}' and '{source}'.");
            }

            var sourceHash = HashHex(File.ReadAllBytes(source));
            var blobRelative = key.Value + ".agmodel";
            var blobFull = Path.Combine(outputDir, blobRelative);

            var upToDate = state.CookerVersion == CookerVersion
                           && state.SourceHashes.TryGetValue(key, out var previousHash)
                           && previousHash == sourceHash
                           && File.Exists(blobFull);

            if (upToDate)
            {
                skipped++;
            }
            else
            {
                var model = GltfLoader.Load(source);
                AgModelWriter.WriteFile(blobFull, model);
                cooked++;
            }

            var blobBytes = File.ReadAllBytes(blobFull);
            entries.Add(new ManifestEntry(key, AssetKind.Model, blobRelative, SHA256.HashData(blobBytes)));
            freshState.Add($"{key.Value}\t{sourceHash}");
            totalBlobBytes += blobBytes.LongLength;
        }

        PruneOrphanBlobs(outputDir, entries);
        ContentManifestWriter.WriteFile(Path.Combine(outputDir, AssetCatalog.ManifestFileName), entries);
        WriteState(Path.Combine(outputDir, StateFileName), freshState);

        return new CookSummary(cooked, skipped, totalBlobBytes);
    }

    // A source that was deleted or renamed leaves its .agmodel behind; IncludeCookedAssets would ship dead content
    // forever. The cooker knows the exact expected set — anything else under outputDir is removed.
    private static void PruneOrphanBlobs(string outputDir, List<ManifestEntry> entries)
    {
        var expected = new HashSet<string>(
            entries.Select(e => Path.GetFullPath(Path.Combine(outputDir, e.BlobPath))),
            StringComparer.OrdinalIgnoreCase);

        foreach (var blob in Directory.EnumerateFiles(outputDir, "*.agmodel", SearchOption.AllDirectories))
        {
            if (!expected.Contains(Path.GetFullPath(blob)))
            {
                File.Delete(blob);
            }
        }
    }

    private readonly record struct CookState(string CookerVersion, IReadOnlyDictionary<AssetKey, string> SourceHashes);

    private static CookState ReadState(string path)
    {
        var hashes = new Dictionary<AssetKey, string>();
        if (!File.Exists(path))
        {
            return new CookState(string.Empty, hashes);
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || !lines[0].StartsWith("agcookstate\t", StringComparison.Ordinal))
        {
            return new CookState(string.Empty, hashes);
        }

        var version = lines[0]["agcookstate\t".Length..];
        for (var i = 1; i < lines.Length; i++)
        {
            var tab = lines[i].IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0)
            {
                continue;
            }

            try
            {
                hashes[new AssetKey(lines[i][..tab])] = lines[i][(tab + 1)..];
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                // A hand-corrupted state line — ignore it, that entry just re-cooks.
            }
        }

        return new CookState(version, hashes);
    }

    private static void WriteState(string path, List<string> lines)
    {
        var sb = new StringBuilder();
        sb.Append("agcookstate\t").Append(CookerVersion).Append('\n');
        foreach (var line in lines)
        {
            sb.Append(line).Append('\n');
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, sb.ToString());
        File.Move(temp, path, overwrite: true);
    }

    private static string HashHex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
