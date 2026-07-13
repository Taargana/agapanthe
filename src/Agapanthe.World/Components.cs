using System.Numerics;
using Agapanthe.Core;
using Arch.Core;

namespace Agapanthe.World;

// The Phase 2 M2 component set (spec §3.4). All are blittable structs. Only POSITION needs double precision
// (spec §3.6); rotation/scale/matrices stay float. GPU is referenced only by handle (MeshRef), never by a GPU
// type — the seam invariant (§3.2).

/// <summary>Stable identity, unique across processes (future streaming/serialization). Assigned at spawn.</summary>
[Component]
public struct GlobalId
{
    public ulong Value;
}

/// <summary>
/// Local (or, for a root, world) transform of a hierarchical entity. Position is <see cref="Double3"/> for
/// large-coordinate precision; rotation and scale stay float. Entities that carry it are driven by the
/// transform-propagation system (M2 W2). Imported drawables do NOT carry it — they hold a baked
/// <see cref="WorldTransform"/> directly (spec §6 condition a, byte-identical).
/// </summary>
[Component]
public struct LocalTransform
{
    public Double3 Position;
    public Quaternion Rotation;
    public float Scale;
}

/// <summary>Parent link for hierarchy propagation. Holds an Arch <see cref="Entity"/> — an implementation detail
/// that never leaves this project (the field and this component are only touched by the world's own systems).</summary>
[Component]
public struct Parent
{
    public Entity Value;
}

/// <summary>World-space transform: the output of the propagation system, or the bit-exact baked matrix of an
/// imported drawable. This is what the render-list builder reads.</summary>
[Component]
public struct WorldTransform
{
    public Matrix4x4 Value;
}

/// <summary>The drawable payload: which mesh + material to draw, by handle (resolved to GPU resources on the
/// render side). No GPU type crosses into the world (spec §3.2).</summary>
[Component]
public struct MeshRef
{
    public MeshHandle Mesh;
    public MaterialHandle Material;
}

/// <summary>
/// Axis-aligned bounds. In M2 this stores the WORLD-space box (static, baked from the vertex fold) so system 2
/// is a pure union that byte-identically replaces <c>Scene.Bounds*</c>. It becomes a LOCAL AABB transformed per
/// frame (<c>localAABB × WorldTransform</c>) in M4 when entities move and culling needs it (spec §3.4 deviation,
/// board D5).
/// </summary>
[Component]
public struct Bounds
{
    public Double3 Min;
    public Double3 Max;
}

/// <summary>
/// Stable draw order. Arch's iteration order is by archetype/chunk and is NOT insertion-stable, so the
/// render-list builder sorts by this before drawing to keep the draw order deterministic (spec §6 condition b).
/// In M2 it is the source mesh index.
/// </summary>
[Component]
public struct RenderOrder
{
    public uint Value;
}
