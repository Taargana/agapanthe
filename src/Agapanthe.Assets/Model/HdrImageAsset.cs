namespace Agapanthe.Assets.Model;

/// <summary>
/// A decoded high-dynamic-range image: tightly packed RGBA float32 pixels, ready to stage into a
/// floating-point GPU texture. The loader (M7-03) forces 4 channels via StbImageSharp
/// (<c>ImageResultFloat</c>), so <see cref="RgbaPixels"/> length is always <c>Width · Height · 4</c>.
/// <para>
/// Radiance HDR (<c>.hdr</c>, RGBE) stores <b>linear</b> radiance, so these values are linear — no sRGB
/// decode is applied and none is implied. Components routinely exceed 1.0 (the whole point of HDR: bright
/// skies, light sources), so this must be uploaded to a float format such as
/// <c>PixelFormat.R32G32B32A32Sfloat</c>, never clamped into an 8-bit UNORM.
/// </para>
/// <para>
/// Intended use (spec §3.6, board M7): an equirectangular HDR environment map staged into a sampled float
/// image, then consumed by the IBL compute generator (equirect → cubemap → irradiance → prefiltered
/// specular). The forced 4th (alpha) channel is padding (StbImageSharp fills it with 1.0); it exists so the
/// data matches a 4-component GPU format with no per-row repacking.
/// </para>
/// </summary>
public sealed record HdrImageAsset
{
    /// <summary>
    /// Row-major, top-left origin, tightly packed RGBA float32 pixels
    /// (length = <see cref="Width"/> · <see cref="Height"/> · 4). Linear radiance; components may exceed 1.0.
    /// </summary>
    public required float[] RgbaPixels { get; init; }

    /// <summary>Image width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Image height in pixels.</summary>
    public required int Height { get; init; }
}
