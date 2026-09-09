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

    /// <summary>Builds the scene. Called ONCE, after <see cref="AppHost"/> has built the simulation (and, for a
    /// client run, the GPU stack + orchestrator + debug overlay), before the first tick.
    /// <paramref name="presentation"/> is <c>null</c> for a headless build — a client-only recipe guards with
    /// <c>?? throw</c>.</summary>
    void Build(SimSceneContext sim, PresentationSceneContext? presentation);
}
