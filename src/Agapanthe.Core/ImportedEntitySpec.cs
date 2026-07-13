using System.Numerics;

namespace Agapanthe.Core;

/// <summary>
/// A GPU-free description of one imported drawable, produced by the render-side scene builder and consumed by
/// the world's <c>SpawnImported</c>. It is the seam between "the render side owns GPU resources" and "the world
/// owns entities" (spec §3.2).
/// <para><see cref="World"/> is the mesh's world matrix copied bit-for-bit from the asset — no
/// decompose/recompose (spec §6 condition a). <see cref="BoundsMin"/>/<see cref="BoundsMax"/> are the float
/// vertex-fold widened to <see cref="Double3"/>. <see cref="Order"/> is the source mesh index, i.e. the stable
/// draw order (spec §6 condition b).</para>
/// </summary>
public readonly struct ImportedEntitySpec
{
    public readonly MeshHandle Mesh;
    public readonly MaterialHandle Material;
    public readonly Matrix4x4 World;
    public readonly Double3 BoundsMin;
    public readonly Double3 BoundsMax;
    public readonly uint Order;

    public ImportedEntitySpec(
        MeshHandle mesh,
        MaterialHandle material,
        in Matrix4x4 world,
        Double3 boundsMin,
        Double3 boundsMax,
        uint order)
    {
        Mesh = mesh;
        Material = material;
        World = world;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        Order = order;
    }
}
