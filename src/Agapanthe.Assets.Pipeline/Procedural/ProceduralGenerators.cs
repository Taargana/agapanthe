using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Tomlyn.Model;

namespace Agapanthe.Assets.Pipeline.Procedural;

/// <summary>
/// Contenu-3c — the cook-time-only name→generator registry: a `content/procedural/*.toml`'s top-level
/// `generator = "..."` key dispatches here. Cook-tool-internal — never exposed through <c>IGame</c> or any
/// runtime type (unlike the client-side <c>ISceneSystemFactory</c> registry, procedural generation happens once,
/// at cook time, and its output is a plain <see cref="AssetKind.Model"/> blob indistinguishable from a
/// glTF-sourced one to every downstream reader).
/// </summary>
internal static class ProceduralGenerators
{
    private static readonly Dictionary<string, Func<TomlTable, string, ModelAsset>> ByName =
        new(StringComparer.Ordinal) { ["uv_sphere"] = UvSphereGenerator.Build };

    public static ModelAsset Build(string generatorName, TomlTable table, string path)
        => ByName.TryGetValue(generatorName, out var build)
            ? build(table, path)
            : throw new AssetException(
                $"'{path}': unknown generator '{generatorName}' (known: {string.Join(", ", ByName.Keys)}).");
}
