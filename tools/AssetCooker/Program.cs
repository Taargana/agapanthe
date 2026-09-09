using Agapanthe.Assets;
using Agapanthe.Assets.Pipeline;
using Agapanthe.Core;

// Build-time asset cooker (Contenu-2). Globs *.glb under a content root, cooks each into a deterministic
// .agmodel blob, and writes content.agmanifest — so the RUNTIME never parses glTF, never generates tangents,
// never decodes an image. Twin of tools/FontCooker: invoked by an MSBuild target via `dotnet exec`.
//
// Usage: AssetCooker <contentRootDir> <outputDir>
// Exit codes: 0 = cooked (or already up to date), 1 = cook failed, 2 = bad arguments.

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: AssetCooker <contentRootDir> <outputDir>");
    return 2;
}

var contentRoot = args[0];
var outputDir = args[1];

if (!Directory.Exists(contentRoot))
{
    Console.Error.WriteLine($"AssetCooker: content root '{contentRoot}' does not exist.");
    return 2;
}

try
{
    var summary = CookRunner.Cook(contentRoot, outputDir);
    Log.Info(
        $"AssetCooker: {summary.Total} asset(s) under '{contentRoot}' → '{outputDir}' "
        + $"({summary.Cooked} cooked, {summary.Skipped} up to date, {summary.TotalBlobBytes / (1024.0 * 1024.0):F1} MB total). "
        + "Blobs store decoded RGBA8 — block compression (BC7/BC5) is a later render milestone; watch this figure.");
    return 0;
}
catch (Exception ex) when (ex is AssetException or AgModelException or IOException or UnauthorizedAccessException)
{
    // One clean line + exit 1, matching FontCooker / ShaderPrecompiler.
    Console.Error.WriteLine($"AssetCooker: {ex.Message}");
    return 1;
}
