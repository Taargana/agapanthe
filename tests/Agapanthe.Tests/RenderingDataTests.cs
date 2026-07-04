using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Agapanthe.Assets.Model;
using Agapanthe.Graphics;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free unit tests for the M4-09 Rendering data layer: the std140 material uniform layout, the
/// index-format narrowing rule, and the Assets→Graphics sampler mapping. Anything needing a live device
/// (Mesh/Material/Scene upload) is exercised by the Sandbox run, not here.
/// </summary>
public sealed class RenderingDataTests
{
    // --- MaterialUniforms std140 layout ---------------------------------------------------------

    [Fact]
    public void MaterialUniforms_Is64Bytes()
    {
        Assert.Equal(64, Unsafe.SizeOf<MaterialUniforms>());
        Assert.Equal(64, Marshal.SizeOf<MaterialUniforms>());
    }

    [Fact]
    public void MaterialUniforms_MembersAtStd140Offsets()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<MaterialUniforms>(nameof(MaterialUniforms.BaseColorFactor)));
        Assert.Equal(16, (int)Marshal.OffsetOf<MaterialUniforms>(nameof(MaterialUniforms.MetallicRoughnessNormalOcclusion)));
        Assert.Equal(32, (int)Marshal.OffsetOf<MaterialUniforms>(nameof(MaterialUniforms.EmissiveFactorStrength)));
        Assert.Equal(48, (int)Marshal.OffsetOf<MaterialUniforms>(nameof(MaterialUniforms.AlphaCutoffFlags)));
    }

    [Fact]
    public void MaterialUniforms_PackingRoundTrips()
    {
        var u = new MaterialUniforms(
            new Vector4(0.1f, 0.2f, 0.3f, 0.4f),
            new Vector4(1f, 2f, 3f, 4f),
            new Vector4(5f, 6f, 7f, 8f),
            new Vector4(0.5f, 1f, 0f, 0f));

        // Reinterpret as 16 floats and confirm the byte order the shader will read.
        Span<float> floats = stackalloc float[16];
        MemoryMarshal.Write(MemoryMarshal.AsBytes(floats), in u);

        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f], floats[..4].ToArray());
        Assert.Equal([1f, 2f, 3f, 4f], floats[4..8].ToArray());
        Assert.Equal([5f, 6f, 7f, 8f], floats[8..12].ToArray());
        Assert.Equal([0.5f, 1f, 0f, 0f], floats[12..16].ToArray());
    }

    // --- Index format narrowing -----------------------------------------------------------------

    [Fact]
    public void ChooseIndexFormat_AllSmall_PicksU16()
    {
        uint[] indices = [0, 1, 2, 65535];
        Assert.Equal(IndexFormat.UInt16, Mesh.ChooseIndexFormat(indices));
    }

    [Fact]
    public void ChooseIndexFormat_AnyAbove65535_PicksU32()
    {
        uint[] indices = [0, 1, 65536];
        Assert.Equal(IndexFormat.UInt32, Mesh.ChooseIndexFormat(indices));
    }

    [Fact]
    public void ChooseIndexFormat_Boundary65535_IsU16()
    {
        // 65535 (0xFFFF) fits in u16; only strictly greater forces u32.
        Assert.Equal(IndexFormat.UInt16, Mesh.ChooseIndexFormat([65535]));
        Assert.Equal(IndexFormat.UInt32, Mesh.ChooseIndexFormat([65536]));
    }

    [Fact]
    public void ChooseIndexFormat_Empty_DefaultsToU16()
        => Assert.Equal(IndexFormat.UInt16, Mesh.ChooseIndexFormat([]));

    // --- Sampler mapping ------------------------------------------------------------------------

    [Fact]
    public void ToSamplerDesc_DefaultSettings_LinearRepeat()
    {
        var desc = SamplerCache.ToSamplerDesc(TextureSettings.Default);
        Assert.Equal(SamplerFilter.Linear, desc.Filter);
        Assert.Equal(SamplerFilter.Linear, desc.MipFilter);
        Assert.Equal(SamplerAddressMode.Repeat, desc.AddressMode);
        Assert.Equal(0f, desc.MaxAnisotropy);
    }

    [Fact]
    public void ToSamplerDesc_MapsWrapU_AndDropsWrapV()
    {
        var settings = new TextureSettings(
            WrapU: TextureWrap.ClampToEdge,
            WrapV: TextureWrap.MirroredRepeat, // ignored: SamplerDesc has one address mode
            MinFilter: TextureFilter.Linear,
            MagFilter: TextureFilter.Linear);
        Assert.Equal(SamplerAddressMode.ClampToEdge, SamplerCache.ToSamplerDesc(settings).AddressMode);
    }

    [Fact]
    public void ToSamplerDesc_FilterFromMag_MipFilterFromMin()
    {
        var settings = new TextureSettings(
            MinFilter: TextureFilter.Nearest,
            MagFilter: TextureFilter.Linear);
        var desc = SamplerCache.ToSamplerDesc(settings);
        Assert.Equal(SamplerFilter.Linear, desc.Filter);     // from MagFilter
        Assert.Equal(SamplerFilter.Nearest, desc.MipFilter); // from MinFilter
    }

    [Fact]
    public void ToSamplerDesc_MirroredRepeatMapped()
        => Assert.Equal(
            SamplerAddressMode.MirroredRepeat,
            SamplerCache.ToSamplerDesc(new TextureSettings(WrapU: TextureWrap.MirroredRepeat)).AddressMode);

    // --- MaterialLayout definition --------------------------------------------------------------

    [Fact]
    public void MaterialLayout_HasSixBindingsInPbrOrder()
    {
        var bindings = MaterialLayout.Bindings;
        Assert.Equal(6, bindings.Length);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal((uint)i, bindings[i].Binding);
            Assert.Equal(DescriptorKind.CombinedImageSampler, bindings[i].Kind);
            Assert.Equal(ShaderStages.Fragment, bindings[i].Stages);
        }

        Assert.Equal(MaterialLayout.UniformsBinding, bindings[5].Binding);
        Assert.Equal(DescriptorKind.UniformBuffer, bindings[5].Kind);
        Assert.Equal(ShaderStages.Fragment, bindings[5].Stages);
    }
}
