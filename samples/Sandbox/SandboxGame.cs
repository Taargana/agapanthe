using Agapanthe.App;

namespace Sandbox;

/// <summary>
/// The Sandbox as an <see cref="IGame"/> — the engine's reference application and its integration test bed. It
/// carries the scene recipes; <see cref="AppHost"/> owns everything else (bootstrap, loop, teardown, capture).
/// <para>
/// <see cref="IGame.Universe"/> stays <c>UniverseId.None</c> (the default): the Sandbox is a dev host, and keeping
/// it unidentified keeps every pinned capture/snapshot hash byte-identical. <c>AGAPANTHE_UNIVERSE</c> overrides it
/// per run.
/// </para>
/// </summary>
internal sealed class SandboxGame : IGame
{
    public string Title => "Agapanthe Sandbox";

    public string DefaultScene => "model";

    // ModelSceneRecipe last: it matches null/empty and the grid:/drop: families, so a named scene must get first
    // refusal at SelectRecipe.
    public IReadOnlyList<ISceneRecipe> Scenes { get; } =
    [
        new PlanetSceneRecipe(),
        new PlanetDropSceneRecipe(),
        new PlanetChallengeSceneRecipe(),
        new DriveSceneRecipe(),
        new ModelSceneRecipe(),
    ];
}
