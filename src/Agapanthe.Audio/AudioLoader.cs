namespace Agapanthe.Audio;

/// <summary>
/// Loads a `.wav` — from a filesystem path or directly from an in-memory stream — and uploads it to a device
/// once (spec D5 — load-once, play-by-handle). No cook step (spec D4), mirroring the existing
/// <c>HdrImageLoader.Load(path)</c> precedent for decode-time-trivial content.
/// </summary>
public static class AudioLoader
{
    /// <summary>Decodes <paramref name="path"/> and uploads it to <paramref name="device"/>. Always safe to
    /// call: returns <see cref="AudioClip"/>'s default (no buffer) value, without touching the filesystem or
    /// OpenAL, if <paramref name="device"/> is unsupported.</summary>
    /// <exception cref="AudioException">The file is not a decodable PCM WAV, or the file's PCM shape has no
    /// OpenAL buffer format representation (spec: only mono/stereo 8/16-bit).</exception>
    public static AudioClip Load(string path, AudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(device);

        if (!device.Supported)
        {
            return default;
        }

        using var stream = File.OpenRead(path);
        return Load(stream, device);
    }

    /// <summary>
    /// Decodes <paramref name="stream"/> and uploads it to <paramref name="device"/> — the in-memory path
    /// (audit finding, engine-architect: the original demo synthesized a tone, wrote it to a real temp file,
    /// then read it back purely to satisfy a path-only API; procedural content has no file to name). Same
    /// safety/exception contract as <see cref="Load(string, AudioDevice)"/>.
    /// </summary>
    public static AudioClip Load(Stream stream, AudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(device);

        if (!device.Supported)
        {
            return default;
        }

        var data = WavFormat.Read(stream);
        return device.UploadClip(in data);
    }
}
