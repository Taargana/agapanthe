using Agapanthe.Graphics;

namespace Agapanthe.Tests;

/// <summary>
/// Guards the <see cref="BufferCopyRegion"/> field semantics (P3-M7): its three ulong fields feed
/// <c>vkCmdCopyBuffer</c>'s <c>VkBufferCopy</c> as (srcOffset, dstOffset, size). Source and destination offsets
/// are trivially swappable — a swap would copy the wrong bytes with no validation error — so the positional
/// constructor order is pinned here.
/// </summary>
public sealed class BufferCopyRegionTests
{
    [Fact]
    public void PositionalConstructor_MapsSourceThenDestinationThenSize()
    {
        var region = new BufferCopyRegion(16, 96, 96);

        Assert.Equal(16ul, region.SourceOffset);
        Assert.Equal(96ul, region.DestinationOffset);
        Assert.Equal(96ul, region.Size);
    }

    [Fact]
    public void SlotRegion_IsContiguousStride()
    {
        // A dirty-slot replay copies one 96-byte SceneCandidate at slot*96 in both buffers (staging IS the mirror,
        // so source and destination offsets coincide — spec §3.1). This is the shape the persistent buffer emits.
        const ulong stride = 96;
        const int slot = 7;
        var region = new BufferCopyRegion(slot * stride, slot * stride, stride);

        Assert.Equal(region.SourceOffset, region.DestinationOffset);
        Assert.Equal(672ul, region.SourceOffset);
        Assert.Equal(stride, region.Size);
    }
}
