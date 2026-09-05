using Agapanthe.Engine;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0c W1 — <see cref="FixedTimestepAccumulator"/> in isolation: it decouples simulation speed from frame rate by
/// accumulating wall-clock time and running a fixed-step <see cref="SimulationHost.Tick"/> as many whole steps as
/// have accumulated. These tests exercise the unit alone (spec §Waves W1); the accumulator ↔ host ↔ physics chain is
/// <see cref="AccumulatorEquivalenceTests"/> (W2).
/// <para>
/// Every profile is built from <see cref="FixedTimestepAccumulator.FixedDeltaSeconds"/>, never from <c>n / 60f</c>:
/// <c>3f / 60f</c> and <c>3f * (1f / 60f)</c> differ by 1 ULP in <see cref="float"/>, which is exactly the trap the
/// scored review caught (F1) — a "3 ticks" profile written that way yields 2 on its first call.
/// </para>
/// </summary>
[Collection("World")]
public sealed class FixedTimestepAccumulatorTests : IDisposable
{
    private const float Fixed = 1f / 60f;

    private readonly List<GameWorld> _worlds = [];

    private SimulationHost NewHost()
    {
        var world = new GameWorld();
        _worlds.Add(world);
        return SimulationHost.CreateDefault(world);
    }

    public void Dispose()
    {
        foreach (var world in _worlds)
        {
            world.Dispose();
        }
    }

    [Fact]
    public void Advance_RunsOneTickPerWholeFixedStep()
    {
        var acc = new FixedTimestepAccumulator(Fixed);
        var host = NewHost();

        Assert.Equal(5, acc.Advance(host, 5f * acc.FixedDeltaSeconds));
    }

    [Fact]
    public void Advance_BelowOneStep_RunsNothingAndCarriesTheRemainder()
    {
        var acc = new FixedTimestepAccumulator(Fixed);
        var host = NewHost();

        Assert.Equal(0, acc.Advance(host, 0.5f * acc.FixedDeltaSeconds));
        // The carried 0.5 step plus another 0.5 step is one whole step.
        Assert.Equal(1, acc.Advance(host, 0.5f * acc.FixedDeltaSeconds));
    }

    [Fact]
    public void Advance_ClampsALongFrame_SoCatchUpIsBounded()
    {
        var acc = new FixedTimestepAccumulator(Fixed, maxWallClockDeltaSeconds: 0.25f);
        var host = NewHost();

        // 10 s would be ~600 ticks unclamped; the 250 ms ceiling caps it at ~15 (the spiral-of-death guard).
        var first = acc.Advance(host, 10f);
        Assert.InRange(first, 1, 15);

        // And the excess is dropped, not banked: a second long frame is clamped the same way, never a burst.
        var second = acc.Advance(host, 10f);
        Assert.InRange(second, 1, 15);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void Constructor_RejectsANonPositiveFixedStep(float fixedDeltaSeconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new FixedTimestepAccumulator(fixedDeltaSeconds));

    [Fact]
    public void Constructor_RejectsAMaxBelowTheFixedStep()
    {
        // A ceiling below one step means the accumulator could never reach a whole step — fail loudly, don't stall.
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedTimestepAccumulator(Fixed, maxWallClockDeltaSeconds: Fixed / 2f));
    }

    [Fact]
    public void Constructor_AcceptsAMaxEqualToTheFixedStep()
    {
        var acc = new FixedTimestepAccumulator(Fixed, maxWallClockDeltaSeconds: Fixed);
        Assert.Equal(Fixed, acc.MaxWallClockDeltaSeconds);
    }

    [Fact]
    public void Constructor_RejectsAnExtremeRatio_ThatWouldStallOrHangTheCatchUpLoop()
    {
        // fixed = 1 ns under a 250 ms ceiling = 2.5e8 ticks per Advance, and past ~2^24 the float subtraction
        // rounds to a no-op and the loop never terminates. Must throw, not hang (audit LL F-1).
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedTimestepAccumulator(1e-9f, maxWallClockDeltaSeconds: 0.25f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedTimestepAccumulator(1e-6f, maxWallClockDeltaSeconds: 1f));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-5f)]
    public void Advance_WithAPathologicalDelta_RunsNothingAndDoesNotCorruptTheAccumulator(float delta)
    {
        var acc = new FixedTimestepAccumulator(Fixed);
        var host = NewHost();

        // A non-finite or negative delta loses the frame — zero ticks, never a clamp-to-Max burst of 15 (LL F-3).
        Assert.Equal(0, acc.Advance(host, delta));
        Assert.Equal(1, acc.SanitisedInputCount);

        // A normal frame right after still behaves: one whole step in, one tick out.
        Assert.Equal(1, acc.Advance(host, acc.FixedDeltaSeconds));
        Assert.Equal(1, acc.SanitisedInputCount); // unchanged by the healthy call
    }

    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(float.NegativeInfinity, 0f)]
    [InlineData(-3f, 0f)]
    [InlineData(0.05f, 0.05f)]
    [InlineData(10f, 0.25f)] // clamped to the max
    public void Sanitise_MapsRawDeltasToTheValueTheAccumulatorMayAdd(float raw, float expected)
        => Assert.Equal(expected, FixedTimestepAccumulator.Sanitise(raw, maxWallClockDeltaSeconds: 0.25f));

    [Fact]
    public void Advance_IsAllocationFree_AfterWarmup()
    {
        var acc = new FixedTimestepAccumulator(Fixed);
        var host = NewHost();

        // Exercise every branch of Advance, not just the steady one-tick path (audit LL F-5): whole step, sub-step,
        // catch-up, non-finite and negative.
        float[] pattern =
        [
            acc.FixedDeltaSeconds, 0.4f * acc.FixedDeltaSeconds, 3f * acc.FixedDeltaSeconds, float.NaN, -1f,
        ];

        for (var i = 0; i < 200; i++)
        {
            acc.Advance(host, pattern[i % pattern.Length]);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++)
        {
            acc.Advance(host, pattern[i % pattern.Length]);
        }
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, delta);
    }
}
