using System.Numerics;
using System.Runtime.InteropServices;
using Agapanthe.Core;
using Arch.Core;

namespace Agapanthe.World;

// The Phase 2 M2 component set (spec §3.4). All are blittable structs with explicit Sequential layout (prereq of
// the future source-generated serialization). Only POSITION/bounds use Double3 (spec §3.6); rotation/scale/
// matrices stay float. GPU is referenced only by handle (MeshRef), never by a GPU type — the seam invariant
// (§3.2). Components are INTERNAL: nothing outside World needs them, and keeping them internal keeps Arch's
// Entity (carried by Parent) out of any public surface (audit W1 M3).

/// <summary>Stable identity, unique across processes (future streaming/serialization). Assigned at spawn.</summary>
[Component]
[StructLayout(LayoutKind.Sequential)]
internal struct GlobalId
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
[StructLayout(LayoutKind.Sequential)]
internal struct LocalTransform
{
    public Double3 Position;
    public Quaternion Rotation;
    public float Scale;
}

/// <summary>Parent link for hierarchy propagation. Holds an Arch <see cref="Entity"/> — an implementation detail
/// kept internal so the ECS type never reaches a public surface (audit W1 M3).</summary>
[Component]
[StructLayout(LayoutKind.Sequential)]
internal struct Parent
{
    public Entity Value;
}

/// <summary>
/// World-space rotation/scale: the output of the propagation system, or the baked matrix of an imported
/// drawable — with a <b>zero translation row</b>. The translation lives in <see cref="WorldPosition"/>, in
/// double (spec §3.3): a float matrix cannot carry a far-out position without losing metres. The render-list
/// builder recombines the two against the frame's camera origin.
/// </summary>
[Component]
[StructLayout(LayoutKind.Sequential)]
internal struct WorldTransform
{
    public Matrix4x4 Value;
}

/// <summary>
/// World-space position, in double — the half of the world transform that must not be narrowed until the
/// camera origin has been subtracted from it (spec §3.3). Every drawable and every propagated entity carries
/// one, alongside its <see cref="WorldTransform"/>.
/// </summary>
[Component]
[StructLayout(LayoutKind.Sequential)]
internal struct WorldPosition
{
    public Double3 Value;
}

/// <summary>The drawable payload: which mesh + material to draw, by handle (resolved to GPU resources on the
/// render side). No GPU type crosses into the world (spec §3.2).</summary>
[Component]
[StructLayout(LayoutKind.Sequential)]
internal struct MeshRef
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
[StructLayout(LayoutKind.Sequential)]
internal struct Bounds
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
[StructLayout(LayoutKind.Sequential)]
internal struct RenderOrder
{
    public uint Value;
}
