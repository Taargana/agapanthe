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
    /// <c>AGAPANTHE_SCENE</c> (or <see cref="DefaultScene"/>); there is no in-process switching in this milestone.</summary>
    IReadOnlyList<ISceneRecipe> Scenes { get; }

    /// <summary>The <see cref="ISceneRecipe.Name"/> chosen when <c>AGAPANTHE_SCENE</c> is unset.</summary>
    string DefaultScene { get; }

    /// <summary>This game's universe identity (MP-0b). Defaults to <see cref="UniverseId.None"/> — the honest
    /// "unidentified" state that keeps JIT/AOT determinism. <see cref="AppHost"/> lets <c>AGAPANTHE_UNIVERSE</c>
    /// override it.</summary>
    UniverseId Universe => UniverseId.None;
}
