using Agapanthe.Graphics;
using Silk.NET.Vulkan;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free tests for the engine-facing <see cref="PixelFormat"/> mapping (UI-1). Adding a format means touching
/// THREE exhaustive switches — <c>ToVk</c> (throws on an unmapped value), <c>FromVk</c>, and
/// <c>GpuUploader.BytesPerTexel</c> (also throws) — and missing one fails only at runtime, on the GPU, in whichever
/// scene happens to use it. These tests turn that into a compile-and-test-time failure.
/// </summary>
public class PixelFormatTests
{
    // Every value the enum declares, so a newly added format that nobody mapped shows up here immediately.
    public static TheoryData<PixelFormat> AllFormats()
    {
        var data = new TheoryData<PixelFormat>();
        foreach (var format in Enum.GetValues<PixelFormat>())
        {
            data.Add(format);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void ToVk_MapsEveryDeclaredFormat(PixelFormat format)
    {
        // Undefined legitimately maps to Format.Undefined; everything else must map to a real Vulkan format.
        var vk = format.ToVk();

        if (format == PixelFormat.Undefined)
        {
            Assert.Equal(Format.Undefined, vk);
        }
        else
        {
            Assert.NotEqual(Format.Undefined, vk);
        }
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void FromVk_RoundTripsEveryDeclaredFormat(PixelFormat format)
    {
        Assert.Equal(format, PixelFormatExtensions.FromVk(format.ToVk()));
    }

    [Fact]
    public void R8Unorm_MapsToSingleChannelUnorm()
    {
        // The font atlas holds an SDF: coverage/distance DATA, never colour — so it must be a linear (unorm)
        // single-channel format. An sRGB mapping here would silently gamma-decode the distance field.
        Assert.Equal(Format.R8Unorm, PixelFormat.R8Unorm.ToVk());
    }

    [Fact]
    public void R8Unorm_IsOneBytePerTexel()
    {
        // BytesPerTexel throws on unlisted formats, so an unmapped atlas format would blow up inside the staging
        // upload rather than at the call site. One byte per texel is also what sizes the staging buffer.
        Assert.Equal(1UL, GpuUploader.BytesPerTexel(PixelFormat.R8Unorm));
    }
}
