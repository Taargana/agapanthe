using Agapanthe.Assets;

namespace Agapanthe.App;

/// <summary>
/// One scene family. <see cref="Build"/> spawns entities, registers simulation systems via
/// <see cref="SimSceneContext.AddSystem"/>, and — for a client scene — frames the camera and subscribes to the
/// window's <c>Updated</c>/<c>KeyPressed</c> events (Contenu-3a splits the sim half from the presentation half so
/// a recipe can also be built headless).
/// <para>
/// <b>This is code organisation, not a data format.</b> A recipe is a class; the declarative scene/prefab format
/// is a separate, later milestone.
/// </para>
/// </summary>
public interface ISceneRecipe
{
    /// <summary>The canonical <c>AGAPANTHE_SCENE</c> token for this recipe (also its <see cref="IGame.DefaultScene"/>
    /// key).</summary>
    string Name { get; }

    /// <summary>True if <paramref name="sceneToken"/> selects this recipe. Default: ordinal-ignore-case equality
    /// with <see cref="Name"/>. A recipe covering a token family (e.g. <c>grid:100x100</c>) overrides this.</summary>
    bool Matches(string? sceneToken)
        => sceneToken is not null && string.Equals(sceneToken, Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Scene management spec, D7: the part of loading this scene that is safe to run on the sanctioned loader
    /// thread (or inline on the main thread, for a synchronous load) — GPU-free content decode plus GPU upload
    /// into <paramref name="background"/>'s fresh, isolated registry. Must observe
    /// <c>background?.CancellationToken</c> between per-model iterations. Never touches
    /// <c>GameWorld</c>/<c>SimulationHost</c>/<c>Renderer</c> — none of those are reachable from
    /// <paramref name="background"/>. Returns whatever <see cref="Build"/> needs to avoid re-decoding what this
    /// already did (e.g. a <c>Dictionary&lt;AssetKey, ModelAsset&gt;</c>); default implementation returns
    /// <c>null</c>, for the common case of a recipe with nothing to prefetch.
    /// </summary>
    object? PrefetchBackground(AssetCatalog catalog, BackgroundPresentationContext? background) => null;

    /// <summary>Builds the scene. Called ONCE, after <see cref="AppHost"/> has built the simulation (and, for a
    /// client run, the GPU stack + orchestrator + debug overlay), before the first tick. MUST run on the
    /// owner/main thread — <c>GameWorld</c>/<c>SimulationHost</c> are constructed fresh immediately before this
    /// call, never handed across threads. <paramref name="prefetched"/> is whatever <see cref="PrefetchBackground"/>
    /// returned (or <c>null</c> if it was never called or returned nothing). <paramref name="presentation"/> is
    /// <c>null</c> for a headless build — a client-only recipe guards with <c>?? throw</c>.</summary>
    void Build(object? prefetched, SimSceneContext sim, PresentationSceneContext? presentation);
}
