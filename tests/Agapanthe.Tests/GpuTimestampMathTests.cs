using Agapanthe.Graphics;

namespace Agapanthe.Tests;

/// <summary>
/// UI-3 — pure host-side math for turning two raw <c>VkQueryPool</c> timestamp ticks into
/// milliseconds (mirrors the <see cref="MathHelpers"/>/<c>ShadowFit</c> precedent of testing the
/// GPU-free math a Vulkan feature depends on, independent of any real device). TDD-first: written
/// before <see cref="GpuTimestampMath"/> exists.
/// </summary>
public sealed class GpuTimestampMathTests
{
    [Fact]
    public void Ordinary_ComputesMilliseconds()
    {
        // 5,000,000 ticks at 1 ns/tick = 5,000,000 ns = 5 ms.
        var ok = GpuTimestampMath.TryToMilliseconds(0, 5_000_000, timestampPeriodNs: 1f, out var ms);

        Assert.True(ok);
        Assert.Equal(5f, ms, 3);
    }

    [Fact]
    public void ZeroDuration_IsZero()
    {
        var ok = GpuTimestampMath.TryToMilliseconds(12345, 12345, timestampPeriodNs: 1f, out var ms);

        Assert.True(ok);
        Assert.Equal(0f, ms);
    }

    [Fact]
    public void Wraparound_EndBeforeBegin_Rejected()
    {
        // A hardware timer counter can wrap over long uptimes; a caller must not silently receive a
        // fabricated negative millisecond value that would read as a suspiciously fast frame.
        var ok = GpuTimestampMath.TryToMilliseconds(100, 50, timestampPeriodNs: 1f, out var ms);

        Assert.False(ok);
        Assert.Equal(0f, ms);
    }

    [Fact]
    public void NonUnitTimestampPeriod_Scales()
    {
        // A real device rarely reports exactly 1 ns/tick (e.g. many NVIDIA GPUs report ~1.024 ns/tick).
        var ok = GpuTimestampMath.TryToMilliseconds(0, 1_000_000, timestampPeriodNs: 2f, out var ms);

        Assert.True(ok);
        Assert.Equal(2f, ms, 3);
    }

    // Audit finding (csharp-lowlevel, 🟠): the Vulkan spec leaves any bit above timestampValidBits
    // UNDEFINED (Intel/AMD/MoltenVK commonly report 36-40, unlike NVIDIA's 64) — raw unmasked ticks mix
    // in garbage and produce a wrong delta the plain wraparound check cannot catch.

    [Fact]
    public void NarrowValidBits_MasksHighGarbageBits()
    {
        // 8 valid bits (0-255). A garbage high bit (bit 8, value 256) must not affect the delta.
        var ok = GpuTimestampMath.TryToMilliseconds(
            beginTicks: 0, endTicks: 256 + 10, timestampPeriodNs: 1f, out var ms, validBits: 8);

        Assert.True(ok);
        Assert.Equal(10f / 1_000_000f, ms, 6); // masked end = 10 ticks, not 266
    }

    [Fact]
    public void NarrowValidBits_WrapAtMaskBoundary_Rejected()
    {
        // 8 valid bits: begin=250 (masked 250), end=260 (masked 4) — the counter wrapped within the
        // masked range, not an artifact of the unmasked comparison.
        var ok = GpuTimestampMath.TryToMilliseconds(
            beginTicks: 250, endTicks: 260, timestampPeriodNs: 1f, out var ms, validBits: 8);

        Assert.False(ok);
        Assert.Equal(0f, ms);
    }

    [Fact]
    public void NonFiniteTimestampPeriod_Rejected()
    {
        var ok = GpuTimestampMath.TryToMilliseconds(0, 100, timestampPeriodNs: float.NaN, out var ms);

        Assert.False(ok);
        Assert.Equal(0f, ms);
    }
}
