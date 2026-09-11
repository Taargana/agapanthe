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

/// <summary>Camera setup. <see cref="SceneCameraMode.Fixed"/> is parsed/round-tripped but no 3b scene uses it
/// (it is <c>drive</c>'s camera, migrated in 3c).</summary>
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
}

public enum SceneEnvironmentMode : byte { None = 0, HdriPath = 1 }

public sealed record SceneEnvironment
{
    public required SceneEnvironmentMode Mode { get; init; }
    public string HdriPath { get; init; } = string.Empty;
}

public sealed record ScenePhysics
{
    public required Vector3 Gravity { get; init; }
    public required float GroundY { get; init; }
}

/// <summary>A snapshot to restore into the world after materialisation. Parsed/round-tripped but no 3b scene
/// uses it (it is <c>planet*</c>'s resume, migrated in 3c).</summary>
public sealed record SceneRestore
{
    public required string SnapshotPath { get; init; }
}
