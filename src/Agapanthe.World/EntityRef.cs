using Arch.Core;

namespace Agapanthe.World;

/// <summary>
/// An opaque wrapper around an Arch <see cref="Entity"/>. It exists so callers outside this project can hold on
/// to an entity without ever naming an Arch type: Arch is referenced with <c>PrivateAssets="compile"</c>, so no
/// consumer (Rendering, Sandbox, tests) can even compile against <see cref="Entity"/> (audit W1 M3). The wrapped
/// value stays internal — only this project's own systems dereference it.
/// </summary>
/// <remarks>
/// Process-local, like the Arch entity it wraps: it must not be persisted. Cross-process identity is
/// <see cref="GlobalId"/>'s job (future serialization/streaming).
/// </remarks>
public readonly struct EntityRef : IEquatable<EntityRef>
{
    internal readonly Entity Value;

    internal EntityRef(Entity value) => Value = value;

    public bool Equals(EntityRef other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is EntityRef other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(EntityRef a, EntityRef b) => a.Equals(b);

    public static bool operator !=(EntityRef a, EntityRef b) => !a.Equals(b);
}
