using System.Globalization;

namespace Agapanthe.World;

/// <summary>
/// The identity of a solo world or shard, as a fact <b>per snapshot file</b>, not per entity (MP-0b W3). Public — a
/// host names one to merge worlds safely: two files stamped with the same non-<see cref="None"/> id are the same
/// universe, so a <see cref="GameWorld"/> that already knows its identity refuses to load a snapshot naming a
/// different one (<see cref="WorldSerializationException"/>). <c>None</c> — the default — is an honest "unidentified"
/// state, not a random identity: a <see cref="Guid"/> drawn at world construction would make two runs of the same
/// scene produce different snapshot bytes, breaking VS-1's cross-process determinism and <c>HeadlessSim</c>'s
/// JIT-vs-AOT hash. Two little-endian <c>ulong</c>s, not a <see cref="Guid"/> — <c>Guid.ToByteArray()</c> is
/// mixed-endian, and this format's determinism rests on explicit little-endian primitives throughout.
/// </summary>
public readonly struct UniverseId : IEquatable<UniverseId>
{
    public readonly ulong High;
    public readonly ulong Low;

    public UniverseId(ulong high, ulong low)
    {
        High = high;
        Low = low;
    }

    /// <summary>The all-zero, "unidentified universe" state — what every world has until a host or a loaded
    /// snapshot names one.</summary>
    public static UniverseId None => default;

    public bool Equals(UniverseId other) => High == other.High && Low == other.Low;

    public override bool Equals(object? obj) => obj is UniverseId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(High, Low);

    public static bool operator ==(UniverseId a, UniverseId b) => a.Equals(b);

    public static bool operator !=(UniverseId a, UniverseId b) => !a.Equals(b);

    /// <summary>32 lowercase hex digits (16 for <see cref="High"/>, then 16 for <see cref="Low"/>) — round-trips
    /// through <see cref="Parse"/>. A host can put one in a config file.</summary>
    public override string ToString() => $"{High:x16}{Low:x16}";

    public static UniverseId Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != 32)
        {
            throw new FormatException($"UniverseId hex must be 32 characters, got {hex.Length}.");
        }

        var high = ulong.Parse(hex.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var low = ulong.Parse(hex.AsSpan(16, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new UniverseId(high, low);
    }
}
