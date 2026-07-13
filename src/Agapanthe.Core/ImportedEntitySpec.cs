using System.Numerics;

namespace Agapanthe.Core;

/// <summary>
/// A GPU-free description of one imported drawable, produced by the render-side scene builder and consumed by
/// the world's <c>SpawnImported</c>. It is the seam between "the render side owns GPU resources" and "the world
/// owns entities" (spec §3.2).
/// <para>
/// <b>The transform is split</b> (spec §3.3, M3): <see cref="Position"/> is the world position in
/// <see cref="Double3"/>, and <see cref="RotationScale"/> is the rest of the asset's baked matrix with its
/// translation row zeroed. A float matrix cannot hold a far-out position without losing metres, so the position
/// must stay in double until the frame's camera origin is subtracted from it. Recombining the two at the origin
/// reproduces the asset matrix bit-for-bit (spec §6 condition a).
/// </para>
/// <para><see cref="BoundsCenter"/>/<see cref="BoundsRadius"/> are the mesh's LOCAL bounding sphere (spec §3.4,
/// M4): local because the entity's placement lives in <see cref="Position"/>/<see cref="RotationScale"/>, and the
/// world sphere is derived from them per frame. <see cref="Order"/> is the source mesh index, i.e. the stable
/// draw order (spec §6 condition b).</para>
/// </summary>
public readonly struct ImportedEntitySpec
{
    public readonly MeshHandle Mesh;
    public readonly MaterialHandle Material;

    /// <summary>World position, in double. Narrowed relative to the camera origin at draw time.</summary>
    public readonly Double3 Position;

    /// <summary>The asset's baked matrix with a zero translation row: rotation, scale and shear only.</summary>
    public readonly Matrix4x4 RotationScale;

    /// <summary>Local bounding-sphere centre (in the mesh's own space, before <see cref="RotationScale"/>).</summary>
    public readonly Vector3 BoundsCenter;

    /// <summary>Local bounding-sphere radius.</summary>
    public readonly float BoundsRadius;

    public readonly uint Order;

    public ImportedEntitySpec(
        MeshHandle mesh,
        MaterialHandle material,
        Double3 position,
        in Matrix4x4 rotationScale,
        Vector3 boundsCenter,
        float boundsRadius,
        uint order)
    {
        Mesh = mesh;
        Material = material;
        Position = position;
        RotationScale = rotationScale;
        BoundsCenter = boundsCenter;
        BoundsRadius = boundsRadius;
        Order = order;
    }
}
