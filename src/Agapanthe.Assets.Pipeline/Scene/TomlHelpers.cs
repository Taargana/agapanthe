using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Core;
using Tomlyn.Model;

namespace Agapanthe.Assets.Pipeline.Scene;

/// <summary>Contenu-3c — the scalar/vector/table TOML helpers <see cref="SceneTomlReader"/> used to keep private
/// to itself, extracted so the cook-time procedural-generator readers (<c>Agapanthe.Assets.Pipeline.Procedural</c>)
/// share them instead of duplicating. Pure refactor of Contenu-3b's code — no behavior change.</summary>
internal static class TomlHelpers
{
    // Contenu-3b audit (engine-architect F1): the class doc and the design spec both promise an unknown key is
    // rejected — nothing enforced it. A typo (`fov` for `fov_y`, `spacing` for `spacing_mul`) silently took the
    // default instead of failing the cook, and the author debugged the renderer instead of the file.
    public static void RejectUnknownKeys(TomlTable t, string path, string context, string[] known)
    {
        var unknown = t.Keys.Where(k => !known.Contains(k, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0)
        {
            throw new AssetException(
                $"'{path}': unknown key(s) in [{context}]: {string.Join(", ", unknown)}. "
                + $"Known keys: {string.Join(", ", known)}.");
        }
    }

    public static TomlTable AsTable(object? o, string where, string path)
        => o as TomlTable ?? throw new AssetException($"'{path}': '{where}' must be a table.");

    public static string? Str(TomlTable t, string key, string path)
        => t.TryGetValue(key, out var v)
            ? v as string ?? throw new AssetException($"'{path}': '{key}' must be a string.")
            : null;

    public static double? NumOpt(TomlTable t, string key, string path)
    {
        if (!t.TryGetValue(key, out var v))
        {
            return null;
        }

        return v switch
        {
            long l => l,
            double d => d,
            _ => throw new AssetException($"'{path}': '{key}' must be a number."),
        };
    }

    public static bool? BoolOpt(TomlTable t, string key, string path)
        => t.TryGetValue(key, out var v)
            ? v as bool? ?? throw new AssetException($"'{path}': '{key}' must be a boolean.")
            : null;

    public static float[] Floats(TomlTable t, string key, string path, int n)
    {
        if (t[key] is not TomlArray a || a.Count != n)
        {
            throw new AssetException($"'{path}': '{key}' must be an array of {n} numbers.");
        }

        var r = new float[n];
        for (var i = 0; i < n; i++)
        {
            r[i] = a[i] switch
            {
                long l => l,
                double d => (float)d,
                _ => throw new AssetException($"'{path}': '{key}'[{i}] is not a number."),
            };
        }

        return r;
    }

    public static Vector3? Vec3Opt(TomlTable t, string key, string path)
    {
        if (!t.ContainsKey(key))
        {
            return null;
        }

        var f = Floats(t, key, path, 3);
        return new Vector3(f[0], f[1], f[2]);
    }

    // Contenu-3b audit (engine-architect F2): `Double3Opt` used to route through `Floats` (an f32 array), silently
    // quantizing a large-magnitude authored position (world_origin, a point light, a fixed camera — precisely the
    // fields the engine's double-precision world exists for) down to float precision before ever reaching the f64
    // blob field. `Doubles()` keeps full precision from the TOML number through to the .agscene payload.
    public static double[] Doubles(TomlTable t, string key, string path, int n)
    {
        if (t[key] is not TomlArray a || a.Count != n)
        {
            throw new AssetException($"'{path}': '{key}' must be an array of {n} numbers.");
        }

        var r = new double[n];
        for (var i = 0; i < n; i++)
        {
            r[i] = a[i] switch
            {
                long l => l,
                double d => d,
                _ => throw new AssetException($"'{path}': '{key}'[{i}] is not a number."),
            };
        }

        return r;
    }

    public static Double3? Double3Opt(TomlTable t, string key, string path)
    {
        if (!t.ContainsKey(key))
        {
            return null;
        }

        var d = Doubles(t, key, path, 3);
        return new Double3(d[0], d[1], d[2]);
    }

    public static Quaternion? QuatOpt(TomlTable t, string key, string path)
    {
        if (!t.ContainsKey(key))
        {
            return null;
        }

        var f = Floats(t, key, path, 4);
        return new Quaternion(f[0], f[1], f[2], f[3]);
    }
}
