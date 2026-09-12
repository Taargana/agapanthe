using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Core;
using Tomlyn;
using Tomlyn.Model;
using static Agapanthe.Assets.Pipeline.Scene.TomlHelpers;

namespace Agapanthe.Assets.Pipeline.Scene;

/// <summary>Contenu-3b — parses a `.toml` scene / prefab into an <see cref="AuthoredScene"/> / <see
/// cref="AuthoredPrefab"/>. Cook-side only (Tomlyn). Unknown keys / missing required fields / malformed arrays →
/// <see cref="AssetException"/> naming the file + the offending path.</summary>
internal static class SceneTomlReader
{
    private static readonly string[] SceneTopKeys =
        ["name", "world_origin", "ambient", "camera", "environment", "physics", "restore", "entity", "grid", "cluster", "light", "system"];
    private static readonly string[] PrefabTopKeys = ["entity"];
    private static readonly string[] ItemKeys =
    [
        "model", "prefab", "position", "rotation", "scale", "casts_shadow",
        "rows", "cols", "spacing_mul", "count", "inverse_mass", "restitution", "radius",
        "body", "velocity",
    ];
    private static readonly string[] LightKeys = ["kind", "color", "intensity", "direction", "position", "range"];
    private static readonly string[] CameraKeys =
    [
        "mode", "fov_y", "free_fly", "view_dir", "distance_mul", "position", "yaw", "pitch", "near", "far",
        "move_speed", "shadow_distance",
    ];
    private static readonly string[] EnvironmentKeys = ["hdri", "procedural_sky", "black"];
    private static readonly string[] PhysicsKeys = ["gravity", "ground_y", "mu", "attractor_center", "surface_radius"];
    private static readonly string[] RestoreKeys = ["snapshot"];
    // Contenu-3c: probe_drop's TOML surface. Keys not in this kind's own list are rejected (per-kind split, same
    // style [[grid]]/[[cluster]] already use).
    private static readonly string[] ProbeDropSystemKeys = ["kind", "probe_model", "probe_radius", "every", "centre"];

    // Contenu-3c-2: landing_challenge's TOML surface.
    private static readonly string[] LandingChallengeSystemKeys =
    [
        "kind", "probe_model", "probe_radius", "zone_center", "zone_radius", "surface_band", "drop_height",
        "target_count", "shot_budget", "quicksave_path",
    ];

    // Contenu-3c-3: drive_control's TOML surface — no probe fields at all.
    private static readonly string[] DriveControlSystemKeys = ["kind", "controlled_entity_index", "move_speed"];

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
        foreach (var s in ReadSystems(table, path)) scene.Systems.Add(s);

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

            // Audit finding (3c-3, engine-architect, 🟡): 'body'/'velocity' are only meaningful on a bare
            // [[entity]] (Compile's Grid/Cluster branches never read AuthoredItem.HasBody/Velocity — Grid always
            // passes body: null, Cluster builds its OWN SceneBody from InverseMass/Restitution/Radius). Letting
            // them parse silently on [[grid]]/[[cluster]] would violate RejectUnknownKeys' own guarantee that no
            // authoring key is accepted and then ignored.
            if (kind != AuthoredItemKind.Entity && (t.ContainsKey("body") || t.ContainsKey("velocity")))
            {
                throw new AssetException($"'{path}': 'body'/'velocity' are only valid on [[entity]], not [[{key}]].");
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
                HasBody = BoolOpt(t, "body", path) ?? false,
                Velocity = Vec3Opt(t, "velocity", path) ?? Vector3.Zero,
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

    // Contenu-3c: [[system]] — a per-kind required/rejected key split (only "kind" is common up front; the rest
    // of the allowed set depends on it, same idea as [[grid]] vs [[cluster]] sharing ItemKeys but this one is
    // stricter because there is only ever one kind live at a time today).
    private static IEnumerable<AuthoredSystem> ReadSystems(TomlTable table, string path)
    {
        if (!table.TryGetValue("system", out var raw))
        {
            yield break;
        }

        if (raw is not TomlTableArray arr)
        {
            throw new AssetException($"'{path}': [[system]] must be an array of tables.");
        }

        foreach (var t in arr)
        {
            var kind = Str(t, "kind", path)
                       ?? throw new AssetException($"'{path}': a [[system]] is missing 'kind'.");
            yield return kind switch
            {
                "probe_drop" => ReadProbeDropSystem(t, path),
                "landing_challenge" => ReadLandingChallengeSystem(t, path),
                "drive_control" => ReadDriveControlSystem(t, path),
                _ => throw new AssetException($"'{path}': unknown [[system]] kind '{kind}' (probe_drop, landing_challenge, drive_control)."),
            };
        }
    }

    private static AuthoredSystem ReadProbeDropSystem(TomlTable t, string path)
    {
        RejectUnknownKeys(t, path, "system (probe_drop)", ProbeDropSystemKeys);
        return new AuthoredSystem
        {
            Kind = "probe_drop",
            ProbeModel = Str(t, "probe_model", path)
                         ?? throw new AssetException($"'{path}': [[system]] kind=probe_drop is missing 'probe_model'."),
            ProbeRadius = (float)(NumOpt(t, "probe_radius", path)
                          ?? throw new AssetException($"'{path}': [[system]] kind=probe_drop is missing 'probe_radius'.")),
            Every = (int)(NumOpt(t, "every", path) ?? 1),
            Centre = Double3Opt(t, "centre", path) ?? default,
        };
    }

    private static AuthoredSystem ReadLandingChallengeSystem(TomlTable t, string path)
    {
        RejectUnknownKeys(t, path, "system (landing_challenge)", LandingChallengeSystemKeys);
        return new AuthoredSystem
        {
            Kind = "landing_challenge",
            ProbeModel = Str(t, "probe_model", path)
                         ?? throw new AssetException($"'{path}': [[system]] kind=landing_challenge is missing 'probe_model'."),
            ProbeRadius = (float)(NumOpt(t, "probe_radius", path)
                          ?? throw new AssetException($"'{path}': [[system]] kind=landing_challenge is missing 'probe_radius'.")),
            ZoneCenter = Double3Opt(t, "zone_center", path)
                         ?? throw new AssetException($"'{path}': [[system]] kind=landing_challenge is missing 'zone_center'."),
            ZoneRadius = NumOpt(t, "zone_radius", path)
                         ?? throw new AssetException($"'{path}': [[system]] kind=landing_challenge is missing 'zone_radius'."),
            SurfaceBand = NumOpt(t, "surface_band", path)
                          ?? throw new AssetException($"'{path}': [[system]] kind=landing_challenge is missing 'surface_band'."),
            DropHeight = NumOpt(t, "drop_height", path)
                         ?? throw new AssetException($"'{path}': [[system]] kind=landing_challenge is missing 'drop_height'."),
            TargetCount = (int)(NumOpt(t, "target_count", path)
                          ?? throw new AssetException($"'{path}': [[system]] kind=landing_challenge is missing 'target_count'.")),
            ShotBudget = (int)(NumOpt(t, "shot_budget", path)
                         ?? throw new AssetException($"'{path}': [[system]] kind=landing_challenge is missing 'shot_budget'.")),
            QuicksavePath = Str(t, "quicksave_path", path) ?? "",
        };
    }

    private static AuthoredSystem ReadDriveControlSystem(TomlTable t, string path)
    {
        RejectUnknownKeys(t, path, "system (drive_control)", DriveControlSystemKeys);
        return new AuthoredSystem
        {
            Kind = "drive_control",
            ControlledEntityIndex = (int)(NumOpt(t, "controlled_entity_index", path)
                                    ?? throw new AssetException($"'{path}': [[system]] kind=drive_control is missing 'controlled_entity_index'.")),
            MoveSpeed = (float)(NumOpt(t, "move_speed", path)
                        ?? throw new AssetException($"'{path}': [[system]] kind=drive_control is missing 'move_speed'.")),
        };
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
            MoveSpeed = (float)(NumOpt(t, "move_speed", path) ?? 0.0),
            ShadowDistance = (float)(NumOpt(t, "shadow_distance", path) ?? 0.0),
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
        var hdri = Str(t, "hdri", path);
        var proceduralSky = BoolOpt(t, "procedural_sky", path) ?? false;
        var black = BoolOpt(t, "black", path) ?? false;

        var modeCount = (hdri is not null ? 1 : 0) + (proceduralSky ? 1 : 0) + (black ? 1 : 0);
        if (modeCount > 1)
        {
            throw new AssetException($"'{path}': [environment] must set at most one of 'hdri'/'procedural_sky'/'black'.");
        }

        return new AuthoredEnvironment { Hdri = hdri, ProceduralSky = proceduralSky, Black = black };
    }

    private static AuthoredPhysics ReadPhysics(TomlTable t, string path)
    {
        RejectUnknownKeys(t, path, "physics", PhysicsKeys);

        var mu = NumOpt(t, "mu", path);
        var attractorCenter = Double3Opt(t, "attractor_center", path);
        var surfaceRadius = NumOpt(t, "surface_radius", path);
        var attractorFieldCount = (mu is not null ? 1 : 0) + (attractorCenter is not null ? 1 : 0) + (surfaceRadius is not null ? 1 : 0);
        if (attractorFieldCount is not (0 or 3))
        {
            throw new AssetException(
                $"'{path}': [physics] 'mu'/'attractor_center'/'surface_radius' must be all present or all absent — got {attractorFieldCount} of 3.");
        }

        return new AuthoredPhysics
        {
            Gravity = Vec3Opt(t, "gravity", path) ?? new Vector3(0f, -9.81f, 0f),
            GroundY = (float)(NumOpt(t, "ground_y", path) ?? 0.0),
            AttractorMu = mu ?? 0.0,
            AttractorCenter = attractorCenter ?? default,
            AttractorSurfaceRadius = surfaceRadius ?? 0.0,
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
}
