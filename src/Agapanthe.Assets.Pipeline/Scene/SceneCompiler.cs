using System.IO;
using System.Linq;
using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Agapanthe.Assets.Scene;
using Agapanthe.Core;

namespace Agapanthe.Assets.Pipeline.Scene;

/// <summary>
/// Contenu-3b — expands an <see cref="AuthoredScene"/>'s directives (<c>[[entity]]</c>, <c>[[grid]]</c>,
/// <c>[[cluster]]</c>, prefab instances) into a <b>flat</b> <see cref="SceneDefinition"/> at cook time. The
/// grid stride and the cube-cluster <c>Hash(i)</c> jitter are copied verbatim from
/// <c>Sandbox.ModelContent.SpawnGrid</c>/<c>SpawnDropScene</c> so a cooked scene reproduces the old code path.
/// </summary>
internal static class SceneCompiler
{
    public static SceneDefinition Compile(
        AuthoredScene scene,
        Func<AssetKey, ModelAsset> loadModel,
        Func<string, AuthoredPrefab> loadPrefab)
    {
        var entities = new List<SceneEntity>();

        foreach (var item in scene.Items)
        {
            // Resolve the item to a set of (model key, local placement) — a prefab expands to its entities,
            // a bare `model` is a single entity at identity.
            var members = ResolveMembers(item, loadPrefab);

            switch (item.Kind)
            {
                case AuthoredItemKind.Entity:
                    // Contenu-3c-3: a bare [[entity]] can opt into a single physics body (`body = true`) — the
                    // `drive` scene's one steerable body, mirroring how [[cluster]] already builds SceneBody from
                    // the same InverseMass/Restitution/Radius fields.
                    var entityBody = item.HasBody
                        ? new SceneBody { Velocity = item.Velocity, InverseMass = item.InverseMass, Restitution = item.Restitution, Radius = item.Radius ?? 1f }
                        : null;
                    var beforeCount = entities.Count;
                    Place(entities, members, item.Position, item.Rotation, item.Scale, item.CastsShadow, entityBody, loadModel);

                    // Audit finding (3c-3, both csharp-lowlevel and engine-architect, 🟠): `body = true` reads as
                    // "this entity is ONE body" — but Place emits one SceneEntity per (prefab member × mesh), and
                    // stamps the SAME SceneBody onto every one of them. A multi-mesh model or multi-member prefab
                    // would silently spawn N co-located, mutually-penetrating rigid bodies at cook time, with no
                    // runtime signal (DriveControl's controlled_entity_index would steer only one of the pile).
                    // Reject the ambiguity at cook time rather than let a future author discover it as a physics
                    // bug.
                    if (item.HasBody && entities.Count - beforeCount != 1)
                    {
                        throw new AssetException(
                            $"'{item.Model ?? item.Prefab}': body=true requires exactly one drawable (one mesh, "
                            + $"one prefab member) — got {entities.Count - beforeCount}.");
                    }

                    break;

                case AuthoredItemKind.Grid:
                    EmitGrid(entities, item, members, loadModel);
                    break;

                case AuthoredItemKind.Cluster:
                    EmitCluster(entities, item, members, loadModel);
                    break;

                default:
                    throw new AssetException($"unknown scene item kind {item.Kind}.");
            }
        }

        // Audit finding (3c-3, engine-architect — the one pre-existing 🟡 endorsed as worth closing before
        // Contenu-3c's domain close): SceneMaterializer.Compose adds WorldOrigin to every entity's Position, but
        // ScenePhysics.AttractorCenter and SceneSystem.Centre/ZoneCenter are consumed raw — a scene with a
        // non-zero world_origin AND an attractor/probe/zone would silently place the entities in one frame and
        // the physics/gameplay geometry in another. Inert today (every shipped scene uses world_origin = 0);
        // reject the combination at cook time rather than leave it a silent-wrong-answer landmine for whichever
        // scene first wants a non-zero origin (the planet family, at ~1e10 m, is the obvious future candidate).
        if (scene.WorldOrigin != Double3.Zero)
        {
            if (scene.Physics is { AttractorMu: > 0.0 })
            {
                throw new AssetException(
                    "a scene with a non-zero world_origin cannot use a physics attractor — AttractorCenter is "
                    + "not offset by world_origin (tracked debt). Author world_origin = 0 and bake the origin "
                    + "into entity/attractor positions directly.");
            }

            if (scene.Systems.Any(s => s.Kind is "probe_drop" or "landing_challenge"))
            {
                throw new AssetException(
                    "a scene with a non-zero world_origin cannot declare a probe_drop/landing_challenge system — "
                    + "Centre/ZoneCenter are not offset by world_origin (tracked debt).");
            }
        }

        return new SceneDefinition
        {
            Name = scene.Name,
            WorldOrigin = scene.WorldOrigin,
            Entities = entities,
            Lights = scene.Lights.Select(ToLight).ToArray(),
            Ambient = scene.Ambient,
            Camera = ToCamera(scene.Camera),
            Environment = ToEnvironment(scene.Environment),
            Physics = scene.Physics is { } p
                ? new ScenePhysics
                {
                    Gravity = p.Gravity, GroundY = p.GroundY,
                    Mu = p.AttractorMu, AttractorCenter = p.AttractorCenter, SurfaceRadius = p.AttractorSurfaceRadius,
                }
                : null,
            Restore = scene.Restore is { } r ? new SceneRestore { SnapshotPath = r.Snapshot } : null,
            Systems = scene.Systems.Select(s => ToSystem(s, loadModel, scene.Physics, entities, scene.Restore is not null)).ToArray(),
        };
    }

    private readonly record struct Member(AssetKey Model, Double3 LocalOffset, Quaternion LocalRotation, float LocalScale, bool CastsShadow);

    private static List<Member> ResolveMembers(AuthoredItem item, Func<string, AuthoredPrefab> loadPrefab)
    {
        if (item.Model is { } m)
        {
            return [new Member(new AssetKey(m), default, Quaternion.Identity, 1f, item.CastsShadow)];
        }

        var prefab = loadPrefab(item.Prefab!);
        return prefab.Entities.Select(e => new Member(
            new AssetKey(e.Model!), e.Position, e.Rotation, e.Scale, e.CastsShadow)).ToList();
    }

    private static void Place(
        List<SceneEntity> sink, List<Member> members,
        Double3 position, Quaternion rotation, float scale, bool castsShadowOverride,
        SceneBody? body, Func<AssetKey, ModelAsset> loadModel)
    {
        // Contenu-3b audit (engine-architect F3): the instance's rotation/scale must carry the prefab member's
        // LOCAL OFFSET too, not just its own local rotation/scale — otherwise a rotated multi-part prefab instance
        // places its parts in the wrong spot (invisible with today's single-entity `helmet` prefab; wrong the
        // first time a rotated multi-part prefab is authored).
        var placement = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation);

        foreach (var member in members)
        {
            var model = loadModel(member.Model);
            var pos = position + new Double3(Vector3.Transform(member.LocalOffset.ToVector3(Double3.Zero), placement));
            var rot = Quaternion.Concatenate(member.LocalRotation, rotation);
            var scl = scale * member.LocalScale;

            for (var meshIdx = 0; meshIdx < model.Meshes.Count; meshIdx++)
            {
                // Contenu-3b audit (engine-architect F5): must match the render registry's local material indexing
                // (ResourceRegistry.Load / SceneBuilder.BuildEntries) — an out-of-range MaterialIndex resolves to
                // the model's APPENDED default material (index == Materials.Count), never index 0.
                var idx = model.Meshes[meshIdx].MaterialIndex;
                var localMat = idx >= 0 && idx < model.Materials.Count ? idx : model.Materials.Count;
                sink.Add(new SceneEntity
                {
                    Model = member.Model,
                    LocalMesh = meshIdx,
                    LocalMat = localMat,
                    Position = pos,
                    Rotation = rot,
                    Scale = scl,
                    CastsShadow = member.CastsShadow && castsShadowOverride,
                    Body = body,
                });
            }
        }
    }

    private static void EmitGrid(List<SceneEntity> sink, AuthoredItem item, List<Member> members, Func<AssetKey, ModelAsset> loadModel)
    {
        var rows = Math.Max(1, item.Rows);
        var cols = Math.Max(1, item.Cols);
        var diagonal = ModelDiagonal(members, loadModel);
        var spacing = diagonal * item.SpacingMul;

        var halfR = (rows - 1) * 0.5;
        var halfC = (cols - 1) * 0.5;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var offset = new Double3((c - halfC) * spacing, 0, (r - halfR) * spacing);
                Place(sink, members, item.Position + offset, item.Rotation, item.Scale, item.CastsShadow, body: null, loadModel);
            }
        }
    }

    private static void EmitCluster(List<SceneEntity> sink, AuthoredItem item, List<Member> members, Func<AssetKey, ModelAsset> loadModel)
    {
        var n = Math.Max(1, item.Count);

        // radius: max over the members' models (copied from SpawnDropScene).
        var radius = item.Radius ?? 1f;
        foreach (var member in members)
        {
            var model = loadModel(member.Model);
            foreach (var mesh in model.Meshes)
            {
                var rot = mesh.WorldTransform;
                rot.M41 = rot.M42 = rot.M43 = 0f;
                radius = MathF.Max(radius, mesh.BoundsRadius * MathHelpers.MaxStretch(rot));
            }
        }

        var side = Math.Max(1, (int)Math.Ceiling(Math.Cbrt(n)));
        var hspacing = radius * 2.1;
        var vspacing = radius * 2.2;
        var half = (side - 1) * 0.5;
        var perLayer = side * side;

        for (var i = 0; i < n; i++)
        {
            var layer = i / perLayer;
            var inLayer = i % perLayer;
            var cx = inLayer % side;
            var cz = inLayer / side;
            var h = Hash(i);
            var jx = (((h & 0xFFFF) / 65535.0) - 0.5) * radius * 0.4;
            var jz = ((((h >> 16) & 0xFFFF) / 65535.0) - 0.5) * radius * 0.4;
            var offset = new Double3(
                ((cx - half) * hspacing) + jx,
                (radius * 4.0) + (layer * vspacing),
                ((cz - half) * hspacing) + jz);

            var body = new SceneBody
            {
                Velocity = Vector3.Zero,
                InverseMass = item.InverseMass,
                Restitution = item.Restitution,
                Radius = radius,
            };
            Place(sink, members, item.Position + offset, item.Rotation, item.Scale, item.CastsShadow, body, loadModel);
        }

        static uint Hash(int i)
        {
            var x = (uint)i * 2654435761u;
            x ^= x >> 15;
            x *= 2246822519u;
            x ^= x >> 13;
            return x;
        }
    }

    private static double ModelDiagonal(List<Member> members, Func<AssetKey, ModelAsset> loadModel)
    {
        var min = new Double3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = new Double3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        var any = false;
        foreach (var member in members)
        {
            var model = loadModel(member.Model);
            foreach (var mesh in model.Meshes)
            {
                var rot = mesh.WorldTransform;
                var trans = new Double3(rot.M41, rot.M42, rot.M43);
                rot.M41 = rot.M42 = rot.M43 = 0f;
                var rr = mesh.BoundsRadius * MathHelpers.MaxStretch(rot);
                var c = member.LocalOffset + trans + new Double3(Vector3.Transform(mesh.BoundsCenter, rot));
                var vr = new Double3(rr, rr, rr);
                min = Double3.Min(min, c - vr);
                max = Double3.Max(max, c + vr);
                any = true;
            }
        }

        return any ? Math.Max(Double3.Distance(min, max), 1d) : 1d;
    }

    private static SceneLight ToLight(AuthoredLight l) => l.Kind.ToLowerInvariant() switch
    {
        // Audit finding (csharp-lowlevel): a zero direction survives to SceneLightRig.Apply's Vector3.Normalize
        // as (NaN,NaN,NaN) — CSM matrices go NaN, shadows break silently with no validation-layer message. Reject
        // it here, at cook time, rather than at every runtime consumer.
        "directional" => l.Direction.LengthSquared() > 1e-12f
            ? new SceneLight { Kind = SceneLightKind.Directional, Color = l.Color, Intensity = l.Intensity, Direction = l.Direction }
            : throw new AssetException($"directional light 'direction' must not be the zero vector."),
        "point" => new SceneLight { Kind = SceneLightKind.Point, Color = l.Color, Intensity = l.Intensity, Position = l.Position, Range = l.Range },
        _ => throw new AssetException($"unknown light kind '{l.Kind}' (directional|point)."),
    };

    private static SceneCamera ToCamera(AuthoredCamera c) => c.Mode.ToLowerInvariant() switch
    {
        "frame-bounds" => new SceneCamera
        {
            Mode = SceneCameraMode.FrameBounds, FovY = c.FovY, FreeFly = c.FreeFly,
            ViewDir = c.ViewDir, DistanceMul = (float)c.DistanceMul,
        },
        "fixed" => new SceneCamera
        {
            Mode = SceneCameraMode.Fixed, FovY = c.FovY, FreeFly = c.FreeFly,
            Position = c.Position, Yaw = c.Yaw, Pitch = c.Pitch, Near = c.Near, Far = c.Far,
            MoveSpeed = c.MoveSpeed, ShadowDistance = c.ShadowDistance,
        },
        _ => throw new AssetException($"unknown camera mode '{c.Mode}' (frame-bounds|fixed)."),
    };

    // Contenu-3c: hdri/procedural_sky/black are already validated mutually-exclusive by SceneTomlReader.
    private static SceneEnvironment ToEnvironment(AuthoredEnvironment e)
    {
        if (e.Hdri is { Length: > 0 } h)
        {
            return new SceneEnvironment { Mode = SceneEnvironmentMode.HdriPath, HdriPath = h };
        }

        if (e.ProceduralSky)
        {
            return new SceneEnvironment { Mode = SceneEnvironmentMode.ProceduralSky };
        }

        return e.Black
            ? new SceneEnvironment { Mode = SceneEnvironmentMode.Black }
            : new SceneEnvironment { Mode = SceneEnvironmentMode.None };
    }

    // Contenu-3c: resolves a [[system]]'s probe model the same way Place resolves a bare entity — mesh 0 (every
    // probe today is a dedicated single-mesh generated sphere), material via the appended-default-material
    // convention (Contenu-3b audit F5) — and validates both exist at cook time rather than at runtime materialize.
    // Contenu-3c-3: drive_control spawns no probe at all, so it's routed BEFORE the shared probe-model load
    // (AssetKey(s.ProbeModel) on an empty string would either throw or resolve to something meaningless — this
    // kind never touches that path).
    private static SceneSystem ToSystem(
        AuthoredSystem s, Func<AssetKey, ModelAsset> loadModel, AuthoredPhysics? physics, List<SceneEntity> entities, bool hasRestore)
    {
        if (s.Kind == "drive_control")
        {
            // Audit finding (3c-3, both csharp-lowlevel and engine-architect, 🟠): DriveControl resolves its
            // target via MaterializeResult.SpawnedEntities, which is entirely null when a restore is pending
            // (spawn is suppressed so GameWorld.Load can populate an empty world) — there is no "resolve later"
            // story for it, unlike LandingChallengeSystem's lazy count-based seed. Reject the combination at
            // cook time (SceneRecipe additionally rejects the runtime AGAPANTHE_LOAD override, which this can't see).
            if (hasRestore)
            {
                throw new AssetException(
                    "scene system kind=drive_control is incompatible with a [restore] block — it steers an entity "
                    + "spawned at cook time, which a restore's suppressed spawn pass never populates.");
            }

            if (s.ControlledEntityIndex < 0 || s.ControlledEntityIndex >= entities.Count)
            {
                throw new AssetException(
                    $"scene system kind=drive_control 'controlled_entity_index' {s.ControlledEntityIndex} is out of "
                    + $"range — the scene has {entities.Count} entities.");
            }

            if (entities[s.ControlledEntityIndex].Body is null)
            {
                throw new AssetException(
                    $"scene system kind=drive_control 'controlled_entity_index' {s.ControlledEntityIndex} names an "
                    + "entity with no [body] block — DriveControl steers a physics body, not a plain drawable.");
            }

            if (!float.IsFinite(s.MoveSpeed) || s.MoveSpeed <= 0f)
            {
                throw new AssetException($"scene system kind=drive_control 'move_speed' must be a positive finite number, got {s.MoveSpeed}.");
            }

            return new SceneSystem
            {
                Kind = SceneSystemKind.DriveControl, ControlledEntityIndex = s.ControlledEntityIndex, MoveSpeed = s.MoveSpeed,
            };
        }

        var key = new AssetKey(s.ProbeModel);
        var model = loadModel(key);
        if (model.Meshes.Count == 0)
        {
            throw new AssetException($"scene system references '{key}', which has no meshes.");
        }

        var mesh = model.Meshes[0];
        var localMat = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < model.Materials.Count
            ? mesh.MaterialIndex
            : model.Materials.Count;

        return s.Kind switch
        {
            "probe_drop" => new SceneSystem
            {
                Kind = SceneSystemKind.ProbeDrop, ProbeModel = key, ProbeLocalMesh = 0, ProbeLocalMat = localMat,
                ProbeRadius = s.ProbeRadius, Every = s.Every, Centre = s.Centre,
            },
            // A LandingChallenge system reads its attractor off the scene's own [physics] block at runtime
            // (MaterializeResult.Physics) rather than duplicating it here — but a scene declaring the system
            // with no attractor is an authoring mistake, caught at cook time instead of a runtime null-check.
            "landing_challenge" => physics is { AttractorMu: > 0.0, AttractorSurfaceRadius: > 0.0 }
                ? ValidateLandingChallengeFields(s) is { } err
                    ? throw new AssetException($"scene system kind=landing_challenge: {err}")
                    : new SceneSystem
                    {
                        Kind = SceneSystemKind.LandingChallenge, ProbeModel = key, ProbeLocalMesh = 0, ProbeLocalMat = localMat,
                        ProbeRadius = s.ProbeRadius, ZoneCenter = s.ZoneCenter, ZoneRadius = s.ZoneRadius,
                        SurfaceBand = s.SurfaceBand, DropHeight = s.DropHeight, TargetCount = s.TargetCount,
                        ShotBudget = s.ShotBudget, QuicksavePath = s.QuicksavePath,
                    }
                : throw new AssetException(
                    "scene system kind=landing_challenge requires a [physics] block with an attractor (mu > 0 AND "
                    + "surface_radius > 0) — the system reads both from the scene's own materialized physics, not a duplicated field."),
            _ => throw new AssetException($"unknown scene system kind '{s.Kind}' (probe_drop, landing_challenge, drive_control)."),
        };
    }

    // Audit finding (csharp-lowlevel, 🟡): the deleted PlanetContent.SetupPlanetChallenge clamped TargetCount/
    // ShotBudget to >= 1 (Math.Max(..., 1.0)) — that clamp was not carried over, and a negative count previously
    // reached AgSceneFormat.BuildPayload's `checked((uint)...)` as a raw OverflowException instead of a named
    // authoring error. NaN in any double field would rewrite as itself and make every LandingChallengeSystem
    // comparison silently false (the challenge becomes unwinnable, no probe ever counts) — caught here instead.
    private static string? ValidateLandingChallengeFields(AuthoredSystem s)
    {
        if (s.TargetCount < 1)
        {
            return $"'target_count' must be >= 1, got {s.TargetCount}.";
        }

        if (s.ShotBudget < 1)
        {
            return $"'shot_budget' must be >= 1, got {s.ShotBudget}.";
        }

        if (s.ShotBudget < s.TargetCount)
        {
            return $"'shot_budget' ({s.ShotBudget}) must be >= 'target_count' ({s.TargetCount}) — the challenge would be unwinnable.";
        }

        if (!double.IsFinite(s.ZoneRadius) || s.ZoneRadius <= 0.0)
        {
            return $"'zone_radius' must be a positive finite number, got {s.ZoneRadius}.";
        }

        if (!double.IsFinite(s.SurfaceBand) || s.SurfaceBand <= 0.0)
        {
            return $"'surface_band' must be a positive finite number, got {s.SurfaceBand}.";
        }

        if (!double.IsFinite(s.DropHeight) || s.DropHeight <= 0.0)
        {
            return $"'drop_height' must be a positive finite number, got {s.DropHeight}.";
        }

        if (!float.IsFinite(s.ProbeRadius) || s.ProbeRadius <= 0f)
        {
            return $"'probe_radius' must be a positive finite number, got {s.ProbeRadius}.";
        }

        if (!double.IsFinite(s.ZoneCenter.X) || !double.IsFinite(s.ZoneCenter.Y) || !double.IsFinite(s.ZoneCenter.Z))
        {
            return $"'zone_center' must be finite, got {s.ZoneCenter}.";
        }

        // Audit finding (csharp-lowlevel, 🟡): quicksave_path is a File.Create target taken verbatim from a
        // cooked blob — reject an absolute path or a traversal segment at cook time (Contenu-2's audit applied
        // the same posture to blobPath: a path embedded in shipped content must not escape the content root).
        if (s.QuicksavePath is { Length: > 0 } qp
            && (Path.IsPathRooted(qp) || qp.Split('/', '\\').Any(seg => seg == "..")))
        {
            return $"'quicksave_path' must be a relative path with no '..' segment, got '{qp}'.";
        }

        return null;
    }
}
