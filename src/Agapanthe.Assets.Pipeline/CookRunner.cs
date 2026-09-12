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
    public const string CookerVersion = "contenu3c-3"; // .agscene v4 (DriveControl system + optional probe fields)

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

        // Contenu-3c: cook every content/procedural/*.toml into an ordinary .agmodel blob (AssetKind.Model — a
        // scene references it exactly like a glTF-sourced model; SceneCompiler/CookScenes's LoadModel below does
        // not need to know where a model came from). Must run BEFORE CookScenes: a scene may reference a
        // procedural key, and CookScenes's LoadModel reads back the just-written blob by key. `cooked` keeps
        // accumulating across both model sources, so the `anyModelCooked: cooked > 0` flag CookScenes uses below
        // already covers "a procedural model was (re)cooked this run" with no separate tracking needed.
        CookProcedural(contentRoot, outputDir, state, freshState, entries, byKey, ref cooked, ref skipped, ref totalBlobBytes);

        // Contenu-3b: after the models (glTF + Contenu-3c procedural), cook every content/scenes/*.toml into a
        // flat .agscene blob. The scene compiler reads back the just-written .agmodel blobs (for bounds/
        // diagonals) — every model (of either origin) is done above.
        CookScenes(contentRoot, outputDir, state, freshState, entries, byKey, ref cooked, ref skipped, ref totalBlobBytes, anyModelCooked: cooked > 0);

        PruneOrphanBlobs(outputDir, entries);
        ContentManifestWriter.WriteFile(Path.Combine(outputDir, AssetCatalog.ManifestFileName), entries);
        WriteState(Path.Combine(outputDir, StateFileName), freshState);

        return new CookSummary(cooked, skipped, totalBlobBytes);
    }

    // Contenu-3c: cooks every content/procedural/*.toml into an ordinary .agmodel blob (AssetKind.Model).
    // Incrementality is simpler than CookScenes': a generator's output is a PURE function of its TOML text alone
    // (no source-model dependency to fold in — a generator never references another cooked asset).
    private static void CookProcedural(
        string contentRoot, string outputDir,
        CookState state, List<string> freshState, List<ManifestEntry> entries, Dictionary<AssetKey, string> byKey,
        ref int cooked, ref int skipped, ref long totalBlobBytes)
    {
        var proceduralDir = Path.Combine(contentRoot, "procedural");
        if (!Directory.Exists(proceduralDir))
        {
            return;
        }

        foreach (var tomlPath in Directory.EnumerateFiles(proceduralDir, "*.toml", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            var key = AssetKey.FromContentPath(contentRoot, tomlPath);
            // Strip the ".toml" extension so the key matches what a scene's `model = "procedural/xxx"` names —
            // same convention CookScenes uses for its own ".toml"-suffixed source keys.
            key = new AssetKey(StripTomlExtension(key.Value!, tomlPath));
            if (!byKey.TryAdd(key, tomlPath))
            {
                throw new AssetException($"two sources map to key '{key}': '{byKey[key]}' and '{tomlPath}'.");
            }

            var tomlHash = HashHex(File.ReadAllBytes(tomlPath));
            var blobRelative = key.Value + ".agmodel";
            var blobFull = Path.Combine(outputDir, blobRelative);

            var upToDate = state.CookerVersion == CookerVersion
                           && state.SourceHashes.TryGetValue(key, out var prev)
                           && prev == tomlHash
                           && File.Exists(blobFull);

            if (upToDate)
            {
                skipped++;
            }
            else
            {
                var model = Procedural.ProceduralTomlReader.Read(tomlPath);
                AgModelWriter.WriteFile(blobFull, model);
                cooked++;
            }

            var blobBytes = File.ReadAllBytes(blobFull);
            entries.Add(new ManifestEntry(key, AssetKind.Model, blobRelative, SHA256.HashData(blobBytes)));
            freshState.Add($"{key.Value}\t{tomlHash}");
            totalBlobBytes += blobBytes.LongLength;
        }
    }

    private static void CookScenes(
        string contentRoot, string outputDir,
        CookState state, List<string> freshState, List<ManifestEntry> entries, Dictionary<AssetKey, string> byKey,
        ref int cooked, ref int skipped, ref long totalBlobBytes, bool anyModelCooked)
    {
        var scenesDir = Path.Combine(contentRoot, "scenes");
        if (!Directory.Exists(scenesDir))
        {
            return;
        }

        var loadedModels = new Dictionary<AssetKey, Agapanthe.Assets.Model.ModelAsset>();
        Agapanthe.Assets.Model.ModelAsset LoadModel(AssetKey key)
        {
            if (loadedModels.TryGetValue(key, out var cachedM))
            {
                return cachedM;
            }

            var blob = Path.Combine(outputDir, key.Value + ".agmodel");
            if (!File.Exists(blob))
            {
                throw new AssetException($"a scene references model '{key}', but no cooked blob exists (add {key.Value} under content/).");
            }

            var m = Agapanthe.Assets.AgModelFormat.Read(File.ReadAllBytes(blob));
            loadedModels[key] = m;
            return m;
        }

        var loadedPrefabs = new Dictionary<string, Scene.AuthoredPrefab>(StringComparer.Ordinal);
        Scene.AuthoredPrefab LoadPrefab(string name)
        {
            if (loadedPrefabs.TryGetValue(name, out var cachedP))
            {
                return cachedP;
            }

            var toml = Path.Combine(contentRoot, "prefabs", name + ".toml");
            if (!File.Exists(toml))
            {
                throw new AssetException($"a scene references prefab '{name}', but content/prefabs/{name}.toml does not exist.");
            }

            var p = Scene.SceneTomlReader.ReadPrefab(toml);
            loadedPrefabs[name] = p;
            return p;
        }

        // Contenu-3b audit (engine-architect F4): a scene's incrementality hash must also fold in every prefab it
        // could reference — editing content/prefabs/helmet.toml touches no scene .toml and re-cooks no model, so
        // without this a stale .agscene would ship. Prefabs are cheap; hashing all of them per scene over-invalidates
        // rather than under-invalidates (the same philosophy as `anyModelCooked` above).
        var prefabsDir = Path.Combine(contentRoot, "prefabs");
        var allPrefabsHash = Directory.Exists(prefabsDir)
            ? HashHex(Encoding.UTF8.GetBytes(string.Join('\n', Directory
                .EnumerateFiles(prefabsDir, "*.toml", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => HashHex(File.ReadAllBytes(p))))))
            : string.Empty;

        foreach (var tomlPath in Directory.EnumerateFiles(scenesDir, "*.toml", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            // Contenu-3b audit (engine-architect F6): every OTHER cooked asset keys via FromContentPath (the
            // Contenu-2 fix for "directory jettisoned") — scenes must too, so a subdirectory under content/scenes/
            // coexists instead of colliding on Path.GetFileNameWithoutExtension.
            var pathKey = AssetKey.FromContentPath(contentRoot, tomlPath);
            var key = new AssetKey(StripTomlExtension(pathKey.Value!, tomlPath));
            if (!byKey.TryAdd(key, tomlPath))
            {
                throw new AssetException($"two sources map to key '{key}': '{byKey[key]}' and '{tomlPath}'.");
            }

            var tomlHash = HashHex(File.ReadAllBytes(tomlPath)) + ":" + allPrefabsHash;
            var blobRelative = key.Value + ".agscene";
            var blobFull = Path.Combine(outputDir, blobRelative);

            // Conservative: re-compile whenever any model was (re)cooked this run, the toml or a prefab it might
            // reference changed, or the cooker version changed. Scenes are cheap; a scene never ships stale.
            var upToDate = !anyModelCooked
                           && state.CookerVersion == CookerVersion
                           && state.SourceHashes.TryGetValue(key, out var prev)
                           && prev == tomlHash
                           && File.Exists(blobFull);

            if (upToDate)
            {
                skipped++;
            }
            else
            {
                var authored = Scene.SceneTomlReader.ReadScene(tomlPath);
                var def = Scene.SceneCompiler.Compile(authored, LoadModel, LoadPrefab);
                Scene.AgSceneWriter.WriteFile(blobFull, def);
                cooked++;
            }

            var blobBytes = File.ReadAllBytes(blobFull);
            entries.Add(new ManifestEntry(key, AssetKind.Scene, blobRelative, SHA256.HashData(blobBytes)));
            freshState.Add($"{key.Value}\t{tomlHash}");
            totalBlobBytes += blobBytes.LongLength;
        }
    }

    // A source that was deleted or renamed leaves its .agmodel behind; IncludeCookedAssets would ship dead content
    // forever. The cooker knows the exact expected set — anything else under outputDir is removed.
    private static void PruneOrphanBlobs(string outputDir, List<ManifestEntry> entries)
    {
        var expected = new HashSet<string>(
            entries.Select(e => Path.GetFullPath(Path.Combine(outputDir, e.BlobPath))),
            StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in new[] { "*.agmodel", "*.agscene" })
        {
            foreach (var blob in Directory.EnumerateFiles(outputDir, pattern, SearchOption.AllDirectories))
            {
                if (!expected.Contains(Path.GetFullPath(blob)))
                {
                    File.Delete(blob);
                }
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

    // Audit finding (csharp-lowlevel, 🟠): Directory.EnumerateFiles(dir, "*.toml") on Windows goes through Win32
    // wildcard matching, documented to also match a file whose extension merely BEGINS with the pattern's
    // extension (e.g. "probe.tomlbak"). A blind `[..^".toml".Length]` slice would then silently derive a wrong
    // key ("procedural/probe.tom") instead of failing loudly. Every ".toml"-stripping call site uses this.
    private static string StripTomlExtension(string key, string path)
    {
        if (!key.EndsWith(".toml", StringComparison.Ordinal))
        {
            throw new AssetException($"'{path}': expected a key ending in '.toml', got '{key}' (Win32 wildcard matching can return near-miss extensions).");
        }

        return key[..^".toml".Length];
    }
}
