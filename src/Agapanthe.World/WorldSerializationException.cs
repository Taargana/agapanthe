namespace Agapanthe.World;

/// <summary>
/// Thrown by <see cref="GameWorld.Load"/> when a world snapshot cannot be read (VS-1): a bad magic/version/component
/// count, a truncated stream, an out-of-range presence mask, or a load into a non-empty world. A malformed snapshot
/// is a data error, not a recoverable runtime condition — but it is surfaced as a typed exception rather than a raw
/// <see cref="EndOfStreamException"/> or an out-of-bounds read, so a caller can distinguish "bad save file" from a bug.
/// </summary>
public sealed class WorldSerializationException : Exception
{
    public WorldSerializationException(string message)
        : base(message)
    {
    }

    public WorldSerializationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
