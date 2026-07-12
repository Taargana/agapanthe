using System.Text;

namespace Agapanthe.Graphics;

/// <summary>
/// A shader source after include expansion, plus the deduplicated set of files that
/// contributed to it (root + every transitively included file, absolute paths). The
/// file list is exactly the set a file watcher must observe to know when the shader
/// is stale (milestone M8-05).
/// </summary>
public sealed record ResolvedShader(string Source, IReadOnlyList<string> Files);

/// <summary>
/// In-house <c>#include</c> resolver for GLSL shaders (spec §3.6/§4 — the shaderc includer
/// mechanism is deliberately not used). Directives of the form <c>#include "relative/path"</c>
/// are resolved relative to the file that issues them, expanded recursively with classic C
/// text semantics (each directive is textually replaced by the included file's content, so a
/// file included twice is expanded twice), while the returned <see cref="ResolvedShader.Files"/>
/// list is deduplicated for the watcher. Include cycles and missing files both throw
/// <see cref="GraphicsException"/> — there is no silent fallback (spec §4).
/// </summary>
public static class ShaderIncludeResolver
{
    /// <summary>
    /// The single source of truth for comparing shader file paths: case-insensitive on Windows,
    /// case-sensitive everywhere else (on Linux <c>Common.glsl</c> and <c>common.glsl</c> are two
    /// distinct files). Every layer that dedups, matches or watches shader paths must use <b>this</b>
    /// comparer — include resolution, the source-file dedup of a reloadable pass, the hot reloader's
    /// pending set and the file→pass mapping — or a file would be silently dropped from the watch set
    /// on a case-sensitive filesystem (audit M8-09 M2).
    /// </summary>
    public static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Reads <paramref name="rootPath"/> and returns its source with every <c>#include</c>
    /// expanded, together with the absolute, deduplicated list of every file touched (the
    /// root first, then includes in first-seen order).
    /// </summary>
    /// <exception cref="GraphicsException">
    /// The root or an included file does not exist, an <c>#include</c> directive is malformed,
    /// or an include cycle is detected.
    /// </exception>
    public static ResolvedShader Resolve(string rootPath)
    {
        ArgumentNullException.ThrowIfNull(rootPath);

        var root = Path.GetFullPath(rootPath);
        if (!File.Exists(root))
        {
            throw new GraphicsException($"Shader file not found: '{root}'.");
        }

        var files = new List<string> { root };
        var seen = new HashSet<string>(PathComparer) { root };
        var stack = new List<string>();
        var sb = new StringBuilder();

        Expand(root, sb, stack, files, seen);

        return new ResolvedShader(sb.ToString(), files);
    }

    private static void Expand(
        string filePath, StringBuilder sb, List<string> stack, List<string> files, HashSet<string> seen)
    {
        stack.Add(filePath);
        var text = File.ReadAllText(filePath);
        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;

        // Walk physical lines while preserving their original terminators, so a file with no
        // #include is reproduced byte-for-byte (keeps the compiled SPIR-V identical for the
        // common no-include case).
        var pos = 0;
        while (pos < text.Length)
        {
            var newline = text.IndexOf('\n', pos);
            var lineEnd = newline < 0 ? text.Length : newline + 1;
            var line = text.AsSpan(pos, lineEnd - pos);

            if (TryParseInclude(line, filePath, out var relative))
            {
                var included = Path.GetFullPath(Path.Combine(directory, relative));

                var cycleAt = stack.FindIndex(p => PathComparer.Equals(p, included));
                if (cycleAt >= 0)
                {
                    var chain = string.Join(" -> ", stack.Skip(cycleAt).Append(included));
                    throw new GraphicsException($"Include cycle detected: {chain}.");
                }

                if (!File.Exists(included))
                {
                    throw new GraphicsException(
                        $"Included shader file not found: '{included}' (included from '{filePath}').");
                }

                if (seen.Add(included))
                {
                    files.Add(included);
                }

                Expand(included, sb, stack, files, seen);

                // Guarantee the expanded content is separated from following source, so the
                // include directive's position always ends a line.
                if (sb.Length > 0 && sb[^1] != '\n')
                {
                    sb.Append('\n');
                }
            }
            else
            {
                sb.Append(line);
            }

            pos = lineEnd;
        }

        stack.RemoveAt(stack.Count - 1);
    }

    /// <summary>
    /// Recognizes <c>#include "path"</c> (with optional whitespace after <c>#</c> and before the
    /// quote). Returns false for any other line. Throws when a line clearly begins an include
    /// directive but is malformed (e.g. angle-bracket or unquoted form) — no silent fallback.
    /// </summary>
    private static bool TryParseInclude(ReadOnlySpan<char> line, string filePath, out string relative)
    {
        relative = string.Empty;

        var s = line.TrimStart();
        if (s.IsEmpty || s[0] != '#')
        {
            return false;
        }

        s = s[1..].TrimStart(); // after '#'
        const string keyword = "include";
        if (!s.StartsWith(keyword))
        {
            return false;
        }

        var after = s[keyword.Length..];
        // Must be followed by whitespace (or the quote) — avoids matching e.g. "#includeX".
        if (!after.IsEmpty && !char.IsWhiteSpace(after[0]) && after[0] != '"')
        {
            return false;
        }

        after = after.TrimStart();
        if (after.IsEmpty || after[0] != '"')
        {
            throw new GraphicsException(
                $"Malformed #include directive in '{filePath}': expected #include \"relative/path\", got: {line.Trim().ToString()}");
        }

        var rest = after[1..];
        var close = rest.IndexOf('"');
        if (close < 0)
        {
            throw new GraphicsException(
                $"Malformed #include directive in '{filePath}': missing closing quote in: {line.Trim().ToString()}");
        }

        relative = rest[..close].ToString();
        if (relative.Length == 0)
        {
            throw new GraphicsException(
                $"Malformed #include directive in '{filePath}': empty include path.");
        }

        return true;
    }
}
