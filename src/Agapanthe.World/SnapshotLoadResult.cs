namespace Agapanthe.World;

/// <summary>How <see cref="GameWorld.Load(Stream, SnapshotAllocatorPolicy)"/> reconciled the snapshot's
/// <see cref="UniverseId"/> against this world's own (MP-0b W3, spec's "adoption is logged" mitigation) — the
/// mismatched case never reaches here, it throws instead. <see cref="Confirmed"/> is the case every server node
/// hits on every ordinary load.</summary>
public enum UniverseOutcome
{
    /// <summary>Both this world and the snapshot were <see cref="UniverseId.None"/> — still unidentified.</summary>
    StayedUnidentified,

    /// <summary>The snapshot named a universe, this world had none — this world now carries it.</summary>
    Adopted,

    /// <summary>This world already had a universe, the snapshot had none — this world's identity is unchanged.</summary>
    Kept,

    /// <summary>Both named the same universe — no state changed, but the fact was checked.</summary>
    Confirmed,
}

/// <summary>What <see cref="GameWorld.Load(Stream, SnapshotAllocatorPolicy)"/> did, returned instead of logged
/// (MP-0b W3, audit finding): <c>Agapanthe.World</c> takes no logging dependency by design, so the caller — the
/// one entity that knows whether this event is worth a log line — decides.</summary>
public readonly record struct SnapshotLoadResult(UniverseOutcome Universe, int EntityCount);
