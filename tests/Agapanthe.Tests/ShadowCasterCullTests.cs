using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Rendering;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// The per-cascade shadow-caster cull (P3-M5). It replaced the P3-M2 two-pass bounded wedge: because each cascade is
/// fitted to its own frustum SLICE (camera-only), the fit no longer depends on the casters, so there is no
/// circularity left to break — casters are simply tested against each finished cascade volume. These assertions
/// cover what the wedge used to guarantee: a far-away entity cannot wreck the shadow map's depth precision, an
/// off-screen caster still casts, and each cascade's list stays mesh-major for the instanced depth draw.
/// </summary>
[Collection("World")]
public sealed class ShadowCasterCullTests
{
    private const uint TileResolution = 2048;
    private const float ShadowDistance = 100f;

    // Sun straight down, so "upstream" (toward the source) is +Y — an easy axis to place a pathological caster on.
    private static readonly Vector3 Sun = new(0f, -1f, 0f);

    private static readonly CascadeSettings Cascades = new(4, 0.5f, ShadowDistance);

    private static RenderView ViewAtOrigin()
    {
        var v = MathHelpers.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        var proj = MathHelpers.PerspectiveVulkan(MathF.PI / 3f, 1f, 0.1f, ShadowDistance);
        return new RenderView(Double3.Zero, Vector3.Zero, in v, in proj, MathF.PI / 3f, 1f, 0.1f, ShadowDistance);
    }

    private static ImportedEntitySpec Caster(Double3 position, uint order, int mesh = 0)
        => new(new MeshHandle(mesh, 1), new MaterialHandle(0, 1), position, Matrix4x4.Identity, Vector3.Zero, 1f, order);

    // Fits the cascades and collects the casters exactly as the engine's frame seam does.
    private static (Matrix4x4[] Cascades, float[] Splits, RenderList[] Casters) Collect(GameWorld world, in RenderView view)
    {
        var mats = new Matrix4x4[Cascades.Count];
        var splits = new float[Cascades.Count];
        ShadowFit.ComputeCascades(in view, Sun, in Cascades, TileResolution, mats, splits);

        var frusta = new Frustum[Cascades.Count];
        for (var c = 0; c < mats.Length; c++)
        {
            frusta[c] = Frustum.FromViewProjection(mats[c]);
        }

        var lists = new RenderList[Cascades.Count];
        for (var c = 0; c < lists.Length; c++)
        {
            lists[c] = new RenderList();
        }

        world.CollectShadowCasters(frusta, lists, in view);
        return (mats, splits, lists);
    }

    // The extent of the fitted ortho box, recovered from the matrix — the quantity that sets shadow texel density.
    private static float FittedWidth(Matrix4x4 m)
        => 2f / new Vector3(m.M11, m.M21, m.M31).Length();

    [Fact]
    public void FarUpstreamCaster_CannotAffectAnyCascadeFit()
    {
        // THE test the bounded wedge used to own (P3-M2 D3.a), now free by construction. An entity 10 000 km
        // upstream of the sun used to drive UpstreamExtent — and thus the depth range — to ~1e7 m. Since P3-M5 the
        // fit is camera-only, so that entity CANNOT touch any cascade's extent, and it is culled from every caster
        // list because it lies far outside every cascade volume. Its lost shadow is the WANTED behaviour.
        using var world = new GameWorld();
        world.SpawnImported(Caster(new Double3(0, 0, -10), 0));        // A: a normal caster, in view
        world.SpawnImported(Caster(new Double3(0, 10_000_000, 0), 1)); // B: 10 000 km straight up = far upstream

        var view = ViewAtOrigin();
        var (mats, _, casters) = Collect(world, in view);

        // Same scene WITHOUT the pathological entity: the fits must be identical, proving B had no influence.
        using var clean = new GameWorld();
        clean.SpawnImported(Caster(new Double3(0, 0, -10), 0));
        var (cleanMats, _, _) = Collect(clean, in view);

        for (var c = 0; c < mats.Length; c++)
        {
            Assert.Equal(FittedWidth(cleanMats[c]), FittedWidth(mats[c]), 3);
        }

        // And B is in no cascade's caster list (A may be in several — the near ones).
        var total = 0;
        foreach (var list in casters)
        {
            total += list.Count;
            foreach (var item in list.Items.ToArray())
            {
                Assert.True(item.WorldTransform.M42 < 1e6f, "the far-upstream caster must not be in any cascade list");
            }
        }

        Assert.True(total >= 1, "the in-view caster must be a caster in at least one cascade");
    }

    [Fact]
    public void NearCaster_LandsInTheNearCascade()
    {
        // A caster a few metres ahead belongs to cascade 0 (the tightest slice) — that is what buys the sharp
        // contact shadow the CSM exists for.
        using var world = new GameWorld();
        world.SpawnImported(Caster(new Double3(0, 0, -3), 0));

        var view = ViewAtOrigin();
        var (_, _, casters) = Collect(world, in view);

        Assert.Equal(1, casters[0].Count);
    }

    [Fact]
    public void CasterStraddlingTwoCascades_IsDrawnInBoth()
    {
        // Cascades overlap in space (each covers a sphere around its slice), so an entity near a split boundary
        // belongs to both lists — it must be drawn into both tiles or its shadow would vanish on one side of the
        // blend band.
        using var world = new GameWorld();
        var view = ViewAtOrigin();
        var mats = new Matrix4x4[Cascades.Count];
        var splits = new float[Cascades.Count];
        ShadowFit.ComputeCascades(in view, Sun, in Cascades, TileResolution, mats, splits);

        // Right at the first split: inside cascade 0's sphere and cascade 1's.
        world.SpawnImported(Caster(new Double3(0, 0, -splits[0]), 0));
        var (_, _, casters) = Collect(world, in view);

        Assert.Equal(1, casters[0].Count);
        Assert.Equal(1, casters[1].Count);
    }

    [Fact]
    public void EachCascadeListIsMeshMajor()
    {
        // The depth pass binds no material, so each cascade's list must be MESH-major: one contiguous run per mesh
        // = one instanced draw. Meshes are interleaved at spawn so a mesh-major sort has real work to do.
        using var world = new GameWorld();
        for (var i = 0u; i < 8; i++)
        {
            world.SpawnImported(Caster(new Double3((i * 0.4) - 1.4, 0, -6), order: i, mesh: (int)(i % 2)));
        }

        var view = ViewAtOrigin();
        var (_, _, casters) = Collect(world, in view);

        var items = casters[0].Items;
        Assert.Equal(8, items.Length);
        for (var i = 1; i < items.Length; i++)
        {
            Assert.True(
                items[i].Mesh.Index >= items[i - 1].Mesh.Index,
                "each cascade's caster list must be mesh-major (contiguous instanced runs)");
        }
    }

    [Fact]
    public void NoShadowCastEntities_AreExcludedFromEveryCascade()
    {
        // The ground plane (P3-M5): it receives but never casts, so it appears in NO cascade list — which is what
        // stops a large flat receiver from self-shadowing into acne.
        using var world = new GameWorld();
        world.SpawnImported(Caster(new Double3(0, 0, -5), 0));
        world.SpawnImported(Caster(new Double3(0, -1, -5), 1), castsShadow: false);

        var view = ViewAtOrigin();
        var (_, _, casters) = Collect(world, in view);

        foreach (var list in casters)
        {
            foreach (var item in list.Items.ToArray())
            {
                // Only the y=0 caster may appear; the y=-1 "ground" never does.
                Assert.True(item.WorldTransform.M42 > -0.5f, "a NoShadowCast entity must not be in any cascade list");
            }
        }
    }
}
