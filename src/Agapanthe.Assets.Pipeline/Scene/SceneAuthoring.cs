using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Assets.Pipeline.Scene;

// The in-memory model a `.toml` scene/prefab parses to (directives NOT yet expanded). SceneCompiler turns an
// AuthoredScene + the cooked models it references + the authored prefabs into a flat SceneDefinition.

internal enum AuthoredItemKind { Entity, Grid, Cluster }

internal sealed class AuthoredItem
{
    public required AuthoredItemKind Kind { get; init; }

    // Entity / Grid / Cluster all name either a model key or a prefab.
    public string? Model { get; init; }
    public string? Prefab { get; init; }

    // Entity
    public Double3 Position { get; init; }
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public float Scale { get; init; } = 1f;
    public bool CastsShadow { get; init; } = true;

    // Grid
    public int Rows { get; init; } = 1;
    public int Cols { get; init; } = 1;
    public double SpacingMul { get; init; } = 1.5;

    // Cluster (a cube cluster of physics bodies — the drop scene)
    public int Count { get; init; } = 1;
    public float InverseMass { get; init; } = 1f;
    public float Restitution { get; init; } = 0.3f;
    public float? Radius { get; init; }
}

internal sealed class AuthoredLight
{
    public required string Kind { get; init; } // "directional" | "point"
    public Vector3 Color { get; init; } = Vector3.One;
    public float Intensity { get; init; } = 1f;
    public Vector3 Direction { get; init; }
    public Double3 Position { get; init; }
    public float Range { get; init; }
}

internal sealed class AuthoredCamera
{
    public string Mode { get; init; } = "frame-bounds"; // | "fixed"
    public float FovY { get; init; } = 60f;
    public bool FreeFly { get; init; }
    public Vector3 ViewDir { get; init; } = new(0f, 0.35f, 1f);
    public double DistanceMul { get; init; } = 1.5;
    public Double3 Position { get; init; }
    public float Yaw { get; init; }
    public float Pitch { get; init; }
    public float Near { get; init; }
    public float Far { get; init; }
    public float MoveSpeed { get; init; }        // Fixed only, Contenu-3c
    public float ShadowDistance { get; init; }   // Fixed only, Contenu-3c
}

internal sealed class AuthoredEnvironment
{
    public string? Hdri { get; init; }
    public bool ProceduralSky { get; init; }  // Contenu-3c — mutually exclusive with Hdri/Black
    public bool Black { get; init; }          // Contenu-3c — mutually exclusive with Hdri/ProceduralSky
}

/// <summary><see cref="AttractorMu"/> `> 0` selects a Newtonian point-attractor (Contenu-3c) instead of uniform
/// gravity — `attractor_center`/`mu`/`surface_radius` must all be present together in TOML or none of them
/// (a partial spec is rejected, not defaulted).</summary>
internal sealed class AuthoredPhysics
{
    public Vector3 Gravity { get; init; } = new(0f, -9.81f, 0f);
    public float GroundY { get; init; }
    public Double3 AttractorCenter { get; init; }
    public double AttractorMu { get; init; }
    public double AttractorSurfaceRadius { get; init; }
}

internal sealed class AuthoredRestore
{
    public required string Snapshot { get; init; }
}

/// <summary>Contenu-3c: one `[[system]]` block. <see cref="Kind"/> is the TOML string (`"probe_drop"`);
/// <see cref="SceneCompiler.ToSystem"/> validates it against the known set.</summary>
internal sealed class AuthoredSystem
{
    public required string Kind { get; init; }
    public required string ProbeModel { get; init; }
    public required float ProbeRadius { get; init; }

    // ProbeDrop
    public int Every { get; init; } = 1;
    public Double3 Centre { get; init; }
}

internal sealed class AuthoredScene
{
    public string Name { get; init; } = string.Empty;
    public Double3 WorldOrigin { get; init; }
    public List<AuthoredItem> Items { get; } = [];
    public List<AuthoredLight> Lights { get; } = [];
    public List<AuthoredSystem> Systems { get; } = [];
    public Vector3 Ambient { get; init; } = new(0.08f, 0.08f, 0.09f);
    public AuthoredCamera Camera { get; init; } = new();
    public AuthoredEnvironment Environment { get; init; } = new();
    public AuthoredPhysics? Physics { get; init; }
    public AuthoredRestore? Restore { get; init; }
}

/// <summary>A prefab is a flat list of entities (no hierarchy in 3b — spec §3.3).</summary>
internal sealed class AuthoredPrefab
{
    public List<AuthoredItem> Entities { get; } = [];
}
