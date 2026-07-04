using Agapanthe.Graphics;

namespace Agapanthe.Tests;

// SamplerDesc is the only non-GPU-touching surface of M3-08: the Sampler and DescriptorAllocator
// themselves need a live VkDevice (vkCreateSampler / vkCreateDescriptorPool) and the pool grow logic
// is not mockable without Vulkan, so those are covered by the Sandbox validation run, not unit tests.
public class SamplerDescTests
{
    [Fact]
    public void Default_IsLinearLinearRepeat_NoAnisotropy()
    {
        var desc = default(SamplerDesc);

        Assert.Equal(SamplerFilter.Linear, desc.Filter);
        Assert.Equal(SamplerFilter.Linear, desc.MipFilter);
        Assert.Equal(SamplerAddressMode.Repeat, desc.AddressMode);
        Assert.Equal(0f, desc.MaxAnisotropy);
        Assert.Equal(0f, desc.MipLodBias);
    }

    [Fact]
    public void ParameterlessNew_MatchesDefault()
    {
        // Because every field's zero value is the sensible default, `new SamplerDesc()` and
        // `default(SamplerDesc)` agree — callers never get a surprise Nearest/ClampToEdge sampler.
        Assert.Equal(default, new SamplerDesc());
    }

    [Fact]
    public void NamedArguments_OverrideOnlyTheGivenFields()
    {
        var desc = new SamplerDesc(
            Filter: SamplerFilter.Nearest,
            AddressMode: SamplerAddressMode.ClampToEdge,
            MaxAnisotropy: 8f);

        Assert.Equal(SamplerFilter.Nearest, desc.Filter);
        Assert.Equal(SamplerFilter.Linear, desc.MipFilter); // untouched default
        Assert.Equal(SamplerAddressMode.ClampToEdge, desc.AddressMode);
        Assert.Equal(8f, desc.MaxAnisotropy);
    }
}
