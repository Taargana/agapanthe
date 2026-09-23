namespace Agapanthe.Audio;

/// <summary>
/// An opaque handle to a decoded, OpenAL-uploaded sound (mirrors <c>MeshHandle</c>'s own load-once/reference-by-
/// handle shape — spec D5). Lifetime is owned by the <see cref="AudioDevice"/> that created it, never the
/// caller. <see cref="Default"/> (buffer id 0) is a real OpenAL sentinel meaning "no buffer" — the value
/// <see cref="AudioLoader.Load"/> returns when the device is unsupported, and the value
/// <see cref="AudioDevice.Play"/> silently ignores.
/// </summary>
public readonly record struct AudioClip
{
    internal AudioClip(uint bufferId) => BufferId = bufferId;

    internal uint BufferId { get; }
}
