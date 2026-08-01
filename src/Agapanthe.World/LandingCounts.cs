namespace Agapanthe.World;

/// <summary>
/// The result of <see cref="GameWorld.QuerySurfaceContacts"/> (VS-3): a GPU-free, Arch-free snapshot of the rigid
/// bodies relative to an attractor surface and a target zone. Deliberately generic (a spatial aggregation, not a
/// gameplay concept) so the World stays gameplay-free — the landing-challenge rule composes these three counts.
/// </summary>
/// <param name="Total">Number of rigid bodies in the world.</param>
/// <param name="Airborne">Bodies whose surface (|p−C|−r) is more than <c>surfaceBand</c> above the attractor surface —
/// i.e. still falling or bouncing, not yet settled on the ground.</param>
/// <param name="InZone">Bodies on the surface (not airborne) AND within the target zone radius of its centre.</param>
public readonly record struct LandingCounts(int Total, int Airborne, int InZone);
