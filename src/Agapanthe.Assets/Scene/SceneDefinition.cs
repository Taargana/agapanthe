using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Assets.Scene;

/// <summary>
/// A cooked scene, as a <b>flat, already-expanded</b> list of entities plus the global blocks (Contenu-3b). All
/// directives (<c>[[grid]]</c>, <c>[[cluster]]</c>, prefab instances) were unrolled by the cooker — the runtime
/// <c>SceneMaterializer</c> just walks the list. GPU-free; names only <c>Agapanthe.Core</c> types. Produced by
/// <see cref="AgSceneFormat.Read"/>.
/// </summary>
public sealed record SceneDefinition
{
    public required string Name { get; init; }
    public required Double3 WorldOrigin { get; init; }
    public required IReadOnlyList<SceneEntity> Entities { get; init; }
    public required IReadOnlyList<SceneLight> Lights { get; init; }
    public required Vector3 Ambient { get; init; }
    public required SceneCamera Camera { get; init; }
    public required SceneEnvironment Environment { get; init; }
    public ScenePhysics? Physics { get; init; }
    public SceneRestore? Restore { get; init; }

    /// <summary>Contenu-3c: game systems a client-side host attaches after materialisation (e.g. a probe
    /// spawner). Empty for every scene with no gameplay system — <c>HeadlessSim</c> refuses (exit 1) any
    /// non-empty list, since a scene system is a client-only concept (window/camera-coupled).</summary>
    public IReadOnlyList<SceneSystem> Systems { get; init; } = [];
}

/// <summary>One drawable (or physics body). Transform is <c>Position</c> (world, double) + <c>Rotation</c> +
/// uniform <c>Scale</c>, composed with the model mesh's own baked transform at materialisation.</summary>
public sealed record SceneEntity
{
    public required AssetKey Model { get; init; }
    public int LocalMesh { get; init; }
    public int LocalMat { get; init; }
    public required Double3 Position { get; init; }
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public float Scale { get; init; } = 1f;
    public bool CastsShadow { get; init; } = true;
    public SceneBody? Body { get; init; }
}

public sealed record SceneBody
{
    public Vector3 Velocity { get; init; }
    public float InverseMass { get; init; } = 1f;
    public float Restitution { get; init; }
    public float Radius { get; init; } = 1f;
}

public enum SceneLightKind : byte { Directional = 0, Point = 1 }

public sealed record SceneLight
{
    public required SceneLightKind Kind { get; init; }
    public required Vector3 Color { get; init; }
    public required float Intensity { get; init; }
    public Vector3 Direction { get; init; }   // Directional
    public Double3 Position { get; init; }    // Point
    public float Range { get; init; }         // Point
}

public enum SceneCameraMode : byte { FrameBounds = 0, Fixed = 1 }

/// <summary>Slice-2 — which projection the camera uses. Its own GPU-free copy mirroring
/// <c>Agapanthe.Rendering.CameraProjection</c> (this assembly cannot reference Rendering) — same pattern as
/// every other closed cooked-scene enum. <see cref="Perspective"/> is the default; every scene before
/// Slice-2 is byte-identical.</summary>
public enum CameraProjection : byte { Perspective = 0, Orthographic = 1 }

/// <summary>Camera setup. <see cref="SceneCameraMode.Fixed"/> is used starting Contenu-3c: the 3 planet camera
/// framers' fully-deterministic eye/yaw/pitch/fov/near/far are baked at cook time into a fixed pose, with
/// <see cref="MoveSpeed"/>/<see cref="ShadowDistance"/> (Fixed-only; 0 = <see cref="SceneCameraMode.FrameBounds"/>
/// keeps deriving them dynamically from the scene's bounds, as before). <see cref="Projection"/>/
/// <see cref="OrthoWidth"/>/<see cref="OrthoHeight"/> (Slice-2, Fixed-only, same always-serialized convention)
/// let a Fixed camera be orthographic instead of perspective — <c>Agapanthe.App.SceneCameraApplier</c>
/// (Contenu-3b) copies them onto the live <c>Camera</c>.</summary>
public sealed record SceneCamera
{
    public required SceneCameraMode Mode { get; init; }
    public required float FovY { get; init; }
    public bool FreeFly { get; init; }
    public Vector3 ViewDir { get; init; }     // FrameBounds
    public float DistanceMul { get; init; } = 1.5f;
    public Double3 Position { get; init; }    // Fixed
    public float Yaw { get; init; }
    public float Pitch { get; init; }
    public float Near { get; init; }
    public float Far { get; init; }
    public float MoveSpeed { get; init; }         // Fixed only, Contenu-3c
    public float ShadowDistance { get; init; }    // Fixed only, Contenu-3c
    public CameraProjection Projection { get; init; } // Fixed only, Slice-2
    public float OrthoWidth { get; init; }            // Fixed only, Slice-2
    public float OrthoHeight { get; init; }           // Fixed only, Slice-2
}

/// <summary><see cref="ProceduralSky"/>/<see cref="Black"/> (Contenu-3c) carry no payload — the client derives
/// what they need at apply time (the sun direction from the scene's directional light, nothing at all for
/// <see cref="Black"/>) rather than baking a cooked HDR blob (deferred to Contenu-2b's real
/// <c>AssetKind.Environment</c>).</summary>
public enum SceneEnvironmentMode : byte { None = 0, HdriPath = 1, ProceduralSky = 2, Black = 3 }

public sealed record SceneEnvironment
{
    public required SceneEnvironmentMode Mode { get; init; }
    public string HdriPath { get; init; } = string.Empty;
}

/// <summary><see cref="Mu"/> `== 0` (default) means no attractor — <see cref="Gravity"/>/<see cref="GroundY"/>
/// drive a uniform-gravity <c>PhysicsSettings</c>, byte-identical to Contenu-3b. <see cref="Mu"/> `&gt; 0` selects
/// a Newtonian point-attractor instead (Contenu-3c), via <c>PhysicsSettings.WithAttractor</c>.</summary>
public sealed record ScenePhysics
{
    public required Vector3 Gravity { get; init; }
    public required float GroundY { get; init; }
    public Double3 AttractorCenter { get; init; }
    public double Mu { get; init; }
    public double SurfaceRadius { get; init; }
}

/// <summary>A snapshot to restore into the world after materialisation. Parsed/round-tripped but no scene uses
/// it yet (it is <c>planet*</c>'s resume, migrated across 3c).</summary>
public sealed record SceneRestore
{
    public required string SnapshotPath { get; init; }
}

/// <summary>Contenu-3c: which gameplay system a <see cref="SceneSystem"/> block describes. A closed tagged
/// union — same style as <see cref="SceneLightKind"/>/<see cref="SceneCameraMode"/>/<see cref="SceneEnvironmentMode"/>,
/// never an open/generic bag. <see cref="ProbeDrop"/> shipped in 3c-1; <see cref="LandingChallenge"/> in 3c-2;
/// <see cref="DriveControl"/> in 3c-3 — each variant's fields were added to <see cref="SceneSystem"/> only once
/// it had a real consumer (see <c>.agmodel</c> v1→v2, `.agscene` v1→v2→v3→v4), never pre-declared speculatively.</summary>
public enum SceneSystemKind : byte { ProbeDrop = 0, LandingChallenge = 1, DriveControl = 2 }

/// <summary>Contenu-3c: data for one client-attached game system. GPU-free — names only <see cref="AssetKey"/>/
/// <see cref="Double3"/> — so <c>Agapanthe.Scene</c> can carry it without referencing <c>Agapanthe.Engine</c>; the
/// caller in <c>Agapanthe.App</c> (which does reference <c>Engine</c>) constructs the concrete <c>ISystem</c>.</summary>
public sealed record SceneSystem
{
    public required SceneSystemKind Kind { get; init; }

    /// <summary>The probe model this system spawns at runtime (the golden-angle drop / the aimed landing shot —
    /// shared by <see cref="ProbeDrop"/>/<see cref="LandingChallenge"/>, same kind of runtime-spawned drawable).
    /// <see cref="AssetKey.None"/> (the default) for <see cref="DriveControl"/>, which spawns nothing — 3c-3
    /// relaxed this from `required` specifically so a non-spawning kind never has to author a dummy model
    /// (audit finding, 3c-2 🟡 F2).</summary>
    public AssetKey ProbeModel { get; init; }
    public int ProbeLocalMesh { get; init; }
    public int ProbeLocalMat { get; init; }
    public float ProbeRadius { get; init; }

    // ProbeDrop
    public int Every { get; init; }
    public Double3 Centre { get; init; }

    // LandingChallenge (3c-2). AttractorCenter/SurfaceRadius are deliberately NOT duplicated here — the
    // factory reads them off the scene's own MaterializeResult.Physics (a LandingChallenge system with no
    // scene attractor is a cook-time authoring error, validated by SceneCompiler, not a runtime field).
    public Double3 ZoneCenter { get; init; }
    public double ZoneRadius { get; init; }
    public double SurfaceBand { get; init; }
    public double DropHeight { get; init; }
    public int TargetCount { get; init; }
    public int ShotBudget { get; init; }

    /// <summary>Where the F5 quicksave writes. Closes D7 (the <c>AGAPANTHE_SAVE</c> host-level vs. F5-quicksave
    /// name collision) by making the quicksave path authored scene data instead of an inline env-var read.</summary>
    public string QuicksavePath { get; init; } = "";

    // DriveControl (3c-3). Names which of the scene's flat, cook-time-ordered Entities this system steers —
    // MaterializeResult.SpawnedEntities[ControlledEntityIndex] is the EntityRef a client factory calls
    // GameWorld.SetBodyVelocity on. MoveSpeed replaces the DriveSceneRecipe.DriveMoveSpeed constant.
    public int ControlledEntityIndex { get; init; }
    public float MoveSpeed { get; init; }
}
