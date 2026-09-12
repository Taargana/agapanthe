using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Pipeline.Procedural;
using Tomlyn;
using Tomlyn.Model;

namespace Agapanthe.Tests;

/// <summary>Contenu-3c-3 — <see cref="GroundQuadGenerator"/> is <c>Sandbox.ModelContent.BuildGroundModel</c>/
/// <c>BuildGrassImage</c> moved verbatim to cook-time. That Sandbox code is deleted in this same phase (its only
/// caller, <c>DriveSceneRecipe</c>, is deleted alongside it), so this test can't diff against it directly —
/// instead it pins the exact geometry/image invariants the old formula produced, the same "verified, not
/// assumed" posture <see cref="UvSphereGeneratorTests"/> established for the sphere generator.</summary>
public sealed class GroundQuadGeneratorTests
{
    private static TomlTable Parse(string toml) => Toml.Parse(toml).ToModel();

    [Fact]
    public void Build_ProducesACcwQuad_InTheXzPlane_CenteredOnOrigin()
    {
        const float size = 40f;
        var table = Parse($"""
            generator = "ground_quad"
            size = {size}
            """);
        var generated = GroundQuadGenerator.Build(table, "<test>");

        var mesh = Assert.Single(generated.Meshes);
        var h = size * 0.5f;
        Assert.Equal(
            new[] { new Vector3(-h, 0f, -h), new Vector3(h, 0f, -h), new Vector3(h, 0f, h), new Vector3(-h, 0f, h) },
            mesh.Positions);
        Assert.All(mesh.Normals, n => Assert.Equal(Vector3.UnitY, n));
        Assert.Equal(new uint[] { 0, 2, 1, 0, 3, 2 }, mesh.Indices);
        Assert.Equal("Ground", mesh.Name);
        Assert.Equal(0, mesh.MaterialIndex);
    }

    [Fact]
    public void Build_TilesUvsBySizeOverFour_ClampedToAtLeastOne()
    {
        var table = Parse("""
            generator = "ground_quad"
            size = 2.0
            """);
        var generated = GroundQuadGenerator.Build(table, "<test>");

        // MathF.Max(size / 4f, 1f) — a small quad still gets 1 full UV tile, never sub-1 (no minification blur).
        var tiles = MathF.Max(2f / 4f, 1f);
        Assert.Equal(new Vector2(0f, 0f), generated.Meshes[0].Uvs[0]);
        Assert.Equal(new Vector2(tiles, 0f), generated.Meshes[0].Uvs[1]);
        Assert.Equal(new Vector2(tiles, tiles), generated.Meshes[0].Uvs[2]);
        Assert.Equal(new Vector2(0f, tiles), generated.Meshes[0].Uvs[3]);
    }

    [Fact]
    public void Build_ComputesBounds_MatchingTheQuadExtent()
    {
        const float size = 40f;
        var table = Parse($"""
            generator = "ground_quad"
            size = {size}
            """);
        var mesh = GroundQuadGenerator.Build(table, "<test>").Meshes[0];

        Assert.Equal(Vector3.Zero, mesh.BoundsCenter);
        // The bounding sphere of a size×size quad's 4 corners: radius = half-diagonal = size/2 * sqrt(2).
        Assert.Equal(size * 0.5f * MathF.Sqrt(2f), mesh.BoundsRadius, precision: 3);
    }

    [Fact]
    public void Build_MaterialAndImage_MatchTheOldConstants()
    {
        var table = Parse("""
            generator = "ground_quad"
            size = 40.0
            """);
        var generated = GroundQuadGenerator.Build(table, "<test>");

        var material = Assert.Single(generated.Materials);
        Assert.Equal(Vector4.One, material.BaseColorFactor);
        Assert.Equal(0, material.BaseColorImage);
        Assert.Equal(0f, material.MetallicFactor);
        Assert.Equal(1f, material.RoughnessFactor);
        Assert.Equal("GrassMaterial", material.Name);

        var image = Assert.Single(generated.Images);
        Assert.Equal(512, image.Width);
        Assert.Equal(512, image.Height);
        Assert.True(image.IsSrgb);
        Assert.Equal(512 * 512 * 4, image.Rgba8Pixels.Length);
        for (var i = 3; i < image.Rgba8Pixels.Length; i += 4)
        {
            Assert.Equal(255, image.Rgba8Pixels[i]); // alpha always opaque
        }
    }

    [Fact]
    public void Build_IsDeterministic_FixedSeedProducesIdenticalImageAcrossCalls()
    {
        var table = Parse("""
            generator = "ground_quad"
            size = 40.0
            """);
        var a = GroundQuadGenerator.Build(table, "<test>");
        var b = GroundQuadGenerator.Build(table, "<test>");

        Assert.Equal(a.Images[0].Rgba8Pixels, b.Images[0].Rgba8Pixels);
    }

    [Theory]
    [InlineData("0.0")]
    [InlineData("-5.0")]
    [InlineData("nan")]
    public void Build_InvalidSize_Throws(string size)
    {
        var table = Parse($"""
            generator = "ground_quad"
            size = {size}
            """);
        Assert.Throws<AssetException>(() => GroundQuadGenerator.Build(table, "<test>"));
    }

    [Fact]
    public void Build_UnknownKey_Throws()
    {
        var table = Parse("""
            generator = "ground_quad"
            size = 40.0
            segments = 8
            """);
        Assert.Throws<AssetException>(() => GroundQuadGenerator.Build(table, "<test>"));
    }

    [Fact]
    public void Build_MissingSize_Throws()
    {
        var table = Parse("""
            generator = "ground_quad"
            """);
        Assert.Throws<AssetException>(() => GroundQuadGenerator.Build(table, "<test>"));
    }
}
