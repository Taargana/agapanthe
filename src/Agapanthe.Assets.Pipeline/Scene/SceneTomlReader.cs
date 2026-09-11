using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Core;
using Tomlyn;
using Tomlyn.Model;

namespace Agapanthe.Assets.Pipeline.Scene;

/// <summary>Contenu-3b — parses a `.toml` scene / prefab into an <see cref="AuthoredScene"/> / <see
/// cref="AuthoredPrefab"/>. Cook-side only (Tomlyn). Unknown keys / missing required fields / malformed arrays →
/// <see cref="AssetException"/> naming the file + the offending path.</summary>
internal static class SceneTomlReader
{
    private static readonly string[] SceneTopKeys =
        ["name", "world_origin", "ambient", "camera", "environment", "physics", "restore", "entity", "grid", "cluster", "light"];
    private static readonly string[] PrefabTopKeys = ["entity"];
    private static readonly string[] ItemKeys =
    [
        "model", "prefab", "position", "rotation", "scale", "casts_shadow",
        "rows", "cols", "spacing_mul", "count", "inverse_mass", "restitution", "radius",
    ];
    private static readonly string[] LightKeys = ["kind", "color", "intensity", "direction", "position", "range"];
    private static readonly string[] CameraKeys =
        ["mode", "fov_y", "free_fly", "view_dir", "distance_mul", "position", "yaw", "pitch", "near", "far"];
    private static readonly string[] EnvironmentKeys = ["hdri"];
    private static readonly string[] PhysicsKeys = ["gravity", "ground_y"];
    private static readonly string[] RestoreKeys = ["snapshot"];

    public static AuthoredScene ReadScene(string path)
    {
        var table = Parse(path);
        RejectUnknownKeys(table, path, "scene", SceneTopKeys);
        var scene = new AuthoredScene
        {
            Name = Str(table, "name", path) ?? Path.GetFileNameWithoutExtension(path),
            WorldOrigin = Double3Opt(table, "world_origin", path) ?? default,
            Ambient = Vec3Opt(table, "ambient", path) ?? new Vector3(0.08f, 0.08f, 0.09f),
            Camera = ReadCamera(table, path),
            Environment = ReadEnvironment(table, path),
            Physics = table.TryGetValue("physics", out var ph) ? ReadPhysics(AsTable(ph, "physics", path), path) : null,
            Restore = table.TryGetValue("restore", out var rs) ? ReadRestore(AsTable(rs, "restore", path), path) : null,
        };

        foreach (var it in ReadItems(table, "entity", path, AuthoredItemKind.Entity)) scene.Items.Add(it);
        foreach (var it in ReadItems(table, "grid", path, AuthoredItemKind.Grid)) scene.Items.Add(it);
        foreach (var it in ReadItems(table, "cluster", path, AuthoredItemKind.Cluster)) scene.Items.Add(it);
        foreach (var l in ReadLights(table, path)) scene.Lights.Add(l);

        if (scene.Items.Count == 0)
        {
            throw new AssetException($"scene '{path}' has no entities ([[entity]] / [[grid]] / [[cluster]]).");
        }

        return scene;
    }

    public static AuthoredPrefab ReadPrefab(string path)
    {
        var table = Parse(path);
        RejectUnknownKeys(table, path, "prefab", PrefabTopKeys);
        var prefab = new AuthoredPrefab();
        foreach (var it in ReadItems(table, "entity", path, AuthoredItemKind.Entity)) prefab.Entities.Add(it);
        if (prefab.Entities.Count == 0)
        {
            throw new AssetException($"prefab '{path}' has no [[entity]] blocks.");
        }

        return prefab;
    }

    // Contenu-3b audit (engine-architect F1): the class doc and the design spec both promise an unknown key is
    // rejected — nothing enforced it. A typo (`fov` for `fov_y`, `spacing` for `spacing_mul`) silently took the
    // default instead of failing the cook, and the author debugged the renderer instead of the file.
    private static void RejectUnknownKeys(TomlTable t, string path, string context, string[] known)
    {
        var unknown = t.Keys.Where(k => !known.Contains(k, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0)
        {
            throw new AssetException(
                $"'{path}': unknown key(s) in [{context}]: {string.Join(", ", unknown)}. "
                + $"Known keys: {string.Join(", ", known)}.");
        }
    }

    private static TomlTable Parse(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AssetException($"cannot read scene file '{path}': {ex.Message}", ex);
        }

        var result = Toml.Parse(text, path);
        if (result.HasErrors)
        {
            throw new AssetException($"scene file '{path}' has TOML syntax errors: {result.Diagnostics}");
        }

        return result.ToModel();
    }

    private static IEnumerable<AuthoredItem> ReadItems(TomlTable table, string key, string path, AuthoredItemKind kind)
    {
        if (!table.TryGetValue(key, out var raw))
        {
            yield break;
        }

        if (raw is not TomlTableArray arr)
        {
            throw new AssetException($"'{path}': [[{key}]] must be an array of tables.");
        }

        foreach (var t in arr)
        {
            RejectUnknownKeys(t, path, key, ItemKeys);
            var model = Str(t, "model", path);
            var prefab = Str(t, "prefab", path);
            if ((model is null) == (prefab is null))
            {
                throw new AssetException($"'{path}': each [[{key}]] needs exactly one of 'model' or 'prefab'.");
            }

            yield return new AuthoredItem
            {
                Kind = kind,
                Model = model,
                Prefab = prefab,
                Position = Double3Opt(t, "position", path) ?? default,
                Rotation = QuatOpt(t, "rotation", path) ?? Quaternion.Identity,
                Scale = (float)(NumOpt(t, "scale", path) ?? 1.0),
                CastsShadow = BoolOpt(t, "casts_shadow", path) ?? true,
                Rows = (int)(NumOpt(t, "rows", path) ?? 1),
                Cols = (int)(NumOpt(t, "cols", path) ?? 1),
                SpacingMul = NumOpt(t, "spacing_mul", path) ?? 1.5,
                Count = (int)(NumOpt(t, "count", path) ?? 1),
                InverseMass = (float)(NumOpt(t, "inverse_mass", path) ?? 1.0),
                Restitution = (float)(NumOpt(t, "restitution", path) ?? 0.3),
                Radius = NumOpt(t, "radius", path) is { } rr ? (float)rr : null,
            };
        }
    }

    private static IEnumerable<AuthoredLight> ReadLights(TomlTable table, string path)
    {
        if (!table.TryGetValue("light", out var raw))
        {
            yield break;
        }

        if (raw is not TomlTableArray arr)
        {
            throw new AssetException($"'{path}': [[light]] must be an array of tables.");
        }

        foreach (var t in arr)
        {
            RejectUnknownKeys(t, path, "light", LightKeys);
            var kind = Str(t, "kind", path)
                       ?? throw new AssetException($"'{path}': a [[light]] is missing 'kind' (directional|point).");
            yield return new AuthoredLight
            {
                Kind = kind,
                Color = Vec3Opt(t, "color", path) ?? Vector3.One,
                Intensity = (float)(NumOpt(t, "intensity", path) ?? 1.0),
                Direction = Vec3Opt(t, "direction", path) ?? new Vector3(0.4f, -0.7f, -0.6f),
                Position = Double3Opt(t, "position", path) ?? default,
                Range = (float)(NumOpt(t, "range", path) ?? 0.0),
            };
        }
    }

    private static AuthoredCamera ReadCamera(TomlTable table, string path)
    {
        if (!table.TryGetValue("camera", out var raw))
        {
            return new AuthoredCamera();
        }

        var t = AsTable(raw, "camera", path);
        RejectUnknownKeys(t, path, "camera", CameraKeys);
        return new AuthoredCamera
        {
            Mode = Str(t, "mode", path) ?? "frame-bounds",
            FovY = (float)(NumOpt(t, "fov_y", path) ?? 60.0),
            FreeFly = BoolOpt(t, "free_fly", path) ?? false,
            ViewDir = Vec3Opt(t, "view_dir", path) ?? new Vector3(0f, 0.35f, 1f),
            DistanceMul = NumOpt(t, "distance_mul", path) ?? 1.5,
            Position = Double3Opt(t, "position", path) ?? default,
            Yaw = (float)(NumOpt(t, "yaw", path) ?? 0.0),
            Pitch = (float)(NumOpt(t, "pitch", path) ?? 0.0),
            Near = (float)(NumOpt(t, "near", path) ?? 0.0),
            Far = (float)(NumOpt(t, "far", path) ?? 0.0),
        };
    }

    private static AuthoredEnvironment ReadEnvironment(TomlTable table, string path)
    {
        if (!table.TryGetValue("environment", out var raw))
        {
            return new AuthoredEnvironment();
        }

        var t = AsTable(raw, "environment", path);
        RejectUnknownKeys(t, path, "environment", EnvironmentKeys);
        return new AuthoredEnvironment { Hdri = Str(t, "hdri", path) };
    }

    private static AuthoredPhysics ReadPhysics(TomlTable t, string path)
    {
        RejectUnknownKeys(t, path, "physics", PhysicsKeys);
        return new AuthoredPhysics
        {
            Gravity = Vec3Opt(t, "gravity", path) ?? new Vector3(0f, -9.81f, 0f),
            GroundY = (float)(NumOpt(t, "ground_y", path) ?? 0.0),
        };
    }

    private static AuthoredRestore ReadRestore(TomlTable t, string path)
    {
        RejectUnknownKeys(t, path, "restore", RestoreKeys);
        return new AuthoredRestore
        {
            Snapshot = Str(t, "snapshot", path)
                       ?? throw new AssetException($"'{path}': [restore] is missing 'snapshot'."),
        };
    }

    // --- scalar helpers -------------------------------------------------------------------------------------

    private static TomlTable AsTable(object? o, string where, string path)
        => o as TomlTable ?? throw new AssetException($"'{path}': '{where}' must be a table.");

    private static string? Str(TomlTable t, string key, string path)
        => t.TryGetValue(key, out var v)
            ? v as string ?? throw new AssetException($"'{path}': '{key}' must be a string.")
            : null;

    private static double? NumOpt(TomlTable t, string key, string path)
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

    private static bool? BoolOpt(TomlTable t, string key, string path)
        => t.TryGetValue(key, out var v)
            ? v as bool? ?? throw new AssetException($"'{path}': '{key}' must be a boolean.")
            : null;

    private static float[] Floats(TomlTable t, string key, string path, int n)
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

    private static Vector3? Vec3Opt(TomlTable t, string key, string path)
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
    // blob field. A `Doubles()` sibling keeps full precision from the TOML number through to the .agscene payload.
    private static double[] Doubles(TomlTable t, string key, string path, int n)
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

    private static Double3? Double3Opt(TomlTable t, string key, string path)
    {
        if (!t.ContainsKey(key))
        {
            return null;
        }

        var d = Doubles(t, key, path, 3);
        return new Double3(d[0], d[1], d[2]);
    }

    private static Quaternion? QuatOpt(TomlTable t, string key, string path)
    {
        if (!t.ContainsKey(key))
        {
            return null;
        }

        var f = Floats(t, key, path, 4);
        return new Quaternion(f[0], f[1], f[2], f[3]);
    }
}
