namespace Agapanthe.World;

/// <summary>
/// Total order over an unordered pair of <see cref="GlobalId"/> values (raw <see langword="ulong"/>s here — the
/// physics scratch already unwraps them), used to sort broadphase contact pairs into a deterministic resolution
/// order (P3-M3) independent of Arch's chunk iteration order.
/// <para>
/// MP-0b W1: replaces the 32-bit-per-half packing this milestone found in <c>GameWorld.Physics</c>
/// (<c>(gidLo &lt;&lt; 32) | (uint)gidHi</c>), which silently unified any two pairs whose ids agreed in their low
/// 32 bits — harmless only while <see cref="GlobalId"/> stayed a dense per-run counter, which MP-0b removes as an
/// assumption. Lexicographic on <c>(Min, Max)</c>, comparable via the constrained generic
/// <see cref="IComparable{T}"/> path so <see cref="Array.Sort{TKey, TValue}(TKey[], TValue[], int, int)"/> never
/// boxes (P2-M2 precedent: a struct <c>IComparer&lt;T&gt;</c> instance boxed ~88 B/call).
/// </para>
/// </summary>
internal readonly struct ContactPairKey : IComparable<ContactPairKey>, IEquatable<ContactPairKey>
{
    public readonly ulong Min;
    public readonly ulong Max;

    /// <summary>Normalizes argument order — either id may be passed first, the smaller always lands in <see cref="Min"/>.</summary>
    public ContactPairKey(ulong a, ulong b)
    {
        if (a <= b)
        {
            Min = a;
            Max = b;
        }
        else
        {
            Min = b;
            Max = a;
        }
    }

    public int CompareTo(ContactPairKey other)
    {
        var byMin = Min.CompareTo(other.Min);
        return byMin != 0 ? byMin : Max.CompareTo(other.Max);
    }

    public bool Equals(ContactPairKey other) => Min == other.Min && Max == other.Max;

    public override bool Equals(object? obj) => obj is ContactPairKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Min, Max);

    public static bool operator ==(ContactPairKey left, ContactPairKey right) => left.Equals(right);

    public static bool operator !=(ContactPairKey left, ContactPairKey right) => !left.Equals(right);
}
