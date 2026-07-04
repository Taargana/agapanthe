namespace Agapanthe.Assets.Model;

/// <summary>
/// A decoded image: tightly packed RGBA8 pixels ready to stage into a GPU texture. The loader
/// (M4-08) forces 4 channels via StbImageSharp, so <see cref="Rgba8Pixels"/> length is always
/// <c>Width · Height · 4</c>.
/// <para>
/// <see cref="IsSrgb"/> is decided by the material slot that references the image, not by the file:
/// base color and emissive are sRGB; normal, metallic-roughness and occlusion are linear (spec §3.6).
/// Rendering picks the matching format (<c>R8G8B8A8_SRGB</c> vs <c>R8G8B8A8_UNORM</c>). The same
/// source file used in two roles is decoded once per role so the flag stays unambiguous.
/// </para>
/// </summary>
public sealed record ImageAsset
{
    /// <summary>Row-major, top-left origin, tightly packed RGBA8 pixels (length = <see cref="Width"/> · <see cref="Height"/> · 4).</summary>
    public required byte[] Rgba8Pixels { get; init; }

    /// <summary>Image width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Image height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>True if the pixels are sRGB-encoded (base color / emissive); false for linear data (normal / MR / AO).</summary>
    public required bool IsSrgb { get; init; }
}
