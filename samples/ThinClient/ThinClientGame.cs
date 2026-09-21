using Agapanthe.App;

namespace ThinClient;

/// <summary>Net-1's thin client — a single, purely network-driven "scene" (D7: no <c>.agscene</c>, no cooked
/// scene definition, everything a drawable is comes from the dedicated server).</summary>
internal sealed class ThinClientGame : IGame
{
    public string Title => "Agapanthe ThinClient";

    public string DefaultScene => "thin-client";

    public IReadOnlyList<ISceneRecipe> Scenes { get; } = [new ThinClientSceneRecipe()];
}
