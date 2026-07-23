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

/// <summary>Stable identity, unique across processes (future streaming/serialization). Assigned at spawn.
/// <para>
/// <b>Downstream packing depends on this staying a dense per-run counter (&lt; 2³²).</b> The physics contact-pair
/// sort key packs two <c>GlobalId</c>s into a <c>ulong</c> (P3-M3, <c>GameWorld.Physics</c>). The day streaming makes
/// ids process-unique (sparse / high-bit-tagged), that packing — and any other 32-bit assumption — must be revisited.
/// </para></summary>
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
/// A LOCAL bounding sphere (spec §3.4, M4) — centre and radius in the entity's own space, baked once at import.
/// It is transformed to world space per frame for culling: <c>worldCentre = Centre·WorldTransform +
/// WorldPosition</c>, <c>worldRadius = Radius × maxAxisScale(WorldTransform)</c>.
/// <para>
/// A sphere, not the M2/M3 world AABB, for two reasons the audit called out: a static world AABB is WRONG the
/// moment an entity rotates or moves, and re-transforming an AABB each frame gives an inflated box (8 corners or
/// 6 abs-dots). A sphere's radius is rotation-invariant, costs 16 bytes instead of 48, and the frustum test is
/// six dot products. It over-covers slightly (never under-covers), so culling can keep an off-screen object,
/// never drop a visible one.
/// </para>
/// </summary>
[Component]
[StructLayout(LayoutKind.Sequential)]
internal struct Bounds
{
    public Vector3 Center;
    public float Radius;
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

/// <summary>
/// Linear velocity of a rigid body, in metres/second (P3-M3). Float, not <see cref="Double3"/>: a velocity
/// magnitude never needs double precision, and integrating <c>pos += (Double3)(Linear · dt)</c> keeps the
/// far-from-origin precision in the <see cref="WorldPosition"/> alone (spec §3, decision log).
/// </summary>
[Component]
[StructLayout(LayoutKind.Sequential)]
internal struct Velocity
{
    public Vector3 Linear;
}

/// <summary>
/// Tag component (P3-M5 CSM): a drawable carrying it RECEIVES shadows but does not CAST them. The shadow-caster
/// collection excludes it (a <c>WithNone&lt;NoShadowCast&gt;</c> query), which tightens every cascade's fit and
/// kills the self-shadow acne of a large flat receiver like the ground plane. A one-byte payload rather than a
/// zero-size struct, so Arch stores it in a normal chunk array (rooted for AOT like every other component).
/// </summary>
[Component]
[StructLayout(LayoutKind.Sequential)]
internal struct NoShadowCast
{
    public byte Value;
}

/// <summary>
/// The drawable's index in the persistent sorted candidate buffer (P3-M6). Assigned at each structural rebuild
/// (<see cref="GameWorld.CollectRenderLists"/>) and STABLE between rebuilds — which is what lets a per-entity
/// dirty patch address a fixed GPU slot without re-sorting. <c>-1</c> means "not yet assigned" (a freshly spawned
/// drawable, before the next rebuild). Present on every drawable (<c>MaterialiseDrawable</c>) and every physics
/// body (<c>SpawnBody</c>), so the dirty-tracking mutation surfaces can read it back.
/// <para>
/// Stability holds precisely because <see cref="RenderOrder"/> (the sort tie-break) is position-independent; it
/// would break the day depth enters the sort key (backlog §0), which is why that stays out of scope here.
/// </para>
/// </summary>
[Component]
[StructLayout(LayoutKind.Sequential)]
internal struct InstanceSlot
{
    public int Value;
}

/// <summary>
/// The rigid-body payload (P3-M3, linear only — no rotation/inertia in v1). <see cref="InverseMass"/> is
/// <c>1/m</c> so an immovable body is <c>0</c> (division-free in the impulse solver); <see cref="Restitution"/>
/// is the bounciness in [0,1]; <see cref="Radius"/> is the COLLISION radius in world metres, independent of the
/// render <see cref="Bounds"/> sphere. A body is a drawable created WITH this component (never added later — an
/// add-component is an archetype move), so non-physics drawables keep their exact archetype.
/// </summary>
[Component]
[StructLayout(LayoutKind.Sequential)]
internal struct RigidBody
{
    public float InverseMass;
    public float Restitution;
    public float Radius;
}
