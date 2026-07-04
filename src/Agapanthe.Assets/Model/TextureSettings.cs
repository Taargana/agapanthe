namespace Agapanthe.Assets.Model;

/// <summary>Texture coordinate wrap mode. Assets-owned enum, decoupled from any Graphics sampler enum.</summary>
public enum TextureWrap
{
    /// <summary>Tile the texture (glTF <c>10497</c>). glTF default.</summary>
    Repeat,

    /// <summary>Clamp coordinates to the edge texel (glTF <c>33071</c>).</summary>
    ClampToEdge,

    /// <summary>Mirror on each repeat (glTF <c>33648</c>).</summary>
    MirroredRepeat,
}

/// <summary>
/// Texture minification/magnification filter. Assets-owned enum: mipmap-aware glTF filter modes
/// collapse to <see cref="Linear"/>/<see cref="Nearest"/> because mip selection is a GPU-side concern
/// resolved by Rendering, not part of the CPU DTO.
/// </summary>
public enum TextureFilter
{
    /// <summary>Linear (bilinear/trilinear) sampling. Sensible default for color textures.</summary>
    Linear,

    /// <summary>Nearest-neighbour sampling (unfiltered).</summary>
    Nearest,
}

/// <summary>
/// Sampler configuration extracted from a glTF sampler, expressed with Assets-owned enums so the DTO
/// stays free of Graphics types. Rendering maps this to its <c>SamplerDesc</c> and deduplicates via a
/// SamplerCache (M4-09). Defaults follow glTF: <see cref="TextureWrap.Repeat"/> and
/// <see cref="TextureFilter.Linear"/>.
/// </summary>
public readonly record struct TextureSettings(
    TextureWrap WrapU = TextureWrap.Repeat,
    TextureWrap WrapV = TextureWrap.Repeat,
    TextureFilter MinFilter = TextureFilter.Linear,
    TextureFilter MagFilter = TextureFilter.Linear)
{
    /// <summary>glTF default sampler: repeat wrap, linear filtering.</summary>
    public static TextureSettings Default => new();
}
