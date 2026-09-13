using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Agapanthe.Assets.Pipeline.Scene;
using Agapanthe.Assets.Scene;
using Agapanthe.Core;

namespace Agapanthe.Tests;

/// <summary>Contenu-3b — the cook-side scene compiler: TOML directives unroll into a flat
/// <see cref="SceneDefinition"/> (grid stride + cube-cluster jitter copied from the old Sandbox code).</summary>
public sealed class SceneCompilerTests
{
    // A 1-mesh, 1-material model with a unit-ish local sphere.
    private static ModelAsset FakeModel(string name) => new()
    {
        Meshes = [new MeshAsset
        {
            Positions = [new(-1, -1, -1), new(1, 1, 1)],
            Indices = [0, 1, 0],
            MaterialIndex = 0,
            BoundsCenter = Vector3.Zero,
            BoundsRadius = 1.7320508f,
        }],
        Materials = [new MaterialAsset { Name = "m" }],
        Images = [],
        Name = name,
    };

    private static Func<AssetKey, ModelAsset> Loader(params string[] keys)
    {
        var set = keys.ToHashSet(StringComparer.Ordinal);
        return k => set.Contains(k.Value!)
            ? FakeModel(k.Value!)
            : throw new AssetException($"no cooked model '{k}'");
    }

    private static AuthoredPrefab Prefab(string model)
    {
        var p = new AuthoredPrefab();
        p.Entities.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = model });
        return p;
    }

    [Fact]
    public void Grid_Unrolls_ToRowsTimesColsEntities_AtSpawnGridPositions()
    {
        var scene = new AuthoredScene { Name = "g" };
        scene.Items.Add(new AuthoredItem
        {
            Kind = AuthoredItemKind.Grid, Model = "models/x.glb", Rows = 3, Cols = 2, SpacingMul = 2.0,
        });

        var def = SceneCompiler.Compile(scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab"));

        Assert.Equal(6, def.Entities.Count); // 3×2, one mesh each

        // spacing = diagonal(FakeModel) × 2.0. diagonal = |(-1,-1,-1)-r  ..  (1,1,1)+r| with r=1.732 → 2*(1+1.732)*sqrt3.
        // Just assert the LAYOUT shape matches SpawnGrid: centred, symmetric.
        var xs = def.Entities.Select(e => e.Position.X).Distinct().OrderBy(x => x).ToArray();
        var zs = def.Entities.Select(e => e.Position.Z).Distinct().OrderBy(z => z).ToArray();
        Assert.Equal(2, xs.Length);            // cols
        Assert.Equal(3, zs.Length);            // rows
        Assert.Equal(0.0, xs[0] + xs[1], 6);   // symmetric about 0
        Assert.Equal(0.0, zs[0] + zs[2], 6);
        Assert.Equal(0.0, zs[1], 6);           // middle row centred
    }

    [Fact]
    public void Cluster_Emits_CountPhysicsBodies_Deterministically()
    {
        var scene = new AuthoredScene { Name = "d" };
        scene.Items.Add(new AuthoredItem
        {
            Kind = AuthoredItemKind.Cluster, Model = "models/x.glb", Count = 20, InverseMass = 1f, Restitution = 0.3f,
        });

        var a = SceneCompiler.Compile(scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab"));
        var b = SceneCompiler.Compile(scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab"));

        Assert.Equal(20, a.Entities.Count);
        Assert.All(a.Entities, e => Assert.NotNull(e.Body));
        Assert.Equal(0.3f, a.Entities[0].Body!.Restitution);
        // deterministic Hash(i) jitter
        Assert.Equal(a.Entities.Select(e => e.Position), b.Entities.Select(e => e.Position));
        // the cube-cluster stacks upward: some entities well above y=0
        Assert.Contains(a.Entities, e => e.Position.Y > 4.0);
    }

    [Fact]
    public void Prefab_Instance_InlinesTheModel()
    {
        var scene = new AuthoredScene { Name = "p" };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Prefab = "helmet", Position = new Double3(5, 0, 0) });

        var def = SceneCompiler.Compile(scene, Loader("models/DamagedHelmet.glb"), _ => Prefab("models/DamagedHelmet.glb"));

        var e = Assert.Single(def.Entities);
        Assert.Equal(new AssetKey("models/DamagedHelmet.glb"), e.Model);
        Assert.Equal(new Double3(5, 0, 0), e.Position);
    }

    [Fact]
    public void Prefab_MemberOffset_IsRotatedByTheInstanceTransform()
    {
        // Audit finding (engine-architect F3): a prefab member's local offset must be transformed by the
        // INSTANCE's rotation, not left in prefab-local space — otherwise a rotated multi-part prefab places its
        // parts in the wrong spot.
        var prefab = new AuthoredPrefab();
        prefab.Entities.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb", Position = new Double3(1, 0, 0) });

        var scene = new AuthoredScene { Name = "p" };
        // A quarter-turn about Y maps local +X to world -Z (row-vector, right-handed).
        scene.Items.Add(new AuthoredItem
        {
            Kind = AuthoredItemKind.Entity, Prefab = "rig",
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
        });

        var def = SceneCompiler.Compile(scene, Loader("models/x.glb"), _ => prefab);

        // Sign/axis convention aside, a pure rotation preserves the offset's magnitude but must NOT leave it in
        // raw prefab-local space (before the fix: Position == (1,0,0) regardless of the instance's rotation).
        var pos = Assert.Single(def.Entities).Position;
        var length = Math.Sqrt((pos.X * pos.X) + (pos.Y * pos.Y) + (pos.Z * pos.Z));
        Assert.Equal(1.0, length, 5);
        Assert.True(Math.Abs(pos.X - 1.0) > 1e-4 || Math.Abs(pos.Z) > 1e-4, "the offset must be rotated by the instance transform, not left in prefab-local space");
    }

    [Fact]
    public void Place_MaterialLessMesh_ResolvesToTheModelsMaterialsCount()
    {
        // Audit finding (engine-architect F5): must match the render registry's convention — an out-of-range
        // MaterialIndex resolves to the model's APPENDED default material (index == Materials.Count), never 0.
        var scene = new AuthoredScene { Name = "d" };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/nomat.glb" });

        var noMatModel = new ModelAsset
        {
            Meshes = [new MeshAsset { Positions = [Vector3.Zero], Indices = [0], MaterialIndex = -1, BoundsRadius = 1f }],
            Materials = [new MaterialAsset { Name = "a" }, new MaterialAsset { Name = "b" }],
            Images = [],
            Name = "nomat",
        };

        var def = SceneCompiler.Compile(scene, _ => noMatModel, _ => throw new Xunit.Sdk.XunitException("no prefab"));

        Assert.Equal(2, Assert.Single(def.Entities).LocalMat); // Materials.Count == 2, not 0
    }

    [Fact]
    public void Compile_UnknownModel_Throws()
    {
        var scene = new AuthoredScene { Name = "x" };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/missing.glb" });
        Assert.Throws<AssetException>(
            () => SceneCompiler.Compile(scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void Compile_ProducesAReadableAgsceneBlob()
    {
        var scene = new AuthoredScene { Name = "rt" };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });
        scene.Lights.Add(new AuthoredLight { Kind = "directional", Color = Vector3.One, Intensity = 10f, Direction = new Vector3(0, -1, 0) });

        var def = SceneCompiler.Compile(scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab"));

        using var ms = new MemoryStream();
        AgSceneWriter.Write(ms, def);
        var restored = Agapanthe.Assets.Scene.AgSceneFormat.Read(ms.ToArray());

        Assert.Equal("rt", restored.Name);
        Assert.Single(restored.Entities);
        Assert.Equal(new AssetKey("models/x.glb"), restored.Entities[0].Model);
        Assert.Single(restored.Lights);
    }

    // --- Contenu-3c: attractor physics, [[system]] → SceneSystem ------------------------------------------------

    [Fact]
    public void ToPhysics_AttractorFields_Translated()
    {
        var scene = new AuthoredScene
        {
            Name = "x",
            Physics = new AuthoredPhysics
            {
                Gravity = Vector3.Zero, GroundY = 0f,
                AttractorMu = 2.03e14, AttractorCenter = new Double3(1, 2, 3), AttractorSurfaceRadius = 3185500.0,
            },
        };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });

        var def = SceneCompiler.Compile(scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab"));

        Assert.NotNull(def.Physics);
        Assert.Equal(2.03e14, def.Physics!.Mu);
        Assert.Equal(new Double3(1, 2, 3), def.Physics.AttractorCenter);
        Assert.Equal(3185500.0, def.Physics.SurfaceRadius);
    }

    [Fact]
    public void ToSystem_ProbeDrop_ResolvesMeshAndMaterial()
    {
        var scene = new AuthoredScene { Name = "x" };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });
        scene.Systems.Add(new AuthoredSystem
        {
            Kind = "probe_drop", ProbeModel = "procedural/probe", ProbeRadius = 3f, Every = 30, Centre = new Double3(0, 100, 0),
        });

        var def = SceneCompiler.Compile(
            scene, Loader("models/x.glb", "procedural/probe"), _ => throw new Xunit.Sdk.XunitException("no prefab"));

        var sys = Assert.Single(def.Systems);
        Assert.Equal(SceneSystemKind.ProbeDrop, sys.Kind);
        Assert.Equal(new AssetKey("procedural/probe"), sys.ProbeModel);
        Assert.Equal(0, sys.ProbeLocalMesh);
        Assert.Equal(0, sys.ProbeLocalMat); // FakeModel's single mesh has MaterialIndex 0
        Assert.Equal(3f, sys.ProbeRadius);
        Assert.Equal(30, sys.Every);
        Assert.Equal(new Double3(0, 100, 0), sys.Centre);
    }

    [Fact]
    public void ToSystem_UnknownProbeModel_Throws()
    {
        var scene = new AuthoredScene { Name = "x" };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });
        scene.Systems.Add(new AuthoredSystem { Kind = "probe_drop", ProbeModel = "procedural/missing", ProbeRadius = 3f });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void ToSystem_LandingChallenge_ResolvesMeshAndMaterial()
    {
        var scene = new AuthoredScene
        {
            Name = "x",
            Physics = new AuthoredPhysics { AttractorMu = 1e14, AttractorCenter = default, AttractorSurfaceRadius = 100.0 },
        };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });
        scene.Systems.Add(new AuthoredSystem
        {
            Kind = "landing_challenge", ProbeModel = "procedural/probe", ProbeRadius = 3f,
            ZoneCenter = new Double3(10, 100, 0), ZoneRadius = 15.0, SurfaceBand = 9.0, DropHeight = 120.0,
            TargetCount = 3, ShotBudget = 6, QuicksavePath = "challenge.save",
        });

        var def = SceneCompiler.Compile(
            scene, Loader("models/x.glb", "procedural/probe"), _ => throw new Xunit.Sdk.XunitException("no prefab"));

        var sys = Assert.Single(def.Systems);
        Assert.Equal(SceneSystemKind.LandingChallenge, sys.Kind);
        Assert.Equal(new AssetKey("procedural/probe"), sys.ProbeModel);
        Assert.Equal(0, sys.ProbeLocalMesh);
        Assert.Equal(3f, sys.ProbeRadius);
        Assert.Equal(new Double3(10, 100, 0), sys.ZoneCenter);
        Assert.Equal(15.0, sys.ZoneRadius);
        Assert.Equal(9.0, sys.SurfaceBand);
        Assert.Equal(120.0, sys.DropHeight);
        Assert.Equal(3, sys.TargetCount);
        Assert.Equal(6, sys.ShotBudget);
        Assert.Equal("challenge.save", sys.QuicksavePath);
    }

    [Fact]
    public void ToSystem_LandingChallenge_NoPhysicsAttractor_Throws()
    {
        var scene = new AuthoredScene { Name = "x" };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });
        scene.Systems.Add(new AuthoredSystem
        {
            Kind = "landing_challenge", ProbeModel = "procedural/probe", ProbeRadius = 3f,
            ZoneCenter = new Double3(10, 100, 0), ZoneRadius = 15.0, SurfaceBand = 9.0, DropHeight = 120.0,
            TargetCount = 3, ShotBudget = 6,
        });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb", "procedural/probe"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void ToSystem_LandingChallenge_UniformGravityPhysics_Throws()
    {
        // Mu == 0 (the uniform-gravity path) is not an attractor — a LandingChallenge system needs a real one.
        var scene = new AuthoredScene { Name = "x", Physics = new AuthoredPhysics() };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });
        scene.Systems.Add(new AuthoredSystem
        {
            Kind = "landing_challenge", ProbeModel = "procedural/probe", ProbeRadius = 3f,
            ZoneCenter = new Double3(10, 100, 0), ZoneRadius = 15.0, SurfaceBand = 9.0, DropHeight = 120.0,
            TargetCount = 3, ShotBudget = 6,
        });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb", "procedural/probe"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void ToSystem_LandingChallenge_ZeroSurfaceRadiusPhysics_Throws()
    {
        // Audit finding (engine-architect): the cook-time guard originally checked only AttractorMu > 0 — a
        // scene with mu > 0 but surface_radius == 0 (or negative) satisfied it while leaving the challenge's
        // contact query measuring against a zero-radius sphere at the planet centre.
        var scene = new AuthoredScene
        {
            Name = "x",
            Physics = new AuthoredPhysics { AttractorMu = 1e14, AttractorSurfaceRadius = 0.0 },
        };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });
        scene.Systems.Add(new AuthoredSystem
        {
            Kind = "landing_challenge", ProbeModel = "procedural/probe", ProbeRadius = 3f,
            ZoneCenter = new Double3(10, 100, 0), ZoneRadius = 15.0, SurfaceBand = 9.0, DropHeight = 120.0,
            TargetCount = 3, ShotBudget = 6,
        });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb", "procedural/probe"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Theory]
    [InlineData(0, 6)]   // target_count < 1
    [InlineData(-1, 6)]  // target_count negative — used to reach AgSceneFormat's checked((uint)) as a raw OverflowException
    [InlineData(3, 0)]   // shot_budget < 1
    [InlineData(3, 2)]   // shot_budget < target_count — structurally unwinnable
    public void ToSystem_LandingChallenge_InvalidCounts_Throws(int targetCount, int shotBudget)
    {
        var scene = new AuthoredScene
        {
            Name = "x",
            Physics = new AuthoredPhysics { AttractorMu = 1e14, AttractorSurfaceRadius = 100.0 },
        };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });
        scene.Systems.Add(new AuthoredSystem
        {
            Kind = "landing_challenge", ProbeModel = "procedural/probe", ProbeRadius = 3f,
            ZoneCenter = new Double3(10, 100, 0), ZoneRadius = 15.0, SurfaceBand = 9.0, DropHeight = 120.0,
            TargetCount = targetCount, ShotBudget = shotBudget,
        });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb", "procedural/probe"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void ToSystem_LandingChallenge_NaNZoneRadius_Throws()
    {
        var scene = new AuthoredScene
        {
            Name = "x",
            Physics = new AuthoredPhysics { AttractorMu = 1e14, AttractorSurfaceRadius = 100.0 },
        };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });
        scene.Systems.Add(new AuthoredSystem
        {
            Kind = "landing_challenge", ProbeModel = "procedural/probe", ProbeRadius = 3f,
            ZoneCenter = new Double3(10, 100, 0), ZoneRadius = double.NaN, SurfaceBand = 9.0, DropHeight = 120.0,
            TargetCount = 3, ShotBudget = 6,
        });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb", "procedural/probe"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void ToSystem_LandingChallenge_QuicksavePathTraversal_Throws()
    {
        var scene = new AuthoredScene
        {
            Name = "x",
            Physics = new AuthoredPhysics { AttractorMu = 1e14, AttractorSurfaceRadius = 100.0 },
        };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });
        scene.Systems.Add(new AuthoredSystem
        {
            Kind = "landing_challenge", ProbeModel = "procedural/probe", ProbeRadius = 3f,
            ZoneCenter = new Double3(10, 100, 0), ZoneRadius = 15.0, SurfaceBand = 9.0, DropHeight = 120.0,
            TargetCount = 3, ShotBudget = 6, QuicksavePath = "../../etc/passwd",
        });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb", "procedural/probe"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void Entity_WithBody_ProducesASceneBody()
    {
        // Contenu-3c-3: a bare [[entity]] can opt into a single physics body (`body = true`) — the `drive`
        // scene's one steerable body, reusing [[cluster]]'s existing InverseMass/Restitution/Radius fields.
        var scene = new AuthoredScene { Name = "x" };
        scene.Items.Add(new AuthoredItem
        {
            Kind = AuthoredItemKind.Entity, Model = "models/x.glb", HasBody = true,
            Velocity = new Vector3(1, 0, 0), InverseMass = 2f, Restitution = 0.1f, Radius = 0.5f,
        });

        var def = SceneCompiler.Compile(scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab"));

        var e = Assert.Single(def.Entities);
        Assert.NotNull(e.Body);
        Assert.Equal(new Vector3(1, 0, 0), e.Body!.Velocity);
        Assert.Equal(2f, e.Body.InverseMass);
        Assert.Equal(0.1f, e.Body.Restitution);
        Assert.Equal(0.5f, e.Body.Radius);
    }

    [Fact]
    public void Entity_WithoutBody_IsPlainDrawable()
    {
        var scene = new AuthoredScene { Name = "x" };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });

        var def = SceneCompiler.Compile(scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab"));

        Assert.Null(Assert.Single(def.Entities).Body);
    }

    [Fact]
    public void ToSystem_DriveControl_ResolvesControlledEntity_NoProbeModelLoadNeeded()
    {
        var scene = new AuthoredScene { Name = "x" };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb", HasBody = true, Radius = 1f });
        scene.Systems.Add(new AuthoredSystem { Kind = "drive_control", ControlledEntityIndex = 0, MoveSpeed = 6f });

        // Loader only knows the entity's own model — proves drive_control's system-resolution path never
        // calls loadModel for a probe (it has none), unlike probe_drop/landing_challenge.
        var def = SceneCompiler.Compile(scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab"));

        var sys = Assert.Single(def.Systems);
        Assert.Equal(SceneSystemKind.DriveControl, sys.Kind);
        Assert.True(sys.ProbeModel.IsNone);
        Assert.Equal(0, sys.ControlledEntityIndex);
        Assert.Equal(6f, sys.MoveSpeed);
    }

    [Fact]
    public void ToSystem_DriveControl_OutOfRangeIndex_Throws()
    {
        var scene = new AuthoredScene { Name = "x" };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb", HasBody = true, Radius = 1f });
        scene.Systems.Add(new AuthoredSystem { Kind = "drive_control", ControlledEntityIndex = 1, MoveSpeed = 6f });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void ToSystem_DriveControl_TargetsNonBodyEntity_Throws()
    {
        var scene = new AuthoredScene { Name = "x" };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" }); // no body
        scene.Systems.Add(new AuthoredSystem { Kind = "drive_control", ControlledEntityIndex = 0, MoveSpeed = 6f });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void ToSystem_DriveControl_InvalidMoveSpeed_Throws()
    {
        var scene = new AuthoredScene { Name = "x" };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb", HasBody = true, Radius = 1f });
        scene.Systems.Add(new AuthoredSystem { Kind = "drive_control", ControlledEntityIndex = 0, MoveSpeed = 0f });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void ToSystem_DriveControl_WithRestore_Throws()
    {
        // Audit finding (both csharp-lowlevel and engine-architect, 🟠): DriveControl resolves its target via
        // MaterializeResult.SpawnedEntities, which a pending restore leaves entirely null (spawn suppressed) —
        // cook-time rejection of the [restore]+drive_control combination, closed alongside SceneRecipe's
        // runtime AGAPANTHE_LOAD rejection (which this test can't reach — that's an integration-level check).
        var scene = new AuthoredScene { Name = "x", Restore = new AuthoredRestore { Snapshot = "x.save" } };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb", HasBody = true, Radius = 1f });
        scene.Systems.Add(new AuthoredSystem { Kind = "drive_control", ControlledEntityIndex = 0, MoveSpeed = 6f });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    private static ModelAsset MultiMeshFakeModel(string name) => new()
    {
        Meshes =
        [
            new MeshAsset { Positions = [new(-1, -1, -1), new(1, 1, 1)], Indices = [0, 1, 0], MaterialIndex = 0, BoundsCenter = Vector3.Zero, BoundsRadius = 1.7f },
            new MeshAsset { Positions = [new(-1, -1, -1), new(1, 1, 1)], Indices = [0, 1, 0], MaterialIndex = 0, BoundsCenter = Vector3.Zero, BoundsRadius = 1.7f },
        ],
        Materials = [new MaterialAsset { Name = "m" }],
        Images = [],
        Name = name,
    };

    [Fact]
    public void Entity_WithBody_OnMultiMeshModel_Throws()
    {
        // Audit finding (both csharp-lowlevel and engine-architect, 🟠): Place stamps the SAME SceneBody onto
        // every (member × mesh) SceneEntity it emits — a multi-mesh model with body=true would silently spawn N
        // co-located, mutually-penetrating rigid bodies. Reject the ambiguity at cook time.
        var scene = new AuthoredScene { Name = "x" };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/multi.glb", HasBody = true, Radius = 1f });

        var loader = (Func<AssetKey, ModelAsset>)(k => k.Value == "models/multi.glb"
            ? MultiMeshFakeModel("multi")
            : throw new AssetException($"no cooked model '{k}'"));

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, loader, _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void Entity_WithBody_OnMultiMemberPrefab_Throws()
    {
        var scene = new AuthoredScene { Name = "x" };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Prefab = "pair", HasBody = true, Radius = 1f });

        var prefab = new AuthoredPrefab();
        prefab.Entities.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });
        prefab.Entities.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb"), name => name == "pair" ? prefab : throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void Compile_NonZeroWorldOriginWithAttractor_Throws()
    {
        // Audit finding (engine-architect, 🟡, endorsed as worth closing before Contenu-3c's domain close):
        // SceneMaterializer.Compose adds WorldOrigin to entity positions but ScenePhysics.AttractorCenter is
        // consumed raw — a non-zero world_origin + an attractor would silently misalign entity vs. physics
        // geometry.
        var scene = new AuthoredScene
        {
            Name = "x", WorldOrigin = new Double3(1000, 0, 0),
            Physics = new AuthoredPhysics { AttractorMu = 1e14, AttractorSurfaceRadius = 100.0 },
        };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void Compile_NonZeroWorldOriginWithProbeDropSystem_Throws()
    {
        var scene = new AuthoredScene { Name = "x", WorldOrigin = new Double3(1000, 0, 0) };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });
        scene.Systems.Add(new AuthoredSystem { Kind = "probe_drop", ProbeModel = "procedural/probe", ProbeRadius = 3f });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb", "procedural/probe"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void Compile_NonZeroWorldOrigin_NoAttractorOrProbeSystem_Succeeds()
    {
        // world_origin alone (no attractor, no probe/zone system) is fine — every shipped scene family other
        // than planet*/drive uses this shape.
        var scene = new AuthoredScene { Name = "x", WorldOrigin = new Double3(1000, 0, 0) };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });

        var def = SceneCompiler.Compile(scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab"));

        Assert.Equal(new Double3(1000, 0, 0), def.WorldOrigin);
    }

    [Fact]
    public void ToCamera_Fixed_Orthographic_CompilesProjectionAndDimensions()
    {
        var scene = new AuthoredScene
        {
            Name = "x",
            Camera = new AuthoredCamera
            {
                Mode = "fixed", Position = new Double3(0, 50, 0), Pitch = -1.5708f, FovY = 60f,
                Projection = "orthographic", OrthoWidth = 40f, OrthoHeight = 30f,
            },
        };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });

        var def = SceneCompiler.Compile(scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab"));

        Assert.Equal(SceneCameraMode.Fixed, def.Camera.Mode);
        Assert.Equal(CameraProjection.Orthographic, def.Camera.Projection);
        Assert.Equal(40f, def.Camera.OrthoWidth);
        Assert.Equal(30f, def.Camera.OrthoHeight);
    }

    [Fact]
    public void ToCamera_Fixed_DefaultProjection_IsPerspective()
    {
        var scene = new AuthoredScene { Name = "x", Camera = new AuthoredCamera { Mode = "fixed", Position = new Double3(0, 0, 10) } };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });

        var def = SceneCompiler.Compile(scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab"));

        Assert.Equal(CameraProjection.Perspective, def.Camera.Projection);
    }

    [Fact]
    public void ToCamera_UnknownProjection_Throws()
    {
        var scene = new AuthoredScene { Name = "x", Camera = new AuthoredCamera { Mode = "fixed", Projection = "isometric" } };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Theory]
    [InlineData(0f, 30f)]
    [InlineData(-1f, 30f)]
    [InlineData(40f, -1f)]
    public void ToCamera_Orthographic_InvalidDimensions_Throws(float width, float height)
    {
        var scene = new AuthoredScene
        {
            Name = "x",
            Camera = new AuthoredCamera { Mode = "fixed", Projection = "orthographic", OrthoWidth = width, OrthoHeight = height },
        };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }

    [Fact]
    public void ToCamera_Orthographic_ZeroHeight_IsAValidDeriveFromAspectSentinel()
    {
        // Audit finding (Slice-2, both csharp-lowlevel and engine-architect): ortho_height=0 means "derive from
        // ortho_width / the runtime aspect ratio" (Camera.ResolveOrthoHeight) — it must compile, not throw.
        var scene = new AuthoredScene
        {
            Name = "x",
            Camera = new AuthoredCamera { Mode = "fixed", Projection = "orthographic", OrthoWidth = 40f, OrthoHeight = 0f },
        };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });

        var def = SceneCompiler.Compile(scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab"));

        Assert.Equal(0f, def.Camera.OrthoHeight);
    }

    [Fact]
    public void ToCamera_FrameBounds_RejectsNonDefaultProjectionFields()
    {
        // Audit finding (Slice-2, both csharp-lowlevel and engine-architect): projection/ortho_width/ortho_height
        // are Fixed-only fields — frame-bounds used to silently drop them instead of rejecting.
        var scene = new AuthoredScene
        {
            Name = "x",
            Camera = new AuthoredCamera { Mode = "frame-bounds", Projection = "orthographic" },
        };
        scene.Items.Add(new AuthoredItem { Kind = AuthoredItemKind.Entity, Model = "models/x.glb" });

        Assert.Throws<AssetException>(() => SceneCompiler.Compile(
            scene, Loader("models/x.glb"), _ => throw new Xunit.Sdk.XunitException("no prefab")));
    }
}
