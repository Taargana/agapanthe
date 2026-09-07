using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Agapanthe.Core;
using Agapanthe.Graphics;
using Agapanthe.Rendering;
using Agapanthe.World;

namespace Sandbox;

// VS-3 planet-challenge setup bundle (what SetupPlanetChallenge produces).
internal readonly record struct ChallengeSetup(
    PhysicsSettings Physics, ImportedEntitySpec ProbeSpec, float ProbeRadius, Double3 Target, Double3 Beacon,
    double TargetRadius, double SurfaceBand, double DropHeight, int TargetCount, int ShotBudget);

/// <summary>The P3-M8 planet family: planet + emissive Sun spheres in Double3, radial Newtonian gravity, and the
/// VS-2/VS-3 probe/challenge setups. Lifted verbatim from the pre-extraction <c>Program.cs</c>.</summary>
internal static class PlanetContent
{
    /// <summary>A sphere ModelAsset at a given RADIUS (baked into the local vertex positions). Reuses
    /// Primitives.UvSphere and splits its interleaved Vertex[] into the SoA arrays MeshAsset wants.</summary>
    public static ModelAsset BuildSphereModel(float radius, MaterialAsset material, string name, int segments = 128, int rings = 64)
    {
        var (verts, idx) = Primitives.UvSphere(segments, rings);
        var positions = new Vector3[verts.Length];
        var normals = new Vector3[verts.Length];
        var tangents = new Vector4[verts.Length];
        var uvs = new Vector2[verts.Length];
        for (var i = 0; i < verts.Length; i++)
        {
            positions[i] = verts[i].Position * radius;
            normals[i] = verts[i].Normal;
            tangents[i] = verts[i].Tangent;
            uvs[i] = verts[i].Uv;
        }

        var indices = new uint[idx.Length];
        for (var i = 0; i < idx.Length; i++)
        {
            indices[i] = idx[i];
        }

        var mesh = new MeshAsset
        {
            Positions = positions,
            Normals = normals,
            Tangents = tangents,
            Uvs = uvs,
            Indices = indices,
            MaterialIndex = 0,
            Name = name,
        };

        return new ModelAsset
        {
            Meshes = [mesh],
            Materials = [material],
            Images = [],
            Name = name,
        };
    }

    /// <summary>A pure-black environment (P3-M8): every texel zero → IBL bakes to zero ambient, skybox paints black.</summary>
    public static HdrImageAsset BuildBlackEnvironment()
        => new() { RgbaPixels = new float[16 * 8 * 4], Width = 16, Height = 8 };

    /// <summary>Builds the planet + Sun (AGAPANTHE_SCENE=planet). Returns the Sun→planet travel direction, the
    /// planet radius, and the Sun's world origin. Scale = 1/2 of reality, UNIFORM.</summary>
    public static (Vector3 SunTravelDir, double PlanetRadius, Double3 SunOrigin) SetupPlanetScene(
        GraphicsDevice device, ResourceRegistry registry, GameWorld world, DescriptorSetLayout materialLayout,
        Double3 planetOrigin, bool spawnEntities = true)
    {
        var planetRadius = SandboxEnv.EnvDouble("AGAPANTHE_PLANET_RADIUS", 6_371_000d / 2d);
        var sunRadius = SandboxEnv.EnvDouble("AGAPANTHE_SUN_RADIUS", 696_340_000d / 2d);
        var sunDistance = SandboxEnv.EnvDouble("AGAPANTHE_SUN_DISTANCE", 1.495_978_707e11 / 2d);

        var sunFromPlanet = Vector3.Normalize(SandboxEnv.EnvVector3("AGAPANTHE_SUN_DIR", new Vector3(0.55f, 0.25f, -0.8f)));
        var sunOrigin = planetOrigin + new Double3(new Vector3(
            (float)(sunFromPlanet.X * sunDistance),
            (float)(sunFromPlanet.Y * sunDistance),
            (float)(sunFromPlanet.Z * sunDistance)));

        var planetMaterial = new MaterialAsset
        {
            BaseColorFactor = new Vector4(0.16f, 0.42f, 0.62f, 1f),
            MetallicFactor = 0f,
            RoughnessFactor = 0.9f,
            Name = "PlanetSurface",
        };
        var (_, planetSpecs) = registry.Load(
            device, BuildSphereModel((float)planetRadius, planetMaterial, "Planet", 128, 64), materialLayout,
            new AssetKey("sandbox/planet-surface"), planetOrigin);
        if (spawnEntities)
        {
            foreach (var s in planetSpecs)
            {
                world.SpawnImported(in s, castsShadow: false);
            }
        }

        var sunMaterial = new MaterialAsset
        {
            BaseColorFactor = new Vector4(0.02f, 0.02f, 0.02f, 1f),
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
            EmissiveFactor = new Vector3(1f, 0.95f, 0.85f),
            EmissiveStrength = 40f,
            Name = "SunSurface",
        };
        var (_, sunSpecs) = registry.Load(
            device, BuildSphereModel((float)sunRadius, sunMaterial, "Sun", 64, 32), materialLayout,
            new AssetKey("sandbox/sun-surface"), sunOrigin);
        if (spawnEntities)
        {
            foreach (var s in sunSpecs)
            {
                world.SpawnImported(in s, castsShadow: false);
            }
        }

        var sunAngularDeg = 2.0 * Math.Atan(sunRadius / sunDistance) * 180.0 / Math.PI;
        Log.Info(
            $"Sandbox: [scene] planet radius {planetRadius / 1000:F0} km @ origin, Sun radius {sunRadius / 1000:F0} km " +
            $"@ {sunDistance:E2} m (1/2 real scale, Sun subtends {sunAngularDeg:F2}°; sun-only lighting, black space).");

        return (-sunFromPlanet, planetRadius, sunOrigin);
    }

    /// <summary>VS-2 planet-drop setup: a single Newtonian attractor at the planet centre, a radial ground
    /// half-space at the planet radius, and a small probe sphere loaded once.</summary>
    public static (PhysicsSettings Physics, ImportedEntitySpec ProbeSpec, Double3 DropCentre, float ProbeRadius) SetupPlanetDrop(
        GraphicsDevice device, ResourceRegistry registry, DescriptorSetLayout materialLayout,
        Double3 planetCentre, double planetRadius, float fixedDt)
    {
        var probeRadius = (float)SandboxEnv.EnvDouble("AGAPANTHE_PROBE_RADIUS", 3.0);
        var dropHeight = SandboxEnv.EnvDouble("AGAPANTHE_DROP_HEIGHT", 120.0);
        var mu = SandboxEnv.EnvDouble("AGAPANTHE_PLANET_MU", 10.0 * planetRadius * planetRadius);
        var physics = new PhysicsSettings(Vector3.Zero, groundY: 0f, fixedDt: fixedDt)
            .WithAttractor(planetCentre, mu, planetRadius);

        var probeMaterial = new MaterialAsset
        {
            BaseColorFactor = new Vector4(0.9f, 0.35f, 0.1f, 1f),
            MetallicFactor = 0.1f,
            RoughnessFactor = 0.6f,
            Name = "Probe",
        };
        var dropCentre = planetCentre + new Double3(0.0, planetRadius + dropHeight, 0.0);
        var (_, probeSpecs) = registry.Load(
            device, BuildProbeSphere(probeRadius, probeMaterial), materialLayout, new AssetKey("sandbox/probe"), dropCentre);

        var g = mu / (planetRadius * planetRadius);
        Log.Info(
            $"Sandbox: [scene] planet-drop — probe r={probeRadius:F1} m from {dropHeight:F0} m up, surface g={g:F2} m/s² " +
            $"(μ={mu:E2}); tune with AGAPANTHE_DROP_EVERY / _PLANET_MU / _DROP_HEIGHT / _PROBE_RADIUS, key B drops one.");
        return (physics, probeSpecs[0], dropCentre, probeRadius);
    }

    /// <summary>VS-3 planet-challenge setup: the same attractor as planet-drop + a target zone and a floating
    /// emissive beacon. Loads probe + beacon assets in a FIXED order (Option 1 seam).</summary>
    public static ChallengeSetup SetupPlanetChallenge(
        GraphicsDevice device, ResourceRegistry registry, GameWorld world, DescriptorSetLayout materialLayout,
        Double3 planetCentre, double planetRadius, float fixedDt, bool spawnEntities)
    {
        var probeRadius = (float)SandboxEnv.EnvDouble("AGAPANTHE_PROBE_RADIUS", 3.0);
        var dropHeight = SandboxEnv.EnvDouble("AGAPANTHE_DROP_HEIGHT", 120.0);
        var mu = SandboxEnv.EnvDouble("AGAPANTHE_PLANET_MU", 10.0 * planetRadius * planetRadius);
        var physics = new PhysicsSettings(Vector3.Zero, groundY: 0f, fixedDt: fixedDt)
            .WithAttractor(planetCentre, mu, planetRadius);

        var targetRadius = SandboxEnv.EnvDouble("AGAPANTHE_ZONE_RADIUS", 15.0);
        var surfaceBand = SandboxEnv.EnvDouble("AGAPANTHE_SURFACE_BAND", 3.0 * probeRadius);
        var targetCount = (int)Math.Max(SandboxEnv.EnvDouble("AGAPANTHE_CHALLENGE_N", 3.0), 1.0);
        var shotBudget = (int)Math.Max(SandboxEnv.EnvDouble("AGAPANTHE_CHALLENGE_SHOTS", 6.0), 1.0);

        var dist = SandboxEnv.EnvDouble("AGAPANTHE_TARGET_DIST", 500.0);
        var phi = SandboxEnv.EnvDouble("AGAPANTHE_TARGET_DIR", 0.0) * Math.PI / 180.0;
        var theta = dist / planetRadius;
        var tdir = new Double3(Math.Sin(theta) * Math.Cos(phi), Math.Cos(theta), Math.Sin(theta) * Math.Sin(phi));
        var target = planetCentre + (tdir * planetRadius);
        var markerHeight = SandboxEnv.EnvDouble("AGAPANTHE_TARGET_MARKER_HEIGHT", 40.0);
        var beacon = planetCentre + (tdir * (planetRadius + markerHeight));

        var probeMaterial = new MaterialAsset
        {
            BaseColorFactor = new Vector4(0.9f, 0.35f, 0.1f, 1f),
            MetallicFactor = 0.1f,
            RoughnessFactor = 0.6f,
            Name = "Probe",
        };
        var (_, probeSpecs) = registry.Load(
            device, BuildProbeSphere(probeRadius, probeMaterial), materialLayout, new AssetKey("sandbox/probe"), target);

        var beaconMaterial = new MaterialAsset
        {
            BaseColorFactor = new Vector4(0.02f, 0.02f, 0.02f, 1f),
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
            EmissiveFactor = new Vector3(0.15f, 1f, 0.35f),
            EmissiveStrength = 30f,
            Name = "Beacon",
        };
        var (_, beaconSpecs) = registry.Load(
            device, BuildSphereModel((float)targetRadius, beaconMaterial, "Beacon", 32, 16), materialLayout,
            new AssetKey("sandbox/beacon"), beacon);
        if (spawnEntities)
        {
            foreach (var s in beaconSpecs)
            {
                world.SpawnImported(in s, castsShadow: false);
            }
        }

        Log.Info(
            $"Sandbox: [scene] planet-challenge — target {targetRadius:F0} m zone {dist:F0} m from start, land {targetCount} " +
            $"in ≤ {shotBudget} shots. Tune AGAPANTHE_CHALLENGE_N/_SHOTS/_ZONE_RADIUS/_TARGET_DIST/_DIR.");
        return new ChallengeSetup(
            physics, probeSpecs[0], probeRadius, target, beacon, targetRadius, surfaceBand, dropHeight, targetCount, shotBudget);
    }

    // The probe sphere (24/12) shared by planet-drop and planet-challenge — matches the pre-extraction call
    // BuildSphereModel(probeRadius, probeMaterial, "Probe", 24, 12).
    private static ModelAsset BuildProbeSphere(float radius, MaterialAsset material)
        => BuildSphereModel(radius, material, "Probe", 24, 12);
}
