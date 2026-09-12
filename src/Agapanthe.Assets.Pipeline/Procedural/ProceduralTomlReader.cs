using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Tomlyn;
using Tomlyn.Model;
using static Agapanthe.Assets.Pipeline.Scene.TomlHelpers;

namespace Agapanthe.Assets.Pipeline.Procedural;

/// <summary>Contenu-3c — parses one `content/procedural/*.toml` and dispatches to its named generator. The file
/// is a single flat table (no directives, no arrays-of-tables): a top-level `generator = "..."` key plus whatever
/// keys that generator itself validates (each generator owns its own <c>RejectUnknownKeys</c> call, same style
/// <see cref="Scene.SceneTomlReader"/> uses per-block).</summary>
internal static class ProceduralTomlReader
{
    public static ModelAsset Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AssetException($"cannot read procedural file '{path}': {ex.Message}", ex);
        }

        var result = Toml.Parse(text, path);
        if (result.HasErrors)
        {
            throw new AssetException($"procedural file '{path}' has TOML syntax errors: {result.Diagnostics}");
        }

        var table = result.ToModel();
        var generator = Str(table, "generator", path)
                        ?? throw new AssetException($"'{path}': missing top-level 'generator' key.");

        return ProceduralGenerators.Build(generator, table, path);
    }
}
