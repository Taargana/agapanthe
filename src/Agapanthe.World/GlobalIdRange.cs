namespace Agapanthe.World;

/// <summary>
/// A half-open block of <c>GlobalId</c> values <c>[Start, EndExclusive)</c> a <see cref="GameWorld"/> allocates
/// from (MP-0b W2). Public — a host that partitions ids across processes/nodes must be able to name a block it
/// hands out, and this is exactly that block, with no protocol implied about who assigns it. <c>Start == 0</c> is
/// rejected: id 0 is <c>default(EntityRef)</c>, the "no entity" sentinel (<see cref="EntityRef"/>).
/// </summary>
public readonly record struct GlobalIdRange
{
    public ulong Start { get; }
    public ulong EndExclusive { get; }

    public GlobalIdRange(ulong start, ulong endExclusive)
    {
        if (start == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start), start, "GlobalIdRange.Start cannot be 0 — 0 is the EntityRef \"no entity\" sentinel.");
        }

        if (start >= endExclusive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endExclusive), endExclusive, "GlobalIdRange requires Start < EndExclusive.");
        }

        Start = start;
        EndExclusive = endExclusive;
    }

    /// <summary>The range every <see cref="GameWorld"/> used before MP-0b W2 — <c>[1, ulong.MaxValue)</c>. Bit-for-bit
    /// what the old bare counter did, so the parameterless <see cref="GameWorld()"/> constructor stays unchanged.</summary>
    public static GlobalIdRange Default => new(1, ulong.MaxValue);
}
