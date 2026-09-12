using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Agapanthe.Assets.Scene;
using Agapanthe.Core;
using Agapanthe.Scene;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// Contenu-3b — <see cref="SceneMaterializer"/> turns a cooked <see cref="SceneDefinition"/> into live entities
/// in a <see cref="GameWorld"/> with no GPU: <c>MeshRef</c> stays invalid, <c>AssetRef</c> carries the identity,
/// bodies attach when the entity has one, and the <c>[physics]</c> block comes back as <see cref="PhysicsSettings"/>.
/// </summary>
[Collection("World")]
public sealed class SceneMaterializerTests
{
    private static ModelAsset FakeModel(string name, int meshes = 1) => new()
    {
        Meshes = Enumerable.Range(0, meshes).Select(i => new MeshAsset
        {
            Positions = [new(-1, -1, -1), new(1, 1, 1)],
            Indices = [0, 1, 0],
            MaterialIndex = 0,
            WorldTransform = Matrix4x4.CreateTranslation(i, 0, 0),
            BoundsCenter = Vector3.Zero,
            BoundsRadius = 1.7320508f,
        }).ToArray(),
        Materials = [new MaterialAsset { Name = "m" }],
        Images = [],
        Name = name,
    };

    private static Func<AssetKey, ModelAsset> Loader(params string[] keys)
    {
        var set = keys.ToHashSet(StringComparer.Ordinal);
        return k => set.Contains(k.Value!) ? FakeModel(k.Value!) : throw new AssetException($"no cooked model '{k}'");
    }

    private static SceneDefinition Scene(
        IReadOnlyList<SceneEntity> entities, ScenePhysics? physics = null, string? restore = null,
        IReadOnlyList<SceneSystem>? systems = null) => new()
    {
        Name = "t",
        WorldOrigin = Double3.Zero,
        Entities = entities,
        Lights = [],
        Ambient = Vector3.Zero,
        Camera = new SceneCamera { Mode = SceneCameraMode.FrameBounds, FovY = 60f },
        Environment = new SceneEnvironment { Mode = SceneEnvironmentMode.None },
        Physics = physics,
        Restore = restore is null ? null : new SceneRestore { SnapshotPath = restore },
        Systems = systems ?? [],
    };

    private static SceneEntity Entity(string model, int mesh = 0, SceneBody? body = null) => new()
    {
        Model = new AssetKey(model),
        LocalMesh = mesh,
        LocalMat = 0,
        Position = Double3.Zero,
        Body = body,
    };

    [Fact]
    public void Materialize_PopulatesWorld_Headless_KeepsMeshRefInvalid()
    {
        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;

        var result = SceneMaterializer.Materialize(
            Scene([Entity("models/x.glb")]), Loader("models/x.glb"), world, 1f / 60f);

        Assert.Equal(1, world.LiveEntityCount);
        Assert.Equal(new MeshRefKey(new AssetKey("models/x.glb"), 0, 0), world.AssetRefForTest(id));
        var handles = world.MeshRefForTest(id);
        Assert.NotNull(handles);
        Assert.False(handles!.Value.Mesh.IsValid); // no GPU → no resolution
        Assert.Null(result.Physics);
        Assert.Single(result.Models);
    }

    [Fact]
    public void Materialize_MultiMeshModel_SpawnsOneEntityPerReferencedMesh()
    {
        using var world = new GameWorld();
        var def = Scene([Entity("models/x.glb", mesh: 0), Entity("models/x.glb", mesh: 2)]);

        SceneMaterializer.Materialize(def, k => FakeModel(k.Value!, meshes: 3), world, 1f / 60f);

        Assert.Equal(2, world.LiveEntityCount);
    }

    [Fact]
    public void Materialize_EntityWithBody_AttachesPhysicsBody_And_PhysicsBlockReturned()
    {
        using var world = new GameWorld();
        var body = new SceneBody { Velocity = new Vector3(0, 1, 0), InverseMass = 1f, Restitution = 0.3f, Radius = 2f };
        var def = Scene(
            [Entity("models/x.glb", body: body)],
            physics: new ScenePhysics { Gravity = new Vector3(0, -9.81f, 0), GroundY = -5f });

        var result = SceneMaterializer.Materialize(def, Loader("models/x.glb"), world, 1f / 120f);

        Assert.Equal(1, world.LiveEntityCount);
        Assert.NotNull(result.Physics);
        Assert.Equal(new Vector3(0, -9.81f, 0), result.Physics!.Value.Gravity);
        Assert.Equal(-5f, result.Physics!.Value.GroundY);
        Assert.Equal(1f / 120f, result.Physics!.Value.FixedDt);
    }

    [Fact]
    public void Materialize_AttractorPhysics_AppliesWithAttractor()
    {
        // Audit finding (both csharp-lowlevel and engine-architect, 🔴 blocking): Mu/AttractorCenter/SurfaceRadius
        // were parsed, compiled, and round-tripped through the binary format, but Materialize silently dropped
        // them — every attractor scene (planet-drop) ran with NO gravity at all. This is the regression test.
        using var world = new GameWorld();
        var physics = new ScenePhysics
        {
            Gravity = Vector3.Zero, GroundY = 0f,
            Mu = 2.03e14, AttractorCenter = new Double3(1, 2, 3), SurfaceRadius = 3185500.0,
        };
        var result = SceneMaterializer.Materialize(Scene([Entity("models/x.glb")], physics: physics), Loader("models/x.glb"), world, 1f / 60f);

        Assert.NotNull(result.Physics);
        Assert.Equal(2.03e14, result.Physics!.Value.Mu);
        Assert.Equal(new Double3(1, 2, 3), result.Physics.Value.AttractorCenter);
        Assert.Equal(3185500.0, result.Physics.Value.SurfaceRadius);
    }

    [Fact]
    public void Materialize_MuZero_TakesTheUniformGravityPath_ByteIdenticalToBefore()
    {
        // Mu == 0 (the default) must keep using the 3-arg ctor — the uniform-gravity path (e.g. the `drop`
        // scene's cluster) stays byte-identical, matching PhysicsSettings' own "Mu == 0 ⇒ no attractor" contract.
        using var world = new GameWorld();
        var physics = new ScenePhysics { Gravity = new Vector3(0, -9.81f, 0), GroundY = -5f };
        var result = SceneMaterializer.Materialize(Scene([Entity("models/x.glb")], physics: physics), Loader("models/x.glb"), world, 1f / 60f);

        Assert.NotNull(result.Physics);
        Assert.Equal(0.0, result.Physics!.Value.Mu);
        Assert.Equal(new Vector3(0, -9.81f, 0), result.Physics.Value.Gravity);
    }

    [Fact]
    public void Materialize_SpawnEntitiesFalse_LeavesWorldEmpty_ButStillLoadsModelsAndPhysics()
    {
        // Contenu-3c-2 (fixes a 🔴 both audits found): a restore-pending caller must be able to keep the world
        // empty for GameWorld.Load (which hard-throws on a non-empty world) while still getting every model the
        // restored entities will need already decoded, and the scene's PhysicsSystem still attached.
        using var world = new GameWorld();
        var physics = new ScenePhysics { Gravity = new Vector3(0, -9.81f, 0), GroundY = -5f };
        var def = Scene([Entity("models/x.glb"), Entity("models/y.glb")], physics: physics);

        var result = SceneMaterializer.Materialize(def, Loader("models/x.glb", "models/y.glb"), world, 1f / 60f, spawnEntities: false);

        Assert.Equal(0, world.LiveEntityCount);
        Assert.Equal(2, result.Models.Count);
        Assert.True(result.Models.ContainsKey(new AssetKey("models/x.glb")));
        Assert.True(result.Models.ContainsKey(new AssetKey("models/y.glb")));
        Assert.NotNull(result.Physics);
        Assert.Equal(new Vector3(0, -9.81f, 0), result.Physics!.Value.Gravity);
    }

    [Fact]
    public void Materialize_SpawnEntitiesTrue_IsTheDefault()
    {
        using var world = new GameWorld();
        var result = SceneMaterializer.Materialize(Scene([Entity("models/x.glb")]), Loader("models/x.glb"), world, 1f / 60f);

        Assert.Equal(1, world.LiveEntityCount);
    }

    [Fact]
    public void Materialize_IdentityPlacement_ComposesTransform_LikeTheRenderPath()
    {
        // The render path (ResourceRegistry.Load → SceneBuilder.BuildEntries) splits mesh.WorldTransform into a
        // Double3 position + a zero-translation matrix, then adds worldOrigin. For an identity-placed entity the
        // materializer must produce the byte-identical result — the cooked-scene capture fidelity guarantee.
        var rot = Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateScale(2f);
        var wt = rot;
        wt.M41 = 3f;
        wt.M42 = -4f;
        wt.M43 = 5f;

        var model = new ModelAsset
        {
            Meshes = [new MeshAsset
            {
                Positions = [Vector3.Zero], Indices = [0], MaterialIndex = 0,
                WorldTransform = wt, BoundsCenter = Vector3.Zero, BoundsRadius = 1f,
            }],
            Materials = [new MaterialAsset { Name = "m" }],
            Images = [],
            Name = "x",
        };

        using var world = new GameWorld();
        var id = world.NextGlobalIdForTest;
        var def = Scene([Entity("models/x.glb")]) with { WorldOrigin = new Double3(100, 0, -50) };

        SceneMaterializer.Materialize(def, _ => model, world, 1f / 60f);

        var (rotationScale, position) = world.WorldTransformForTest(id)!.Value;
        Assert.Equal(new Double3(103, -4, -45), position); // worldOrigin + meshTranslation
        Assert.Equal(rot, rotationScale);                  // translation row zeroed → back to `rot`
    }

    [Fact]
    public void Materialize_LocalMatOutOfRange_Throws()
    {
        // Audit finding (csharp-lowlevel): headless has no resolver to catch a bad material index later — the
        // materializer itself must reject it, the same way it already rejects a bad LocalMesh.
        using var world = new GameWorld();
        var entity = Entity("models/x.glb") with { LocalMat = 7 }; // FakeModel has 1 material (index 0)
        Assert.Throws<AssetException>(() => SceneMaterializer.Materialize(
            Scene([entity]), Loader("models/x.glb"), world, 1f / 60f));
    }

    [Fact]
    public void Materialize_LocalMat_EqualToMaterialsCount_IsValid()
    {
        // Materials.Count itself names the model's appended engine-default material (SceneCompiler/SceneBuilder
        // convention) — a valid index, not an off-by-one error.
        using var world = new GameWorld();
        var entity = Entity("models/x.glb") with { LocalMat = 1 }; // FakeModel has exactly 1 material
        SceneMaterializer.Materialize(Scene([entity]), Loader("models/x.glb"), world, 1f / 60f);
        Assert.Equal(1, world.LiveEntityCount);
    }

    [Fact]
    public void Materialize_UnknownModelKey_Throws()
    {
        using var world = new GameWorld();
        Assert.Throws<AssetException>(() => SceneMaterializer.Materialize(
            Scene([Entity("models/missing.glb")]), Loader("models/x.glb"), world, 1f / 60f));
    }

    [Fact]
    public void Materialize_RestorePath_FlowsToResult()
    {
        using var world = new GameWorld();
        var result = SceneMaterializer.Materialize(
            Scene([Entity("models/x.glb")], restore: "save/world.agworld"), Loader("models/x.glb"), world, 1f / 60f);

        Assert.Equal("save/world.agworld", result.RestorePath);
    }

    // --- Contenu-3c: SceneSystem.ProbeModel loading + BuildRuntimeTemplate --------------------------------------

    [Fact]
    public void Materialize_ProbeOnlyModel_NotReferencedByAnyEntity_StillLandsInModels()
    {
        using var world = new GameWorld();
        var system = new SceneSystem { Kind = SceneSystemKind.ProbeDrop, ProbeModel = new AssetKey("procedural/probe"), ProbeRadius = 3f };

        var result = SceneMaterializer.Materialize(
            Scene([Entity("models/x.glb")], systems: [system]), Loader("models/x.glb", "procedural/probe"), world, 1f / 60f);

        Assert.True(result.Models.ContainsKey(new AssetKey("procedural/probe")));
        Assert.Equal(1, world.LiveEntityCount); // the probe model spawns no static entity of its own
    }

    [Fact]
    public void BuildRuntimeTemplate_ProducesGpuFreeSpecFromMeshBoundsAndTransform()
    {
        var models = new Dictionary<AssetKey, ModelAsset> { [new AssetKey("procedural/probe")] = FakeModel("probe") };

        var template = SceneMaterializer.BuildRuntimeTemplate(new AssetKey("procedural/probe"), 0, 0, models);

        Assert.False(template.Mesh.IsValid);
        Assert.False(template.Material.IsValid);
        Assert.Equal(Vector3.Zero, template.BoundsCenter);
        Assert.Equal(1.7320508f, template.BoundsRadius);
        Assert.Equal(new MeshRefKey(new AssetKey("procedural/probe"), 0, 0), template.Identity);
    }

    [Fact]
    public void BuildRuntimeTemplate_UnknownModel_Throws()
    {
        var models = new Dictionary<AssetKey, ModelAsset>();
        Assert.Throws<AssetException>(() => SceneMaterializer.BuildRuntimeTemplate(new AssetKey("procedural/missing"), 0, 0, models));
    }

    [Fact]
    public void BuildRuntimeTemplate_MeshOutOfRange_Throws()
    {
        var models = new Dictionary<AssetKey, ModelAsset> { [new AssetKey("procedural/probe")] = FakeModel("probe") };
        Assert.Throws<AssetException>(() => SceneMaterializer.BuildRuntimeTemplate(new AssetKey("procedural/probe"), 5, 0, models));
    }
}
