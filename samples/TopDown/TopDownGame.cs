using Agapanthe.App;
using Agapanthe.Platform.App.Systems;

namespace TopDown;

/// <summary>
/// Slice-2 — the 2nd dissimilar slice: a top-down orthographic app. Its whole point is to be a second,
/// independent <see cref="IGame"/> proving <see cref="AppHost"/>/<see cref="ISceneRecipe"/>/
/// <see cref="ISceneSystemFactory"/> are reusable outside <c>samples/Sandbox</c> — everything here mirrors
/// <c>Sandbox.SandboxGame</c>'s shape exactly, just with a single cooked scene and the shared
/// <see cref="DriveControlSystemFactory"/> (from <c>Agapanthe.Platform.App</c>, the same class Sandbox uses,
/// not a duplicate).
/// <para>
/// <see cref="IGame.Universe"/> stays <c>UniverseId.None</c> (dev-host convention, matching
/// <c>SandboxGame</c>).
/// </para>
/// </summary>
internal sealed class TopDownGame : IGame
{
    public string Title => "Agapanthe TopDown";

    public string DefaultScene => "topdown";

    public IReadOnlyList<ISceneRecipe> Scenes { get; } = [new SceneRecipe("topdown")];

    public IReadOnlyList<ISceneSystemFactory> SceneSystems { get; } = [new DriveControlSystemFactory()];
}
