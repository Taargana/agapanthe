using Agapanthe.Assets;
using Agapanthe.Assets.Model;

namespace Agapanthe.Assets.Pipeline;

/// <summary>Cook-side entry point for producing a <c>.agmodel</c> blob from a decoded <see cref="ModelAsset"/>
/// (Contenu-2). A thin façade over the internal <see cref="AgModelFormat"/> writer, reachable here via
/// <c>InternalsVisibleTo("Agapanthe.Assets.Pipeline")</c>.</summary>
public static class AgModelWriter
{
    /// <summary>Writes <paramref name="model"/> to <paramref name="stream"/> in the <c>.agmodel</c> layout.
    /// Deterministic on one machine.</summary>
    public static void Write(Stream stream, ModelAsset model) => AgModelFormat.WriteContainer(stream, model);

    /// <summary>Writes <paramref name="model"/> to <paramref name="path"/> via a temp file + atomic move, so an
    /// interrupted cook never leaves a half-written blob a later incremental build would trust.</summary>
    public static void WriteFile(string path, ModelAsset model)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            Write(stream, model);
        }

        File.Move(temp, path, overwrite: true);
    }
}
