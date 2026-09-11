using Agapanthe.Assets;
using Agapanthe.Assets.Pipeline.Scene;

namespace Agapanthe.Tests;

/// <summary>Contenu-3b — the TOML scene/prefab reader: valid files parse, malformed ones fail with an
/// <see cref="AssetException"/> naming the file.</summary>
public sealed class SceneTomlReaderTests
{
    private static string TempToml(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aw-scene-{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void ReadScene_ParsesEntitiesLightsCameraEnvironment()
    {
        var path = TempToml("""
            name = "t"
            world_origin = [1.0, 2.0, 3.0]

            [[entity]]
            model = "models/a.glb"
            position = [0.0, 5.0, 0.0]
            casts_shadow = false

            [[light]]
            kind = "directional"
            direction = [0.0, -1.0, 0.0]
            intensity = 8.0

            [camera]
            mode = "frame-bounds"
            fov_y = 55.0
            free_fly = true

            [environment]
            hdri = "env/studio.hdr"
            """);
        try
        {
            var s = SceneTomlReader.ReadScene(path);
            Assert.Equal("t", s.Name);
            Assert.Single(s.Items);
            Assert.Equal("models/a.glb", s.Items[0].Model);
            Assert.False(s.Items[0].CastsShadow);
            Assert.Single(s.Lights);
            Assert.Equal("frame-bounds", s.Camera.Mode);
            Assert.True(s.Camera.FreeFly);
            Assert.Equal("env/studio.hdr", s.Environment.Hdri);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadScene_NoEntities_Throws()
    {
        var path = TempToml("name = \"empty\"\n");
        try
        {
            Assert.Throws<AssetException>(() => SceneTomlReader.ReadScene(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadScene_EntityWithBothModelAndPrefab_Throws()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "models/a.glb"
            prefab = "helmet"
            """);
        try
        {
            Assert.Throws<AssetException>(() => SceneTomlReader.ReadScene(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadScene_SyntaxError_Throws()
    {
        var path = TempToml("name = = broken\n[[entity]]\nmodel=\"a\"\n");
        try
        {
            var ex = Assert.Throws<AssetException>(() => SceneTomlReader.ReadScene(path));
            Assert.Contains("TOML syntax", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadScene_UnknownTopLevelKey_Throws()
    {
        // Contenu-3b audit (engine-architect F1): the class doc + design spec promise a rejection; a typo like
        // this must not silently take the AuthoredScene default.
        var path = TempToml("""
            name = "x"
            fov = 60

            [[entity]]
            model = "a"
            """);
        try
        {
            var ex = Assert.Throws<AssetException>(() => SceneTomlReader.ReadScene(path));
            Assert.Contains("fov", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadScene_UnknownItemKey_Throws()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "a"
            spacing = 1.5
            """);
        try
        {
            Assert.Throws<AssetException>(() => SceneTomlReader.ReadScene(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadScene_WorldOrigin_KeepsDoublePrecision()
    {
        // Contenu-3b audit (engine-architect F2): a large-magnitude authored position must not round-trip
        // through float before landing in the f64 Double3 field.
        var path = TempToml("""
            name = "x"
            world_origin = [74800000000.0, 0.0, 0.0]
            [[entity]]
            model = "a"
            """);
        try
        {
            var s = SceneTomlReader.ReadScene(path);
            Assert.Equal(74800000000.0, s.WorldOrigin.X);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadScene_BadArrayLength_Throws()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "a"
            position = [1.0, 2.0]
            """);
        try
        {
            Assert.Throws<AssetException>(() => SceneTomlReader.ReadScene(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
