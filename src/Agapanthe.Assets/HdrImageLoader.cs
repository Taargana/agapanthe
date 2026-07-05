using Agapanthe.Assets.Model;
using StbImageSharp;

namespace Agapanthe.Assets;

/// <summary>
/// Decodes high-dynamic-range images into <see cref="HdrImageAsset"/> (tightly packed RGBA float32).
/// Backed by StbImageSharp's <see cref="ImageResultFloat"/>, so it accepts Radiance HDR (<c>.hdr</c>,
/// RGBE) — the equirectangular environment maps used for IBL (spec §3.6, board M7) — and always forces
/// 4 channels (the 4th is padding, filled with 1.0) so the result matches a 4-component float GPU format.
/// <para>
/// Unlike <see cref="ImageLoader"/>, there is <b>no</b> <c>isSrgb</c> parameter: Radiance HDR stores linear
/// radiance, so the pixels are unconditionally linear and are handed to the GPU as-is. This is a CPU-only
/// asset step with zero Graphics/Vulkan dependency, so it is unit-testable without a device.
/// </para>
/// </summary>
public static class HdrImageLoader
{
    /// <summary>
    /// Loads and decodes an HDR image file to RGBA float32.
    /// </summary>
    /// <param name="path">Filesystem path to the <c>.hdr</c> image.</param>
    /// <returns>A decoded <see cref="HdrImageAsset"/> with 4 float channels per pixel.</returns>
    /// <exception cref="AssetException">
    /// The file is missing, cannot be read, is not a decodable HDR image, or decodes to zero dimensions.
    /// </exception>
    public static HdrImageAsset Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            throw new AssetException(path, "HDR image file not found");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AssetException(path, $"could not read HDR image file ({ex.Message})", ex);
        }

        return Decode(bytes, path);
    }

    /// <summary>
    /// Decodes an in-memory HDR image to RGBA float32. Used when the encoded bytes do not live on disk.
    /// </summary>
    /// <param name="bytes">The raw, encoded HDR image bytes (Radiance RGBE).</param>
    /// <param name="sourceName">
    /// A human-readable name for the byte source, surfaced verbatim in <see cref="AssetException"/> messages.
    /// </param>
    /// <returns>A decoded <see cref="HdrImageAsset"/> with 4 float channels per pixel.</returns>
    /// <exception cref="AssetException">
    /// The bytes are empty, not a decodable HDR image, or decode to zero dimensions.
    /// </exception>
    public static HdrImageAsset LoadFromBytes(ReadOnlySpan<byte> bytes, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(sourceName);

        if (bytes.IsEmpty)
        {
            throw new AssetException(sourceName, "HDR image data is empty");
        }

        // StbImageSharp only accepts a byte[]; the span may be a slice of a larger buffer, so copy out the
        // exact image bytes. Asset loading is not a hot path.
        return Decode(bytes.ToArray(), sourceName);
    }

    private static HdrImageAsset Decode(byte[] bytes, string sourceName)
    {
        ImageResultFloat image;
        try
        {
            // Force 4 channels: the GPU IBL path wants RGBA float32 (the alpha is padding = 1.0).
            image = ImageResultFloat.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex)
        {
            // stb_image reports failures as a plain Exception carrying its failure reason string.
            throw new AssetException(sourceName, $"failed to decode HDR image ({ex.Message})", ex);
        }

        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new AssetException(
                sourceName, $"decoded HDR image has invalid dimensions ({image.Width}x{image.Height})");
        }

        // stb's HDR decoder pads truncated bodies silently; a pixel-count mismatch is the one
        // detectable symptom of a corrupt payload that sneaked past the signature check.
        if (image.Data is null || image.Data.Length != image.Width * image.Height * 4)
        {
            throw new AssetException(
                sourceName,
                $"failed to decode HDR image (pixel data length {image.Data?.Length ?? 0} does not match {image.Width}x{image.Height} RGBA)");
        }

        return new HdrImageAsset
        {
            RgbaPixels = image.Data,
            Width = image.Width,
            Height = image.Height,
        };
    }
}
