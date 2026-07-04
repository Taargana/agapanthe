using System.Numerics;
using System.Runtime.InteropServices;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// The single, authoritative definition of descriptor <b>set 1</b> — the per-material PBR set.
/// Frozen in its final metallic-roughness shape from M4 onward (architect decision session 3, #4):
/// the six bindings never change again, so the shader interface, the pipeline layout and every
/// <see cref="Material"/> agree by construction. M4 only fills base color + factors and points the
/// other maps at 1×1 placeholders (M4-09); M5 lighting consumes the rest without a layout change.
/// <list type="table">
///   <item><term>0</term><description>baseColor — combined image sampler (sRGB), fragment</description></item>
///   <item><term>1</term><description>normal — combined image sampler (linear), fragment</description></item>
///   <item><term>2</term><description>metallicRoughness — combined image sampler (linear, B=metal / G=rough), fragment</description></item>
///   <item><term>3</term><description>occlusion — combined image sampler (linear, R=AO), fragment</description></item>
///   <item><term>4</term><description>emissive — combined image sampler (sRGB), fragment</description></item>
///   <item><term>5</term><description>factors — uniform buffer (<see cref="MaterialUniforms"/>), fragment</description></item>
/// </list>
/// </summary>
public static class MaterialLayout
{
    /// <summary>Binding 0 — base color texture (sRGB). Multiplied by <see cref="MaterialUniforms.BaseColorFactor"/>.</summary>
    public const uint BaseColorBinding = 0;

    /// <summary>Binding 1 — tangent-space normal map (linear).</summary>
    public const uint NormalBinding = 1;

    /// <summary>Binding 2 — metallic (B) / roughness (G) map (linear).</summary>
    public const uint MetallicRoughnessBinding = 2;

    /// <summary>Binding 3 — occlusion (R) map (linear).</summary>
    public const uint OcclusionBinding = 3;

    /// <summary>Binding 4 — emissive texture (sRGB).</summary>
    public const uint EmissiveBinding = 4;

    /// <summary>Binding 5 — material factor uniform buffer (<see cref="MaterialUniforms"/>).</summary>
    public const uint UniformsBinding = 5;

    /// <summary>Number of combined-image-sampler bindings (0..4). The UBO is the sixth binding.</summary>
    public const int TextureCount = 5;

    // The five textures are read in the fragment shader; the factor UBO likewise (base color tint,
    // alpha cutoff, and — from M5 — metallic/roughness/emissive scaling all apply in the fragment).
    private static readonly DescriptorBinding[] _bindings =
    [
        new DescriptorBinding(BaseColorBinding, DescriptorKind.CombinedImageSampler, ShaderStages.Fragment),
        new DescriptorBinding(NormalBinding, DescriptorKind.CombinedImageSampler, ShaderStages.Fragment),
        new DescriptorBinding(MetallicRoughnessBinding, DescriptorKind.CombinedImageSampler, ShaderStages.Fragment),
        new DescriptorBinding(OcclusionBinding, DescriptorKind.CombinedImageSampler, ShaderStages.Fragment),
        new DescriptorBinding(EmissiveBinding, DescriptorKind.CombinedImageSampler, ShaderStages.Fragment),
        new DescriptorBinding(UniformsBinding, DescriptorKind.UniformBuffer, ShaderStages.Fragment),
    ];

    /// <summary>The six bindings of set 1, in binding order. Shared, read-only definition.</summary>
    public static ReadOnlySpan<DescriptorBinding> Bindings => _bindings;

    /// <summary>
    /// Creates the set-1 <see cref="DescriptorSetLayout"/> for <paramref name="device"/>. One instance is
    /// enough for the whole app (the Renderer, M4-10, owns it and every material set is allocated against
    /// it); the caller disposes it at shutdown after <c>WaitIdle</c>.
    /// </summary>
    public static DescriptorSetLayout CreateLayout(GraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new DescriptorSetLayout(device, _bindings);
    }
}

/// <summary>
/// The per-material factor block backing set 1, binding 5. Sixteen floats / 64 bytes, laid out to match
/// std140: four <see cref="Vector4"/> members, each 16-byte aligned, contiguous at offsets 0/16/32/48. The
/// GPU-side <c>layout(std140) uniform</c> in the M4/M5 fragment shader must declare the members in this
/// exact order.
/// <para>
/// <b>Packing</b> (why values are grouped four-to-a-vec4 rather than one scalar per line — std140 would
/// otherwise pad every scalar to its own 16-byte slot):
/// </para>
/// <list type="bullet">
///   <item><see cref="BaseColorFactor"/> — linear RGBA tint, multiplies the base color texture (glTF
///   <c>baseColorFactor</c>).</item>
///   <item><see cref="MetallicRoughnessNormalOcclusion"/> — <c>x</c> metallic, <c>y</c> roughness,
///   <c>z</c> normal scale, <c>w</c> occlusion strength. glTF has no normalScale/occlusionStrength in our
///   DTO subset, so both default to <c>1</c>; the field exists now so M5 can honour them without a layout
///   change.</item>
///   <item><see cref="EmissiveFactorStrength"/> — <c>xyz</c> emissive color, <c>w</c>
///   <c>KHR_materials_emissive_strength</c> multiplier (the shader uses <c>xyz · w</c>).</item>
///   <item><see cref="AlphaCutoffFlags"/> — <c>x</c> alpha cutoff, <c>y</c> alpha mode as a float
///   (<c>0</c> = Opaque, <c>1</c> = Mask; matches <see cref="Assets.Model.AlphaMode"/>'s ordinal),
///   <c>z</c>/<c>w</c> reserved (0).</item>
/// </list>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct MaterialUniforms
{
    /// <summary>Linear RGBA base-color multiplier (offset 0).</summary>
    public readonly Vector4 BaseColorFactor;

    /// <summary>x = metallic, y = roughness, z = normal scale, w = occlusion strength (offset 16).</summary>
    public readonly Vector4 MetallicRoughnessNormalOcclusion;

    /// <summary>xyz = emissive color, w = emissive strength (offset 32).</summary>
    public readonly Vector4 EmissiveFactorStrength;

    /// <summary>x = alpha cutoff, y = alpha mode as float (0 Opaque / 1 Mask), z/w reserved (offset 48).</summary>
    public readonly Vector4 AlphaCutoffFlags;

    public MaterialUniforms(
        Vector4 baseColorFactor,
        Vector4 metallicRoughnessNormalOcclusion,
        Vector4 emissiveFactorStrength,
        Vector4 alphaCutoffFlags)
    {
        BaseColorFactor = baseColorFactor;
        MetallicRoughnessNormalOcclusion = metallicRoughnessNormalOcclusion;
        EmissiveFactorStrength = emissiveFactorStrength;
        AlphaCutoffFlags = alphaCutoffFlags;
    }
}
