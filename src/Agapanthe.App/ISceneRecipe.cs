namespace Agapanthe.App;

/// <summary>
/// One scene family. <see cref="Build"/> spawns entities, registers systems on
/// <see cref="SceneContext.Orchestrator"/> / <see cref="SceneContext.Simulation"/>, frames the camera, and — for a
/// free-fly scene — subscribes to <see cref="SceneContext.Window"/>'s <c>Updated</c> event to drive the controller
/// (that is the one place <c>MouseDelta</c> is valid; it is zeroed right after <c>Updated</c> fires). It also
/// subscribes to <c>KeyPressed</c> for this scene's own keys.
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

    /// <summary>Builds the scene. Called ONCE, after <see cref="AppHost"/> has built the GPU stack and the
    /// orchestrator (and wired the debug overlay), before the first tick.</summary>
    void Build(SceneContext ctx);
}
