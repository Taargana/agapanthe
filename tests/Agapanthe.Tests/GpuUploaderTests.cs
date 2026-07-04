using Agapanthe.Graphics;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free unit tests for the pure helpers behind the staging upload (M3-06) and mip generation (M3-07):
/// the deletion-payload offset/memory-type packing and the mip-size rule. The Vulkan submit/copy/blit path
/// itself needs a device and is exercised at runtime by the Sandbox in M3-09 (no GPU in CI).
/// </summary>
public class GpuUploaderTests
{
    // ---- Deletion-payload packing (M3-06): offset + memory-type index share one 64-bit slot ----

    [Theory]
    [InlineData(0UL, 0u)]
    [InlineData(0UL, 5u)]
    [InlineData(64UL, 3u)]
    [InlineData(64UL * 1024 * 1024 - 256, 7u)] // an offset near the end of a 64 MiB block
    [InlineData((1UL << 40) - 1, (1u << 24) - 1)] // both fields at their maximum
    public void PackOffsetAndType_RoundTrips(ulong offset, uint memoryTypeIndex)
    {
        var packed = GpuImage.PackOffsetAndType(offset, memoryTypeIndex);

        Assert.Equal(offset, GpuImage.UnpackOffset(packed));
        Assert.Equal(memoryTypeIndex, GpuImage.UnpackMemoryType(packed));
    }

    [Fact]
    public void PackOffsetAndType_DistinctFieldsDoNotOverlap()
    {
        // A large offset and a non-zero type must not bleed into each other.
        var packed = GpuImage.PackOffsetAndType((1UL << 40) - 1, 31);
        Assert.Equal((1UL << 40) - 1, GpuImage.UnpackOffset(packed));
        Assert.Equal(31u, GpuImage.UnpackMemoryType(packed));
    }

    [Fact]
    public void PackOffsetAndType_OffsetOverflow_Throws()
    {
        Assert.Throws<GraphicsException>(() => GpuImage.PackOffsetAndType(1UL << 40, 0));
    }

    [Fact]
    public void PackOffsetAndType_MemoryTypeOverflow_Throws()
    {
        Assert.Throws<GraphicsException>(() => GpuImage.PackOffsetAndType(0, 1u << 24));
    }

    // ---- Mip-size rule (M3-07): max(1, dim >> level) per axis ----

    [Theory]
    [InlineData(256u, 256u, 0u, 256u, 256u)]
    [InlineData(256u, 256u, 1u, 128u, 128u)]
    [InlineData(256u, 256u, 8u, 1u, 1u)]   // last valid level of a 256² chain
    [InlineData(256u, 256u, 12u, 1u, 1u)]  // beyond the chain still floors to 1
    [InlineData(8u, 4u, 1u, 4u, 2u)]       // non-square halves independently
    [InlineData(8u, 4u, 2u, 2u, 1u)]       // height already floored at 1
    [InlineData(8u, 4u, 3u, 1u, 1u)]
    public void MipSize_HalvesAndFloorsToOne(uint width, uint height, uint level, uint expectedW, uint expectedH)
    {
        var (w, h) = GpuImage.MipSize(width, height, level);
        Assert.Equal(expectedW, w);
        Assert.Equal(expectedH, h);
    }

    [Fact]
    public void MipSize_AtFullChainLastLevel_IsOneByOne()
    {
        // The full chain length and the mip-size rule must agree: the last level is always 1×1.
        var mips = GpuImage.FullMipChain(512, 128);
        var (w, h) = GpuImage.MipSize(512, 128, mips - 1);
        Assert.Equal(1u, w);
        Assert.Equal(1u, h);
    }
}
