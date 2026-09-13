using Agapanthe.Core;

namespace Agapanthe.World;

/// <summary>
/// One raycast hit (physics queries): the entity struck, the distance along the ray, and the point on its
/// <c>Bounds</c> sphere proxy where the ray met it. <see cref="Point"/> is approximate — the same sphere
/// over-approximation frustum culling already accepts (spec §3.2), never the true mesh surface.
/// </summary>
public readonly record struct RaycastHit(EntityRef Entity, double Distance, Double3 Point);
