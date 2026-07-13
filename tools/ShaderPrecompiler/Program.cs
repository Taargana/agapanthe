using Agapanthe.Core;
using Agapanthe.Graphics;

// Build-time shader precompiler (Phase 2, P2-M1-04). It reuses the ENGINE's ShaderCompiler and
// ShaderIncludeResolver, so the disk-cache entries it writes are byte-for-byte the ones the runtime looks up:
// the key is {stage}_{SHA256(source resolved after #include)}.spv, computed from the same resolver and the same
// hash. A shipping build therefore starts with a fully warm cache and never loads shaderc at runtime (rule
// §2.1-2). shaderc AT BUILD is fine (dev machine); it is shaderc AT RUNTIME that this milestone eliminates.
//
// Usage: ShaderPrecompiler <shadersSourceDir> <cacheOutputDir>
//   - globs *.vert / *.frag / *.comp in <shadersSourceDir> (stage derived from the extension — all the coverage
//     both runtime ShaderCompiler instances need without enumerating passes),
//   - resolves #include, compiles, and writes each .spv into <cacheOutputDir> as a side effect of Compile.
// Exit codes: 0 = all compiled, 1 = a shader failed / none found, 2 = bad arguments.

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: ShaderPrecompiler <shadersSourceDir> <cacheOutputDir>");
    return 2;
}

var shadersDir = args[0];
var cacheDir = args[1];

if (!Directory.Exists(shadersDir))
{
    Console.Error.WriteLine($"ShaderPrecompiler: shader source directory '{shadersDir}' does not exist.");
    return 2;
}

var stages = new Dictionary<string, ShaderStage>(StringComparer.OrdinalIgnoreCase)
{
    [".vert"] = ShaderStage.Vertex,
    [".frag"] = ShaderStage.Fragment,
    [".comp"] = ShaderStage.Compute,
};

// Recursive to match the csproj's shaders/**\* glob (audit m4): if shaders are ever nested in subfolders, the
// tool and the build must agree on the set. Deterministic order so the build log reads the same run to run.
var shaderFiles = Directory.EnumerateFiles(shadersDir, "*", SearchOption.AllDirectories)
    .Where(f => stages.ContainsKey(Path.GetExtension(f)))
    .OrderBy(f => f, StringComparer.Ordinal)
    .ToList();

if (shaderFiles.Count == 0)
{
    Console.Error.WriteLine($"ShaderPrecompiler: no .vert/.frag/.comp shaders found under '{shadersDir}'.");
    return 1;
}

// Full mode: the build machine has shaderc. CompileFileResolved writes {stage}_{hash}.spv into cacheDir.
using var compiler = new ShaderCompiler(cacheDir);
var count = 0;
try
{
    foreach (var file in shaderFiles)
    {
        var stage = stages[Path.GetExtension(file)];
        compiler.CompileFileResolved(file, stage);
        count++;
    }
}
catch (Exception ex) when (ex is GraphicsException or IOException or UnauthorizedAccessException)
{
    // A compile error (GraphicsException) or an unreadable source / missing #include (IO) fails the build with a
    // clean one-line message + exit 1 rather than a raw stack (audit m5).
    Console.Error.WriteLine($"ShaderPrecompiler: {ex.Message}");
    return 1;
}

Log.Info($"ShaderPrecompiler: pre-cooked {count} shader(s) into '{cacheDir}'.");
return 0;
