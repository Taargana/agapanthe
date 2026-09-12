using System.Globalization;
using Agapanthe.Core;

namespace Sandbox;

/// <summary>Environment-variable parsing shared by the scene recipes (the host reads its own <c>AGAPANTHE_*</c>
/// via <c>HostOptions</c>; these are content/scene knobs).</summary>
internal static class SandboxEnv
{
    /// <summary>Parses <c>AGAPANTHE_WORLD_ORIGIN</c> / any "x,y,z" double triplet; anything unparseable → the origin.</summary>
    public static Double3 ParseDouble3(string? spec)
    {
        if (spec is not { Length: > 0 })
        {
            return Double3.Zero;
        }

        var parts = spec.Split(',');
        if (parts.Length == 3
            && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var x)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var y)
            && double.TryParse(parts[2], CultureInfo.InvariantCulture, out var z))
        {
            return new Double3(x, y, z);
        }

        Log.Warn($"Sandbox: could not parse '{spec}' as \"x,y,z\"; using the world origin.");
        return Double3.Zero;
    }

    // EnvDouble/EnvVector3 removed (Contenu-3c-2 audit finding): their last callers (PlanetContent's
    // scene-knob env vars) were deleted along with the hand-coded planet-challenge recipe — every planet-family
    // scene now reads baked TOML constants instead. ParseDouble3 survives (DriveSceneRecipe, 3c-3 scope).
}
