using Agapanthe.World;

namespace Agapanthe.App;

/// <summary>
/// An application's definition — what <see cref="AppHost"/> needs to run it. Configuration, not callbacks: the
/// host owns the frame loop, the game declares its scenes and identity and each <see cref="ISceneRecipe"/>
/// populates the world + registers its systems.
/// </summary>
public interface IGame
{
    /// <summary>Display name — used as the Vulkan application/instance name (<c>GraphicsDevice</c>). The window
    /// title is the caller's concern (it constructs the concrete window).</summary>
    string Title { get; }

    /// <summary>Every scene this game can build. <see cref="AppHost"/> selects one at startup from
    /// <c>AGAPANTHE_SCENE</c> (or <see cref="DefaultScene"/>); a recipe may switch to another at runtime via
    /// <see cref="SimSceneContext.RequestSceneSwitch"/> (scene management spec).</summary>
    IReadOnlyList<ISceneRecipe> Scenes { get; }

    /// <summary>The <see cref="ISceneRecipe.Name"/> chosen when <c>AGAPANTHE_SCENE</c> is unset.</summary>
    string DefaultScene { get; }

    /// <summary>This game's universe identity (MP-0b). Defaults to <see cref="UniverseId.None"/> — the honest
    /// "unidentified" state that keeps JIT/AOT determinism. <see cref="AppHost"/> lets <c>AGAPANTHE_UNIVERSE</c>
    /// override it.</summary>
    UniverseId Universe => UniverseId.None;

    /// <summary>Contenu-3c: the scene-system factories this game knows how to construct, one per
    /// <see cref="Assets.Scene.SceneSystemKind"/> it supports. Defaults to none — a game whose scenes declare no
    /// <c>[[system]]</c> block needs no changes here. <see cref="SceneRecipe"/> dispatches by matching
    /// <see cref="ISceneSystemFactory.Kind"/> and throws for a kind with no registered factory.</summary>
    IReadOnlyList<ISceneSystemFactory> SceneSystems => [];

    /// <summary>
    /// Scene management spec, D5: the UI font this game's recipes draw text with, as a path relative to
    /// <see cref="AppContext.BaseDirectory"/> (e.g. <c>"fonts/Oswald-Bold.agfont"</c>) — <see cref="AppHost"/>
    /// resolves it the same way <see cref="Universe"/> is resolved (an <see cref="IGame"/>-level default, not a
    /// <c>HostOptions</c>/env-var override — a font choice is a game identity decision, not a per-run tuning knob).
    /// Defaults to <c>null</c>, meaning the engine's own default (<c>fonts/JetBrainsMono-Regular.agfont</c>) — every
    /// existing game (Sandbox, TopDown, ThinClient) keeps that behavior unchanged by not overriding this.
    /// </summary>
    string? FontPath => null;
}
