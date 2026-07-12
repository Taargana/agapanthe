using Agapanthe.Core;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering.Passes;

/// <summary>
/// Shared skeleton for the four reloadable passes (<see cref="ShadowPass"/>, <see cref="ScenePass"/>,
/// <see cref="SkyboxPass"/>, <see cref="TonemapPass"/>). It owns the pass's shader modules, its live
/// <see cref="GraphicsPipeline"/> and the resolved source-file list, and implements the compile → build →
/// swap lifecycle once. Subclasses provide only what varies: the shader files to compile and the stable
/// pipeline description (formats, set layouts, cull/depth state) — via <see cref="CreatePipeline"/>.
/// <para>
/// A subclass constructor assigns its own stable fields and then calls <see cref="Build"/> last, which
/// compiles the shaders and creates the initial pipeline. <see cref="Reload"/> repeats the same compile/create
/// steps but only swaps on success and defers the old objects (see <see cref="IReloadablePipeline"/>).
/// </para>
/// </summary>
internal abstract class ReloadablePass : IReloadablePipeline
{
    private readonly GraphicsDevice _device;
    private readonly string _shaderDirectory;
    private readonly (string FileName, ShaderStage Stage)[] _shaderFiles;

    private ShaderModule[] _modules = [];
    private GraphicsPipeline? _pipeline;
    private string[] _sourceFiles = [];
    private bool _disposed;

    /// <param name="device">The graphics device (borrowed).</param>
    /// <param name="shaderDirectory">Directory the shader file names resolve against.</param>
    /// <param name="shaderFiles">
    /// The shaders to compile, in the order <see cref="CreatePipeline"/> expects them (index 0 = vertex, then
    /// fragment). The compiled modules are handed to <see cref="CreatePipeline"/> in the same order.
    /// </param>
    protected ReloadablePass(
        GraphicsDevice device, string shaderDirectory, (string FileName, ShaderStage Stage)[] shaderFiles)
    {
        _device = device;
        _shaderDirectory = shaderDirectory;
        _shaderFiles = shaderFiles;
    }

    /// <summary>The graphics device, for <see cref="CreatePipeline"/>.</summary>
    protected GraphicsDevice Device => _device;

    public GraphicsPipeline Pipeline => _pipeline!;

    public IReadOnlyList<string> SourceFiles => _sourceFiles;

    /// <summary>
    /// Builds the initial pipeline. A subclass calls this as the <b>last</b> statement of its constructor,
    /// after its stable fields are assigned, so <see cref="CreatePipeline"/> can read them. Throws on
    /// compilation or pipeline-creation failure (initial construction must fail loudly — unlike a hot reload,
    /// there is no previous pipeline to fall back to); the caller's ctor <c>catch</c> disposes the pass.
    /// </summary>
    protected void Build(ShaderCompiler compiler)
    {
        var (modules, sourceFiles) = CompileModules(compiler);
        try
        {
            _pipeline = CreatePipeline(modules);
        }
        catch
        {
            DisposeModules(modules);
            throw;
        }

        _modules = modules;
        _sourceFiles = sourceFiles;
    }

    public bool Reload(ShaderCompiler compiler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Compile into fresh modules first. A failed edit (missing include, GLSL error) must not touch the
        // live pipeline: log and keep rendering with what we have (spec §4).
        ShaderModule[] newModules;
        string[] newSourceFiles;
        try
        {
            (newModules, newSourceFiles) = CompileModules(compiler);
        }
        catch (Exception ex)
        {
            Log.Error($"{GetType().Name}: shader recompilation failed, keeping the previous pipeline. {ex.Message}");
            return false;
        }

        GraphicsPipeline newPipeline;
        try
        {
            newPipeline = CreatePipeline(newModules);
        }
        catch (Exception ex)
        {
            DisposeModules(newModules);
            Log.Error($"{GetType().Name}: pipeline recreation failed, keeping the previous pipeline. {ex.Message}");
            return false;
        }

        // Success: retire the old pipeline and modules to the deletion queue (deferred N+FramesInFlight — safe
        // because reloads run at the frame boundary before recording, see IReloadablePipeline), then swap.
        _pipeline?.Dispose();
        DisposeModules(_modules);
        _pipeline = newPipeline;
        _modules = newModules;
        _sourceFiles = newSourceFiles;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pipeline?.Dispose();
        DisposeModules(_modules);
        _pipeline = null;
        _modules = [];
    }

    /// <summary>
    /// Builds the pass's pipeline from freshly compiled <paramref name="modules"/> (indexed as in the
    /// constructor's <c>shaderFiles</c>). Reads the subclass's stable state (formats, set layouts, cull/depth);
    /// must not mutate any pass field. Called by <see cref="Build"/> and every <see cref="Reload"/>.
    /// </summary>
    protected abstract GraphicsPipeline CreatePipeline(ShaderModule[] modules);

    // Compiles every shader file into a new module and collects the deduplicated resolved source-file set.
    // On failure disposes whatever modules were already created and rethrows.
    private (ShaderModule[] Modules, string[] SourceFiles) CompileModules(ShaderCompiler compiler)
    {
        var modules = new ShaderModule[_shaderFiles.Length];

        // Dedup across the pass's shaders (a shared include appears in both stages' file lists) with the ONE
        // OS-aware path comparer (ShaderIncludeResolver.PathComparer): on Linux `Common.glsl` and `common.glsl`
        // are distinct files and both must stay in SourceFiles, or the second would never be watched while
        // Renderer's file->pass mapping (same comparer) would still match it (audit M8-09 M2). The HashSet also
        // replaces an O(n) Enumerable.Contains; the List preserves first-seen order (root first).
        var files = new List<string>();
        var seen = new HashSet<string>(ShaderIncludeResolver.PathComparer);
        var built = 0;
        try
        {
            for (var i = 0; i < _shaderFiles.Length; i++)
            {
                var path = Path.Combine(_shaderDirectory, _shaderFiles[i].FileName);
                var (spirv, sourceFiles) = compiler.CompileFileResolved(path, _shaderFiles[i].Stage);
                modules[i] = new ShaderModule(_device, spirv, _shaderFiles[i].Stage);
                built++;
                for (var f = 0; f < sourceFiles.Count; f++)
                {
                    if (seen.Add(sourceFiles[f]))
                    {
                        files.Add(sourceFiles[f]);
                    }
                }
            }
        }
        catch
        {
            for (var i = 0; i < built; i++)
            {
                modules[i].Dispose();
            }

            throw;
        }

        return (modules, files.ToArray());
    }

    private static void DisposeModules(ShaderModule[] modules)
    {
        for (var i = 0; i < modules.Length; i++)
        {
            modules[i]?.Dispose();
        }
    }
}
