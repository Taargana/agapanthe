using Agapanthe.Assets;
using Agapanthe.Assets.Pipeline.Scene;
using Agapanthe.Core;

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

    // --- Contenu-3c: attractor physics, [[system]], baked Fixed camera, new environment modes ------------------

    [Fact]
    public void ReadScene_ParsesProbeDropSystem()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "a"

            [[system]]
            kind = "probe_drop"
            probe_model = "procedural/probe"
            probe_radius = 3.0
            centre = [0.0, 100.0, 0.0]
            every = 30
            """);
        try
        {
            var s = SceneTomlReader.ReadScene(path);
            var sys = Assert.Single(s.Systems);
            Assert.Equal("probe_drop", sys.Kind);
            Assert.Equal("procedural/probe", sys.ProbeModel);
            Assert.Equal(3.0f, sys.ProbeRadius);
            Assert.Equal(30, sys.Every);
            Assert.Equal(new Double3(0, 100, 0), sys.Centre);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadScene_SystemMissingKind_Throws()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "a"
            [[system]]
            probe_model = "procedural/probe"
            probe_radius = 3.0
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
    public void ReadScene_SystemUnknownKind_Throws()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "a"
            [[system]]
            kind = "not_a_real_system"
            probe_model = "procedural/probe"
            probe_radius = 3.0
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
    public void ReadScene_ProbeDropSystemMissingProbeModel_Throws()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "a"
            [[system]]
            kind = "probe_drop"
            probe_radius = 3.0
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
    public void ReadScene_ProbeDropSystemUnknownKey_Throws()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "a"
            [[system]]
            kind = "probe_drop"
            probe_model = "procedural/probe"
            probe_radius = 3.0
            zone_radius = 15.0
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
    public void ReadScene_ParsesLandingChallengeSystem()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "a"

            [[system]]
            kind = "landing_challenge"
            probe_model = "procedural/probe"
            probe_radius = 3.0
            zone_center = [10.0, 6371000.0, 0.0]
            zone_radius = 15.0
            surface_band = 9.0
            drop_height = 120.0
            target_count = 3
            shot_budget = 6
            quicksave_path = "challenge.save"
            """);
        try
        {
            var s = SceneTomlReader.ReadScene(path);
            var sys = Assert.Single(s.Systems);
            Assert.Equal("landing_challenge", sys.Kind);
            Assert.Equal("procedural/probe", sys.ProbeModel);
            Assert.Equal(3.0f, sys.ProbeRadius);
            Assert.Equal(new Double3(10, 6_371_000, 0), sys.ZoneCenter);
            Assert.Equal(15.0, sys.ZoneRadius);
            Assert.Equal(9.0, sys.SurfaceBand);
            Assert.Equal(120.0, sys.DropHeight);
            Assert.Equal(3, sys.TargetCount);
            Assert.Equal(6, sys.ShotBudget);
            Assert.Equal("challenge.save", sys.QuicksavePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadScene_LandingChallengeSystemMissingZoneCenter_Throws()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "a"
            [[system]]
            kind = "landing_challenge"
            probe_model = "procedural/probe"
            probe_radius = 3.0
            zone_radius = 15.0
            surface_band = 9.0
            drop_height = 120.0
            target_count = 3
            shot_budget = 6
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
    public void ReadScene_LandingChallengeSystemUnknownKey_Throws()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "a"
            [[system]]
            kind = "landing_challenge"
            probe_model = "procedural/probe"
            probe_radius = 3.0
            zone_center = [10.0, 6371000.0, 0.0]
            zone_radius = 15.0
            surface_band = 9.0
            drop_height = 120.0
            target_count = 3
            shot_budget = 6
            every = 30
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
    public void ReadScene_ParsesAttractorPhysics()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "a"
            [physics]
            gravity = [0.0, 0.0, 0.0]
            ground_y = 0.0
            mu = 2.03e14
            attractor_center = [0.0, 0.0, 0.0]
            surface_radius = 3185500.0
            """);
        try
        {
            var s = SceneTomlReader.ReadScene(path);
            Assert.NotNull(s.Physics);
            Assert.Equal(2.03e14, s.Physics!.AttractorMu);
            Assert.Equal(3185500.0, s.Physics.AttractorSurfaceRadius);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadScene_PartialAttractorSpec_Throws()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "a"
            [physics]
            mu = 2.03e14
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
    public void ReadScene_ParsesFixedCameraWithMoveSpeedAndShadowDistance()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "a"
            [camera]
            mode = "fixed"
            position = [0.0, 5.0, 10.0]
            fov_y = 70.0
            move_speed = 20.0
            shadow_distance = 1.0
            """);
        try
        {
            var s = SceneTomlReader.ReadScene(path);
            Assert.Equal("fixed", s.Camera.Mode);
            Assert.Equal(20.0f, s.Camera.MoveSpeed);
            Assert.Equal(1.0f, s.Camera.ShadowDistance);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("procedural_sky = true")]
    [InlineData("black = true")]
    public void ReadScene_ParsesNewEnvironmentModes(string envBody)
    {
        var path = TempToml($"""
            name = "x"
            [[entity]]
            model = "a"
            [environment]
            {envBody}
            """);
        try
        {
            var s = SceneTomlReader.ReadScene(path);
            Assert.True(s.Environment.ProceduralSky || s.Environment.Black);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadScene_EnvironmentBothHdriAndBlack_Throws()
    {
        var path = TempToml("""
            name = "x"
            [[entity]]
            model = "a"
            [environment]
            hdri = "env/studio.hdr"
            black = true
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
