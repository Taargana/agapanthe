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

    // Contenu-3b/3c: every scene is now cooked data — one generic `SceneRecipe` per `content/scenes/*.toml`.
    // `drive` (last to migrate, Contenu-3c-3) dropped its CLI arbitrary-model argument (D6, confirmed capability
    // loss) — `drive.toml` fixes on `models/DamagedHelmet.glb`, consistent with every other scene family.
    public IReadOnlyList<ISceneRecipe> Scenes { get; } =
    [
        new SceneRecipe("model"),
        new SceneRecipe("grid"),
        new SceneRecipe("drop"),
        new SceneRecipe("metalrough"),
        new SceneRecipe("planet"),
        new SceneRecipe("planet-drop"),
        new SceneRecipe("planet-challenge"),
        new SceneRecipe("drive"),
    ];

    // Contenu-3c: the scene-system factory registry.
    public IReadOnlyList<ISceneSystemFactory> SceneSystems { get; } =
        [new ProbeDropSystemFactory(), new LandingChallengeSystemFactory(), new DriveControlSystemFactory()];
}
