namespace Agapanthe.World;

/// <summary>
/// An opaque, stable handle to an entity — its <see cref="GlobalId"/> (a <c>ulong</c>), NOT an Arch
/// <see cref="Arch.Core.Entity"/>. Two things forced this (P3-M2, decision D2, corrected in V2 against the
/// decompiled Arch 2.1.0):
/// <list type="number">
/// <item>A deferred <c>Spawn</c> must hand back a usable handle BEFORE the entity exists — the entity is only
/// created at the end-of-stage barrier. An Arch <c>Entity</c> cannot be fabricated for a not-yet-created entity
/// (its constructor is internal to Arch), and Arch's own buffered entity is invalidated by playback. A
/// <c>GlobalId</c> is assigned at enqueue time and outlives the barrier — it is the identity that <b>precedes
/// creation</b>.</item>
/// <item>It keeps the Arch <c>Entity</c> out of even the internal handle surface: consumers (Rendering, Sandbox,
/// tests) already cannot compile against Arch (<c>PrivateAssets="compile"</c>), and now they hold a plain
/// <c>ulong</c> identity that <see cref="GameWorld"/> resolves to the real entity through its <c>_live</c> map.</item>
/// </list>
/// </summary>
/// <remarks>
/// Process-local: the <c>ulong</c> is a per-run monotonic counter, not a persisted key. Cross-process identity
/// (serialization/streaming) is a separate future concern. <c>default(EntityRef)</c> (id 0) is the sentinel
/// "no entity" — the id counter starts at 1.
/// </remarks>
public readonly struct EntityRef : IEquatable<EntityRef>
{
    internal readonly ulong Id;

    internal EntityRef(ulong id) => Id = id;

    /// <summary>True if this is <c>default(EntityRef)</c> — the "no entity" sentinel (id 0).</summary>
    public bool IsNone => Id == 0;

    public bool Equals(EntityRef other) => Id == other.Id;

    public override bool Equals(object? obj) => obj is EntityRef other && Equals(other);

    public override int GetHashCode() => Id.GetHashCode();

    public static bool operator ==(EntityRef a, EntityRef b) => a.Equals(b);

    public static bool operator !=(EntityRef a, EntityRef b) => !a.Equals(b);
}
