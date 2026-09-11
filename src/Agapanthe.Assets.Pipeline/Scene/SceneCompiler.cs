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
                    Place(entities, members, item.Position, item.Rotation, item.Scale, item.CastsShadow, body: null, loadModel);
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

        return new SceneDefinition
        {
            Name = scene.Name,
            WorldOrigin = scene.WorldOrigin,
            Entities = entities,
            Lights = scene.Lights.Select(ToLight).ToArray(),
            Ambient = scene.Ambient,
            Camera = ToCamera(scene.Camera),
            Environment = scene.Environment.Hdri is { Length: > 0 } h
                ? new SceneEnvironment { Mode = SceneEnvironmentMode.HdriPath, HdriPath = h }
                : new SceneEnvironment { Mode = SceneEnvironmentMode.None },
            Physics = scene.Physics is { } p ? new ScenePhysics { Gravity = p.Gravity, GroundY = p.GroundY } : null,
            Restore = scene.Restore is { } r ? new SceneRestore { SnapshotPath = r.Snapshot } : null,
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
        },
        _ => throw new AssetException($"unknown camera mode '{c.Mode}' (frame-bounds|fixed)."),
    };
}
