using Agapanthe.Core;
using Agapanthe.Engine.Render;
using Agapanthe.Graphics;
using Agapanthe.Rendering;

namespace Agapanthe.App;

/// <summary>
/// The <b>presentation</b> half of what <see cref="ISceneRecipe.Build"/> receives (Contenu-3a): the GPU device,
/// the resource registry, the renderer, the camera + free-fly controller, the window and the per-frame render
/// list. <c>null</c> when the recipe is built headless — a client recipe opens <c>Build</c> with a guard
/// (<c>?? throw</c>). The sim-only half is <see cref="SimSceneContext"/>.
/// </summary>
public sealed class PresentationSceneContext
{
    /// <summary>The GPU device — for <see cref="Registry"/> uploads.</summary>
    public required GraphicsDevice Device { get; init; }

    /// <summary>Owns the GPU resources; hands back GPU-free <c>ImportedEntitySpec</c>s the world spawns.</summary>
    public required ResourceRegistry Registry { get; init; }

    /// <summary>The renderer — lights, environment, shadow distance, <c>MaterialSetLayout</c>.</summary>
    public required Renderer Renderer { get; init; }

    /// <summary>The view. A recipe frames it; a free-fly recipe also drives it from <see cref="Window"/>'s
    /// <c>Updated</c> event via <see cref="Controller"/>.</summary>
    public required Camera Camera { get; init; }

    /// <summary>The free-fly controller (move speed, look).</summary>
    public required FreeCameraController Controller { get; init; }

    /// <summary>The window — for interactive <c>SampleInput</c>, per-scene keys, and free-fly look.</summary>
    public required IWindow Window { get; init; }

    /// <summary>The reused per-frame render list.</summary>
    public required RenderList RenderList { get; init; }

    /// <summary>The frame assembly — register <c>IRenderSystem</c>s here. Simulation systems go through
    /// <see cref="SimSceneContext.AddSystem"/>.</summary>
    public required FrameOrchestrator Orchestrator { get; init; }
}
