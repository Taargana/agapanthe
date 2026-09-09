using Agapanthe.Assets.Model;
using StbImageSharp;

namespace Agapanthe.Assets;

/// <summary>
/// Decodes still images into <see cref="ImageAsset"/> (tightly packed RGBA8). Backed by
/// StbImageSharp, so it accepts everything <c>stb_image</c> handles — PNG, JPEG, BMP, TGA,
/// PSD, GIF, HDR (tonemapped to 8-bit) — and always forces 4 channels regardless of the source
/// layout. GPU-compressed containers (KTX2, Basis Universal) are out of scope (phase 2).
/// <para>
/// This is a CPU-only asset step (spec §5): it produces DTOs with zero Graphics/Vulkan
/// dependency, so it is unit-testable without a device. Whether the pixels are treated as sRGB
/// or linear is <b>not</b> read from the file — it is passed in by the caller, because the same
/// bytes are sRGB when used as base color/emissive and linear when used as normal/MR/AO
/// (decided by the material slot in M4-06, spec §3.6).
/// </para>
/// </summary>
public static class ImageLoader
{
    /// <summary>
    /// Loads and decodes an image file to RGBA8.
    /// </summary>
    /// <param name="path">Filesystem path to the image.</param>
    /// <param name="isSrgb">
    /// <c>true</c> to flag the result as sRGB-encoded (base color / emissive), <c>false</c> for
    /// linear data (normal / metallic-roughness / occlusion). The bytes are not altered — only the
    /// <see cref="ImageAsset.IsSrgb"/> flag is set, so Rendering picks the matching Vulkan format.
    /// </param>
    /// <returns>A decoded <see cref="ImageAsset"/> with 4 channels per pixel.</returns>
    /// <exception cref="AssetException">
    /// The file is missing, cannot be read, is not a decodable image, or decodes to zero dimensions.
    /// </exception>
    public static ImageAsset Load(string path, bool isSrgb)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            throw new AssetException(path, "image file not found");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AssetException(path, $"could not read image file ({ex.Message})", ex);
        }

        return Decode(bytes, isSrgb, path);
    }

    /// <summary>
    /// Decodes an in-memory image to RGBA8. Used for glTF images that live inside a GLB
    /// <c>bufferView</c> or a <c>data:</c> URI, where there is no file on disk.
    /// </summary>
    /// <param name="bytes">The raw, encoded image bytes (PNG/JPEG/…).</param>
    /// <param name="isSrgb">See <see cref="Load(string, bool)"/>.</param>
    /// <param name="sourceName">
    /// A human-readable name for the byte source (e.g. <c>"model.glb#image[2]"</c> or the data-URI
    /// description), surfaced verbatim in <see cref="AssetException"/> messages.
    /// </param>
    /// <returns>A decoded <see cref="ImageAsset"/> with 4 channels per pixel.</returns>
    /// <exception cref="AssetException">
    /// The bytes are empty, not a decodable image, or decode to zero dimensions.
    /// </exception>
    public static ImageAsset LoadFromBytes(ReadOnlySpan<byte> bytes, bool isSrgb, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(sourceName);

        if (bytes.IsEmpty)
        {
            throw new AssetException(sourceName, "image data is empty");
        }

        // StbImageSharp only accepts a byte[]; the span may be a slice of a larger buffer
        // (e.g. a GLB chunk), so copy out the exact image bytes. Asset loading is not a hot path.
        return Decode(bytes.ToArray(), isSrgb, sourceName);
    }

    private static ImageAsset Decode(byte[] bytes, bool isSrgb, string sourceName)
    {
        ImageResult image;
        try
        {
            // Force 4 channels: the source may be grey/RGB/paletted, but the GPU path wants RGBA8.
            image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex)
        {
            // stb_image reports failures as a plain Exception carrying its failure reason string.
            throw new AssetException(sourceName, $"failed to decode image ({ex.Message})", ex);
        }

        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new AssetException(
                sourceName, $"decoded image has invalid dimensions ({image.Width}x{image.Height})");
        }

        return new ImageAsset
        {
            Rgba8Pixels = image.Data,
            Width = image.Width,
            Height = image.Height,
            IsSrgb = isSrgb,
        };
    }
}
