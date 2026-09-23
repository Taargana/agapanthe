namespace Agapanthe.Audio;

/// <summary>Malformed WAV input, or a genuine OpenAL error surfaced by the AL error-checking discipline
/// (spec D8) — never a "no device present" condition, which <see cref="AudioDevice"/> handles separately
/// by degrading <see cref="AudioDevice.Supported"/> to <see langword="false"/> rather than throwing.</summary>
public sealed class AudioException : Exception
{
    public AudioException(string message) : base(message)
    {
    }

    public AudioException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
