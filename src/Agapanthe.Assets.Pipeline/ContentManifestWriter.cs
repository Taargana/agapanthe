using Agapanthe.Assets;

namespace Agapanthe.Assets.Pipeline;

/// <summary>Cook-side entry point for writing a <c>content.agmanifest</c> (Contenu-2). Thin façade over the
/// internal <see cref="ContentManifest"/> writer.</summary>
public static class ContentManifestWriter
{
    /// <summary>Writes <paramref name="entries"/> (sorted ordinal by key) to <paramref name="stream"/>.</summary>
    public static void Write(Stream stream, IReadOnlyList<ManifestEntry> entries)
        => ContentManifest.Write(stream, entries);

    /// <summary>Writes the manifest to <paramref name="path"/> via a temp file + atomic move.</summary>
    public static void WriteFile(string path, IReadOnlyList<ManifestEntry> entries)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            Write(stream, entries);
        }

        File.Move(temp, path, overwrite: true);
    }
}
