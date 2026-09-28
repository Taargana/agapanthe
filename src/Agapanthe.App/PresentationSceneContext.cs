using Agapanthe.Assets.Font;
using Agapanthe.Core;
using Agapanthe.Engine.Render;
using Agapanthe.Graphics;
using Agapanthe.Rendering;
using Agapanthe.Ui;

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

    /// <summary>Contenu-3c: this game's scene-system factory registry (from <see cref="IGame.SceneSystems"/>).
    /// Lives here, not on <see cref="SimSceneContext"/>, because <see cref="ISceneSystemFactory.Create"/> itself
    /// names <see cref="PresentationSceneContext"/> — a scene-system factory is a client-only concept (window/
    /// camera-coupled), so the headless-safe context must not carry it even transitively (audit finding,
    /// engine-architect: the prior placement weakened the <c>SimSceneContext_NamesNoGpuOrWindowType</c> gate's
    /// intent without tripping its assertion).</summary>
    public required IReadOnlyList<ISceneSystemFactory> SceneSystemFactories { get; init; }

    /// <summary>
    /// Scene management spec, D5: the shared <c>UiRenderSystem.DrawList</c> a recipe appends to via
    /// <see cref="Agapanthe.Ui.TextLayout.DrawText"/> to draw its own on-screen text — <c>null</c> exactly when no
    /// cooked font was found (mirroring the debug overlay's own existing silent-absence convention). A recipe never
    /// constructs its own <c>UiRenderSystem</c> — this is the one, process-shared instance <see cref="AppHost"/>
    /// owns.
    /// </summary>
    public UiDrawList? UiDrawList { get; init; }

    /// <summary>
    /// Scene management spec, D5: the loaded font, for <see cref="UiDrawList"/> draws — <c>null</c> exactly when
    /// <see cref="UiDrawList"/> is. Stays valid across a scene switch (a hand-off never reloads the font; the
    /// engine keeps exactly one atlas live for the whole process, <c>Renderer.LoadFont</c>'s own contract).
    /// <b>A recipe must never call <c>presentation.Renderer.LoadFont(...)</c> itself</b> — it would replace the
    /// shared atlas without updating this property, silently corrupting glyph UVs (this font's metrics against a
    /// different atlas). Loading the font is <see cref="AppHost"/>'s job alone.
    /// </summary>
    public FontAsset? UiFont { get; init; }
}
