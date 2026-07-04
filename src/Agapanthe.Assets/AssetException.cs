namespace Agapanthe.Assets;

/// <summary>
/// Thrown when an asset cannot be loaded because it is missing, corrupt, or uses a feature
/// outside the phase-1 glTF subset (spec §3.6, §4). The engine never falls back silently:
/// every failure surfaces the offending path and a human-readable reason.
/// </summary>
public class AssetException : Exception
{
    /// <summary>Path (file or <c>data:</c> URI description) of the asset that failed, if known.</summary>
    public string? AssetPath { get; }

    /// <summary>Creates an exception from a raw message (no path context).</summary>
    public AssetException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception wrapping an inner cause (no path context).</summary>
    public AssetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates an exception whose message combines <paramref name="assetPath"/> and
    /// <paramref name="reason"/>, the shape mandated by spec §4 (path + reason, no silent fallback).
    /// </summary>
    public AssetException(string assetPath, string reason)
        : base($"Asset '{assetPath}': {reason}")
    {
        AssetPath = assetPath;
    }

    /// <summary>As <see cref="AssetException(string, string)"/> but preserving the underlying cause.</summary>
    public AssetException(string assetPath, string reason, Exception innerException)
        : base($"Asset '{assetPath}': {reason}", innerException)
    {
        AssetPath = assetPath;
    }
}
