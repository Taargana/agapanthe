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
}
