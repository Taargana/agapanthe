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

    // Contenu-3b/3c: the `model` family (single / grid / cluster) and now `planet`/`planet-drop` are cooked
    // data — one generic `SceneRecipe` per `content/scenes/*.toml`. `planet-challenge` + `drive` stay
    // hand-coded (migrate in 3c-2/3c-3): `planet-challenge` still needs `LandingChallengeSystem` (no factory
    // registered for it yet), `drive` still needs its CLI arbitrary-model argument.
    public IReadOnlyList<ISceneRecipe> Scenes { get; } =
    [
        new PlanetChallengeSceneRecipe(),
        new DriveSceneRecipe(),
        new SceneRecipe("model"),
        new SceneRecipe("grid"),
        new SceneRecipe("drop"),
        new SceneRecipe("metalrough"),
        new SceneRecipe("planet"),
        new SceneRecipe("planet-drop"),
    ];

    // Contenu-3c: the scene-system factory registry. ProbeDropSystemFactory has no consumer yet (planet-drop
    // migrates in Wave 7) — registering it now is harmless (SceneRecipe only dispatches a scene's OWN declared
    // Systems list) and keeps this wave's registry wiring complete and testable in isolation.
    public IReadOnlyList<ISceneSystemFactory> SceneSystems { get; } = [new ProbeDropSystemFactory()];
}
