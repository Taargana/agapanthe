namespace Agapanthe.Graphics;

/// <summary>
/// UI-3 — pure host-side math for turning two raw <c>VkQueryPool</c> timestamp ticks (as read back
/// by <c>vkGetQueryPoolResults</c>) into milliseconds, given the device's reported
/// <c>VkPhysicalDeviceLimits::timestampPeriod</c> (nanoseconds per tick). GPU-free and unit-tested
/// on its own, mirroring the project's <c>MathHelpers</c>/<c>ShadowFit</c> precedent of extracting
/// the pure math a Vulkan feature depends on.
/// </summary>
public static class GpuTimestampMath
{
    private const double NanosecondsPerMillisecond = 1_000_000.0;

    /// <summary>
    /// Converts the tick delta between <paramref name="beginTicks"/> and <paramref name="endTicks"/> to
    /// milliseconds, first masking both to <paramref name="validBits"/> low-order bits
    /// (<c>VkQueueFamilyProperties::timestampValidBits</c> — the Vulkan spec leaves any higher bit
    /// UNDEFINED; audit finding, csharp-lowlevel, 🟠: NVIDIA commonly reports 64 (a no-op mask), but
    /// Intel/AMD/MoltenVK commonly report 36-40, and reading raw unmasked ticks there mixes in
    /// undefined garbage — producing a wrong delta that a wraparound check alone cannot catch, since it
    /// is not reliably negative). Returns <see langword="false"/> (and <paramref name="milliseconds"/>
    /// is <c>0</c>) when the masked <paramref name="endTicks"/> is smaller than the masked
    /// <paramref name="beginTicks"/> (the counter wrapped — expected periodically at a narrow
    /// <paramref name="validBits"/>, e.g. every ~68 s at 36 bits/~1 ns) or when
    /// <paramref name="timestampPeriodNs"/> or the computed result is not finite — a caller must never
    /// receive a fabricated value that would read as a plausible (if suspiciously fast, or absurdly
    /// slow) frame instead of a garbage sample.
    /// </summary>
    public static bool TryToMilliseconds(
        ulong beginTicks, ulong endTicks, float timestampPeriodNs, out float milliseconds, uint validBits = 64)
    {
        milliseconds = 0f;

        if (!float.IsFinite(timestampPeriodNs) || timestampPeriodNs < 0f)
        {
            return false;
        }

        var mask = validBits >= 64 ? ulong.MaxValue : (1UL << (int)validBits) - 1UL;
        var maskedBegin = beginTicks & mask;
        var maskedEnd = endTicks & mask;

        if (maskedEnd < maskedBegin)
        {
            return false;
        }

        var deltaTicks = maskedEnd - maskedBegin;
        var ms = (float)(deltaTicks * (double)timestampPeriodNs / NanosecondsPerMillisecond);
        if (!float.IsFinite(ms))
        {
            return false;
        }

        milliseconds = ms;
        return true;
    }
}
