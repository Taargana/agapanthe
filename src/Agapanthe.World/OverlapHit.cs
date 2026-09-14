namespace Agapanthe.World;

/// <summary>
/// One sphere-overlap hit (shape queries): the entity found and the center-to-center distance between the query
/// center and the entity's <c>Bounds</c> sphere center — NOT a surface-to-surface distance, and not adjusted for
/// either sphere's radius (spec `docs/plans/2026-09-14-shape-queries-overlap-design.md` §3.1). Unlike
/// <see cref="RaycastHit"/>, there is no approximate hit <c>Point</c> — a region query has no single meaningful
/// intersection point (a query sphere can fully contain a candidate).
/// </summary>
public readonly record struct OverlapHit(EntityRef Entity, double Distance);
