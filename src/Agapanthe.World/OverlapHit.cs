namespace Agapanthe.World;

/// <summary>
/// One region-query hit, shared by every shape query in <c>GameWorld.Queries.cs</c> (<see cref="GameWorld.OverlapSphere"/>,
/// <see cref="GameWorld.OverlapBox"/>, and any future shape). <see cref="Distance"/> is the center-to-center
/// distance between the query region's own geometric center — the query sphere's center for
/// <see cref="GameWorld.OverlapSphere"/>, the box midpoint <c>(min+max)/2</c> for <see cref="GameWorld.OverlapBox"/>
/// — and the entity's <c>Bounds</c> sphere center. It is NOT a surface-to-surface distance, and is not adjusted
/// for either shape's radius/extent (spec `docs/plans/2026-09-14-shape-queries-overlap-design.md` §3.1,
/// `docs/plans/2026-09-14-shape-queries-box-overlap-design.md` §3.1). Unlike <see cref="RaycastHit"/>, there is
/// no approximate hit <c>Point</c> — a region query has no single meaningful intersection point (a query region
/// can fully contain a candidate).
/// </summary>
public readonly record struct OverlapHit(EntityRef Entity, double Distance);
