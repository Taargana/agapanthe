using Agapanthe.Graphics;

namespace Agapanthe.Rendering.Passes;

/// <summary>
/// A render pass whose graphics pipeline can be rebuilt from its shader sources at runtime (shader hot
/// reload, spec §3.6 / M8). Each implementation owns its <see cref="ShaderModule"/>s, its
/// <see cref="GraphicsPipeline"/> and a copy of the <em>stable</em> pipeline description (attachment formats,
/// set layouts, cull/depth state, vertex layout — everything <b>except</b> the shader modules), so it can
/// recompile and recreate the pipeline without the <see cref="Renderer"/> re-plumbing anything.
/// <para>
/// <b>Reload safety.</b> <see cref="Reload"/> retires the old pipeline through the device deletion queue
/// (deferred N+FramesInFlight, see <see cref="GraphicsPipeline.Dispose"/>). That is safe <b>only because</b>
/// reloads are driven at the frame boundary, before command recording (M8-05
/// <c>Renderer.PollShaderReload</c>); the deferral then covers every frame still in flight. Never call
/// <see cref="Reload"/> mid-recording.
/// </para>
/// </summary>
internal interface IReloadablePipeline : IDisposable
{
    /// <summary>
    /// Absolute, deduplicated paths of every source file (root shader + resolved <c>#include</c>s) the
    /// current pipeline was compiled from — the set a file watcher observes to decide a reload (M8-05).
    /// Refreshed on each successful <see cref="Reload"/>.
    /// </summary>
    IReadOnlyList<string> SourceFiles { get; }

    /// <summary>The live graphics pipeline. The <see cref="Renderer"/>'s record methods bind this.</summary>
    GraphicsPipeline Pipeline { get; }

    /// <summary>
    /// Recompiles the shaders (via <see cref="ShaderCompiler.CompileFileResolved"/>) and, on success, builds a
    /// new pipeline, retires the old pipeline and modules to the deletion queue and swaps them in, refreshing
    /// <see cref="SourceFiles"/>. On any compilation or pipeline-creation failure it logs an error and keeps
    /// the current pipeline intact (spec §4 — a bad edit never crashes the renderer). Must be called at the
    /// frame boundary, before recording (see the type remarks).
    /// </summary>
    /// <returns>
    /// <c>true</c> if a new pipeline was swapped in; <c>false</c> if the edit failed to compile and the
    /// previous pipeline was kept. Callers must not report a reload as successful on <c>false</c>.
    /// </returns>
    bool Reload(ShaderCompiler compiler);
}
