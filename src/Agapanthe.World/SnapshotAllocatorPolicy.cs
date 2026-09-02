namespace Agapanthe.World;

/// <summary>
/// How <see cref="GameWorld.Load(Stream, SnapshotAllocatorPolicy)"/> reconciles the snapshot's id counter against
/// this (already-constructed) world's <see cref="GlobalIdRange"/> (MP-0b W3, decision 3).
/// <para>
/// This is deliberately NOT a <c>GlobalIdRange?</c> parameter (audit finding): the world's range is a fact fixed at
/// construction (<see cref="GameWorld(GlobalIdRange, UniverseId)"/>) — a lease a host was handed once. Letting
/// <c>Load</c> also accept a range would let that lease be silently reassigned by a deserialization call instead of
/// by construction, giving the same fact two competing points of truth. A policy carries no data: the receiving
/// world already has the range it needs.
/// </para>
/// </summary>
public enum SnapshotAllocatorPolicy
{
    /// <summary>Adopt the snapshot's id counter — VS-1's original behaviour. Rejected if it falls outside this
    /// world's declared <see cref="GlobalIdRange"/> (a world with a declared range loading a foreign allocator
    /// state is exactly the mistake <see cref="KeepMine"/> exists for).</summary>
    AdoptFromHeader,

    /// <summary>Ignore the snapshot's id counter entirely; this world keeps allocating from wherever its own
    /// construction-time <see cref="GlobalIdRange"/> already had it (unchanged, since <c>Load</c> requires a fresh,
    /// unspawned world). The receiving-node case: a node that accepts entities it did not create must not advance
    /// ITS OWN allocator by accepting them.</summary>
    KeepMine,
}
