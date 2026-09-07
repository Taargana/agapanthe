using System.Globalization;
using System.Numerics;
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

    /// <summary>Reads a scalar env var as a double (invariant culture), falling back to <paramref name="fallback"/>.</summary>
    public static double EnvDouble(string name, double fallback)
        => Environment.GetEnvironmentVariable(name) is { } s
           && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;

    /// <summary>Reads an "x,y,z" env var as a Vector3, falling back to <paramref name="fallback"/>. A zero vector
    /// (which would Normalize to NaN downstream and blank the screen) is rejected back to the fallback.</summary>
    public static Vector3 EnvVector3(string name, Vector3 fallback)
    {
        if (Environment.GetEnvironmentVariable(name) is { } s)
        {
            var p = s.Split(',');
            if (p.Length == 3
                && float.TryParse(p[0], CultureInfo.InvariantCulture, out var x)
                && float.TryParse(p[1], CultureInfo.InvariantCulture, out var y)
                && float.TryParse(p[2], CultureInfo.InvariantCulture, out var z))
            {
                var v = new Vector3(x, y, z);
                if (v.LengthSquared() >= 1e-12f)
                {
                    return v;
                }

                Log.Warn($"Sandbox: {name}='{s}' is a zero vector; using the default direction.");
            }
        }

        return fallback;
    }
}
