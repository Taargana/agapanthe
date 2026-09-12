using Agapanthe.Assets.Model;

namespace Agapanthe.App;

/// <summary>
/// Contenu-3c — a pure-black environment (relocated verbatim from <c>Sandbox.PlanetContent.BuildBlackEnvironment</c>,
/// P3-M8): every texel zero → IBL bakes to zero ambient, skybox paints black. Used by the planet-family scenes
/// (no sky, just the planet + Sun sphere against void).
/// </summary>
public static class BlackEnvironment
{
    public static HdrImageAsset Build() => new() { RgbaPixels = new float[16 * 8 * 4], Width = 16, Height = 8 };
}
