using System.Globalization;
using Agapanthe.Assets.Font;
using Agapanthe.Core;
using FontCooker;

// Build-time font cooker (UI-1). Rasterises a .ttf into a single-channel SDF atlas + metrics and writes a .agfont,
// so the RUNTIME never parses a font file and never links a font library — the same stance the shader pipeline
// takes with shaderc (Release forbids runtime compilation and strips the native library from the output).
//
// Deliberately NOT referenced by any shipping project: a ProjectReference would drag StbTrueTypeSharp into the AOT
// closure. Invoked by an MSBuild target via `dotnet exec`, exactly like tools/ShaderPrecompiler.
//
// Usage: FontCooker <font.ttf> <charset.txt> <out.agfont> [--dump-atlas <out.pgm>]
//   charset.txt holds one entry per line: a single character, a U+XXXX codepoint, or a RANGE (U+0020-U+007E).
//   Blank lines and lines starting with '#' are ignored.
// Exit codes: 0 = cooked, 1 = cook failed, 2 = bad arguments.

if (args.Length is not (3 or 5) || (args.Length == 5 && args[3] != "--dump-atlas"))
{
    Console.Error.WriteLine("usage: FontCooker <font.ttf> <charset.txt> <out.agfont> [--dump-atlas <out.pgm>]");
    return 2;
}

var fontPath = args[0];
var charsetPath = args[1];
var outputPath = args[2];
var dumpPath = args.Length == 5 ? args[4] : null;

if (!File.Exists(fontPath))
{
    Console.Error.WriteLine($"FontCooker: font file '{fontPath}' does not exist.");
    return 2;
}

if (!File.Exists(charsetPath))
{
    Console.Error.WriteLine($"FontCooker: charset file '{charsetPath}' does not exist.");
    return 2;
}

try
{
    var codepoints = ParseCharset(File.ReadAllLines(charsetPath));
    if (codepoints.Count == 0)
    {
        Console.Error.WriteLine($"FontCooker: charset '{charsetPath}' yielded no codepoints.");
        return 1;
    }

    var font = SdfFontBaker.Bake(File.ReadAllBytes(fontPath), codepoints);

    // Write via a temp file + move, so an interrupted cook can never leave a half-written .agfont that a later
    // incremental build would happily treat as up to date (the shader cache learned this the hard way).
    var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var temp = outputPath + ".tmp";
    using (var stream = File.Create(temp))
    {
        FontAssetFormat.Write(stream, font);
    }

    File.Move(temp, outputPath, overwrite: true);

    if (dumpPath is not null)
    {
        WritePgm(dumpPath, font.AtlasPixels, font.AtlasWidth, font.AtlasHeight);
        Log.Info($"FontCooker: atlas dumped to '{dumpPath}'.");
    }

    Log.Info(
        $"FontCooker: cooked '{Path.GetFileName(fontPath)}' → '{outputPath}' "
        + $"({font.Glyphs.Length} glyph(s), {font.KernPairs.Length} kern pair(s), "
        + $"{font.AtlasWidth}×{font.AtlasHeight} SDF atlas, em {font.EmPixelSize:F0} px, spread {font.SdfPixelRange:F0}).");
    return 0;
}
catch (Exception ex) when (ex is InvalidOperationException or FormatException or IOException
                               or UnauthorizedAccessException or FontAssetException)
{
    // One clean line + exit 1 rather than a raw stack, matching ShaderPrecompiler's failure shape.
    Console.Error.WriteLine($"FontCooker: {ex.Message}");
    return 1;
}

// Parses the charset into a sorted, de-duplicated codepoint list. Sorting here is what makes the whole cook
// deterministic downstream — every later stage relies on ascending codepoint order.
static List<uint> ParseCharset(string[] lines)
{
    var set = new SortedSet<uint>();
    foreach (var raw in lines)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == '#')
        {
            continue;
        }

        var dash = line.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0 && line.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
        {
            var from = ParseCodepoint(line[..dash].Trim());
            var to = ParseCodepoint(line[(dash + 1)..].Trim());
            if (to < from)
            {
                throw new FormatException($"Charset range '{line}' runs backwards.");
            }

            for (var cp = from; cp <= to; cp++)
            {
                set.Add(cp);
            }

            continue;
        }

        set.Add(ParseCodepoint(line));
    }

    return [.. set];
}

static uint ParseCodepoint(string token)
{
    if (token.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
    {
        return uint.Parse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    var runes = token.EnumerateRunes();
    if (runes.MoveNext())
    {
        var rune = runes.Current;
        if (!runes.MoveNext())
        {
            return (uint)rune.Value;
        }
    }

    throw new FormatException($"Charset entry '{token}' is neither a single character nor a U+XXXX codepoint.");
}

// Binary PGM: the simplest single-channel image format, readable by any viewer. Debug aid only (--dump-atlas), so
// the cooker stays free of an image-encoding dependency.
static void WritePgm(string path, byte[] pixels, int width, int height)
{
    using var stream = File.Create(path);
    using var writer = new StreamWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
    writer.Write($"P5\n{width} {height}\n255\n");
    writer.Flush();
    stream.Write(pixels, 0, pixels.Length);
}
