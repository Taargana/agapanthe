using System.Numerics;

namespace Agapanthe.Assets.Model;

/// <summary>
/// Alpha handling mode. Phase-1 subset: BLEND is out of scope (loader coerces it to
/// <see cref="Opaque"/> with a warning, spec §3.6), so only these two values ever appear.
/// </summary>
public enum AlphaMode
{
    /// <summary>Fully opaque; alpha channel ignored. glTF default.</summary>
    Opaque,

    /// <summary>Alpha-tested: fragments with alpha &lt; <see cref="MaterialAsset.AlphaCutoff"/> are discarded.</summary>
    Mask,
}

/// <summary>
/// A metallic-roughness PBR material as CPU data. Image slots are integer indices into
/// <see cref="ModelAsset.Images"/>, with <c>-1</c> meaning "no texture" (Rendering binds a 1×1
/// placeholder for that slot, M4-09). Factor defaults match the glTF 2.0 spec so a material with no
/// explicit values is well-formed.
/// </summary>
public sealed record MaterialAsset
{
    /// <summary>Linear base color multiplier (RGBA). glTF default <c>(1,1,1,1)</c>. Multiplies the base color texture.</summary>
    public Vector4 BaseColorFactor { get; init; } = Vector4.One;

    /// <summary>Metalness scalar in [0,1]. glTF default <c>1</c>.</summary>
    public float MetallicFactor { get; init; } = 1f;

    /// <summary>Roughness scalar in [0,1]. glTF default <c>1</c>.</summary>
    public float RoughnessFactor { get; init; } = 1f;

    /// <summary>
    /// Scales the sampled tangent-space normal's X/Y (glTF <c>normalTexture.scale</c>). Default <c>1</c>
    /// (no scaling); <c>&lt;1</c> flattens the surface detail. Applied by the M5 fragment shader.
    /// </summary>
    public float NormalScale { get; init; } = 1f;

    /// <summary>
    /// Interpolates ambient occlusion toward 1 (glTF <c>occlusionTexture.strength</c>): the shader uses
    /// <c>1 + strength · (ao − 1)</c>. Default <c>1</c> (full occlusion); <c>0</c> disables the AO map.
    /// </summary>
    public float OcclusionStrength { get; init; } = 1f;

    /// <summary>Linear emissive color. glTF default <c>(0,0,0)</c>.</summary>
    public Vector3 EmissiveFactor { get; init; } = Vector3.Zero;

    /// <summary>
    /// Multiplier on <see cref="EmissiveFactor"/> from <c>KHR_materials_emissive_strength</c>. Default
    /// <c>1</c> (extension absent). Values &gt; 1 push emission into HDR range for bloom/tone mapping.
    /// </summary>
    public float EmissiveStrength { get; init; } = 1f;

    /// <summary>Alpha handling. glTF default <see cref="AlphaMode.Opaque"/>.</summary>
    public AlphaMode AlphaMode { get; init; } = AlphaMode.Opaque;

    /// <summary>Alpha-test threshold, used only when <see cref="AlphaMode"/> is <see cref="AlphaMode.Mask"/>. glTF default <c>0.5</c>.</summary>
    public float AlphaCutoff { get; init; } = 0.5f;

    /// <summary>Base color texture, sampled as sRGB. Index into <see cref="ModelAsset.Images"/>, or <c>-1</c> if absent.</summary>
    public int BaseColorImage { get; init; } = -1;

    /// <summary>Tangent-space normal map, sampled as linear. Index into <see cref="ModelAsset.Images"/>, or <c>-1</c> if absent.</summary>
    public int NormalImage { get; init; } = -1;

    /// <summary>Metallic (B) / roughness (G) map, sampled as linear. Index into <see cref="ModelAsset.Images"/>, or <c>-1</c> if absent.</summary>
    public int MetallicRoughnessImage { get; init; } = -1;

    /// <summary>Ambient occlusion (R) map, sampled as linear. Index into <see cref="ModelAsset.Images"/>, or <c>-1</c> if absent.</summary>
    public int OcclusionImage { get; init; } = -1;

    /// <summary>Emissive texture, sampled as sRGB. Index into <see cref="ModelAsset.Images"/>, or <c>-1</c> if absent.</summary>
    public int EmissiveImage { get; init; } = -1;

    /// <summary>Sampler settings shared by this material's textures (glTF wrap/filter). Defaults to <see cref="TextureSettings.Default"/>.</summary>
    public TextureSettings TextureSettings { get; init; } = TextureSettings.Default;

    /// <summary>Material name for diagnostics; not required to be unique.</summary>
    public string Name { get; init; } = string.Empty;
}
