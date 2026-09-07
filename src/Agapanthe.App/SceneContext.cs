using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Engine.Render;
using Agapanthe.Graphics;
using Agapanthe.Rendering;
using Agapanthe.World;

namespace Agapanthe.App;

/// <summary>
/// The toolbox handed to <see cref="ISceneRecipe.Build"/>: the full GPU + simulation stack <see cref="AppHost"/>
/// built, exactly what the pre-extraction <c>Program.cs</c> touched inline. Recipes are trusted first-party code.
/// </summary>
public sealed class SceneContext
{
    /// <summary>The GPU device — for <see cref="Registry"/> uploads.</summary>
    public required GraphicsDevice Device { get; init; }

    /// <summary>Owns the GPU resources; hands back GPU-free <c>ImportedEntitySpec</c>s the world spawns.</summary>
    public required ResourceRegistry Registry { get; init; }

    /// <summary>The entities.</summary>
    public required GameWorld World { get; init; }

    /// <summary>The frame assembly — register systems here (<c>Add(Stage, ISystem)</c> / <c>Add(IRenderSystem)</c>).</summary>
    public required FrameOrchestrator Orchestrator { get; init; }

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

    /// <summary>The raw process command line — the host does not parse it; a recipe may
    /// (e.g. a model viewer picks the glTF).</summary>
    public required string[] Args { get; init; }

    /// <summary>The simulation half (input phase, tick schedule, <c>SimulationSettings</c>).</summary>
    public SimulationHost Simulation => Orchestrator.Simulation;
}
