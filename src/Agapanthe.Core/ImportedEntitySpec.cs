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
/// <para><see cref="BoundsMin"/>/<see cref="BoundsMax"/> are the world-space vertex fold. <see cref="Order"/> is
/// the source mesh index, i.e. the stable draw order (spec §6 condition b).</para>
/// </summary>
public readonly struct ImportedEntitySpec
{
    public readonly MeshHandle Mesh;
    public readonly MaterialHandle Material;

    /// <summary>World position, in double. Narrowed relative to the camera origin at draw time.</summary>
    public readonly Double3 Position;

    /// <summary>The asset's baked matrix with a zero translation row: rotation, scale and shear only.</summary>
    public readonly Matrix4x4 RotationScale;

    public readonly Double3 BoundsMin;
    public readonly Double3 BoundsMax;
    public readonly uint Order;

    public ImportedEntitySpec(
        MeshHandle mesh,
        MaterialHandle material,
        Double3 position,
        in Matrix4x4 rotationScale,
        Double3 boundsMin,
        Double3 boundsMax,
        uint order)
    {
        Mesh = mesh;
        Material = material;
        Position = position;
        RotationScale = rotationScale;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        Order = order;
    }
}
