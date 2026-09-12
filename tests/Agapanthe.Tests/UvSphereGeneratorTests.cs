using Agapanthe.Assets;
using Agapanthe.Assets.Pipeline.Procedural;
using Agapanthe.Rendering;
using Tomlyn;
using Tomlyn.Model;

namespace Agapanthe.Tests;

/// <summary>Contenu-3c — <see cref="UvSphereGenerator"/>'s tessellation must match <c>Primitives.UvSphere</c>
/// exactly: it is a cook-side reimplementation of the SAME algorithm (writing SoA arrays directly instead of an
/// intermediate <c>Vertex[]</c>, since the cook side must not reference <c>Agapanthe.Rendering</c>). Contenu-3b's
/// own audit flagged an unverified "copied verbatim" claim as a finding — this test closes that class of gap
/// proactively rather than trusting the doc comment.</summary>
public sealed class UvSphereGeneratorTests
{
    private static TomlTable Parse(string toml) => Toml.Parse(toml).ToModel();

    [Fact]
    public void Build_MatchesPrimitivesUvSphere_Geometry()
    {
        const float radius = 6371000f / 2f;
        const int segments = 16;
        const int rings = 8;

        var table = Parse($"""
            generator = "uv_sphere"
            radius = {radius}
            segments = {segments}
            rings = {rings}
            """);
        var generated = UvSphereGenerator.Build(table, "<test>");

        var (expectedVerts, expectedIdx) = Primitives.UvSphere(segments, rings);

        var mesh = Assert.Single(generated.Meshes);
        Assert.Equal(expectedVerts.Length, mesh.Positions.Length);
        Assert.Equal(expectedIdx.Length, mesh.Indices.Length);

        for (var i = 0; i < expectedVerts.Length; i++)
        {
            Assert.Equal(expectedVerts[i].Position * radius, mesh.Positions[i]);
            Assert.Equal(expectedVerts[i].Normal, mesh.Normals[i]);
            Assert.Equal(expectedVerts[i].Tangent, mesh.Tangents[i]);
            Assert.Equal(expectedVerts[i].Uv, mesh.Uvs[i]);
        }

        for (var i = 0; i < expectedIdx.Length; i++)
        {
            Assert.Equal((uint)expectedIdx[i], mesh.Indices[i]);
        }
    }

    [Fact]
    public void Build_DefaultTessellation_MatchesPlanetContentDefaults()
    {
        // PlanetContent.BuildSphereModel's default segments/rings (128/64) — confirm the generator's own
        // defaults match, so an author who omits them gets the same geometry the old code always produced.
        var table = Parse("""
            generator = "uv_sphere"
            radius = 1.0
            """);
        var generated = UvSphereGenerator.Build(table, "<test>");
        var (expectedVerts, expectedIdx) = Primitives.UvSphere(); // defaults: 128, 64

        Assert.Equal(expectedVerts.Length, generated.Meshes[0].Positions.Length);
        Assert.Equal(expectedIdx.Length, generated.Meshes[0].Indices.Length);
    }

    [Fact]
    public void Build_MaterialFactors_Applied()
    {
        var table = Parse("""
            generator = "uv_sphere"
            radius = 1.0
            base_color = [0.02, 0.02, 0.02, 1.0]
            metallic = 0.0
            roughness = 1.0
            emissive = [1.0, 0.95, 0.85]
            emissive_strength = 40.0
            name = "SunSurface"
            """);
        var generated = UvSphereGenerator.Build(table, "<test>");

        var material = Assert.Single(generated.Materials);
        Assert.Equal(new System.Numerics.Vector4(0.02f, 0.02f, 0.02f, 1f), material.BaseColorFactor);
        Assert.Equal(0f, material.MetallicFactor);
        Assert.Equal(new System.Numerics.Vector3(1f, 0.95f, 0.85f), material.EmissiveFactor);
        Assert.Equal(40f, material.EmissiveStrength);
        Assert.Equal("SunSurface", material.Name);
    }

    [Fact]
    public void Build_BoundsPrecomputed()
    {
        var table = Parse("""
            generator = "uv_sphere"
            radius = 5.0
            segments = 8
            rings = 4
            """);
        var generated = UvSphereGenerator.Build(table, "<test>");
        var mesh = generated.Meshes[0];

        Assert.True(mesh.BoundsRadius > 0f);
        Assert.Equal(5.0f, mesh.BoundsRadius, 3);
    }

    [Fact]
    public void Build_MissingRadius_Throws()
    {
        var table = Parse("""generator = "uv_sphere" """);
        Assert.Throws<AssetException>(() => UvSphereGenerator.Build(table, "<test>"));
    }

    [Fact]
    public void Build_UnknownKey_Throws()
    {
        var table = Parse("""
            generator = "uv_sphere"
            radius = 1.0
            not_a_real_key = 1
            """);
        Assert.Throws<AssetException>(() => UvSphereGenerator.Build(table, "<test>"));
    }

    [Fact]
    public void ProceduralGenerators_UnknownName_Throws()
        => Assert.Throws<AssetException>(() => ProceduralGenerators.Build("not_a_generator", Parse("x = 1"), "<test>"));

    [Fact]
    public void ProceduralTomlReader_MissingGeneratorKey_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aw-proc-{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, "radius = 1.0\n");
        try
        {
            Assert.Throws<AssetException>(() => ProceduralTomlReader.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProceduralTomlReader_ParsesAndDispatches()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aw-proc-{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, "generator = \"uv_sphere\"\nradius = 3.0\nsegments = 8\nrings = 4\n");
        try
        {
            var model = ProceduralTomlReader.Read(path);
            Assert.Single(model.Meshes);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
