using Agapanthe.Assets.Scene;

namespace Agapanthe.Assets.Pipeline.Scene;

/// <summary>Contenu-3b — writes a compiled <see cref="SceneDefinition"/> to a <c>.agscene</c> blob. Thin public
/// façade over the <c>internal</c> <see cref="AgSceneFormat.WriteContainer"/> (IVT to this assembly), with the
/// tmp-then-move write of <c>AgModelWriter</c> so an interrupted cook never leaves a half-written blob.</summary>
public static class AgSceneWriter
{
    public static void Write(Stream stream, SceneDefinition def) => AgSceneFormat.WriteContainer(stream, def);

    public static void WriteFile(string path, SceneDefinition def)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var fs = File.Create(temp))
        {
            AgSceneFormat.WriteContainer(fs, def);
        }

        File.Move(temp, path, overwrite: true);
    }
}
