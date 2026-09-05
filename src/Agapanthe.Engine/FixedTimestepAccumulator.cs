namespace Agapanthe.Engine;

/// <summary>
/// Decouples simulation speed from frame rate (MP-0c): it accumulates wall-clock time and runs a fixed-step
/// <see cref="SimulationHost.Tick"/> as many whole steps as have accumulated, carrying the remainder to the next
/// call. The renderer then shows the last completed tick — this type delivers no interpolation between ticks
/// (spec decision 1).
/// <para>
/// <b>It lives here, not in <c>FrameOrchestrator</c> and not inside <see cref="SimulationHost"/></b> (spec decision
/// 7): a dedicated headless real-time server has a host but no orchestrator, and <see cref="SimulationHost"/> was
/// kept deliberately minimal by MP-0a. This drives the host by composition and is reusable as-is — via
/// <see cref="AdvanceFrame"/> for a real-time host loop, or <see cref="Advance"/> alone when the caller owns the
/// frame bracket.
/// </para>
/// <para>
/// <b>Single-threaded, zero-alloc.</b> <see cref="Advance"/> calls <see cref="SimulationHost.Tick"/> directly in a
/// loop — no delegate, no closure — and holds one <see cref="float"/> of state.
/// </para>
/// </summary>
public sealed class FixedTimestepAccumulator
{
    // A ratio ceiling of Max/Fixed above this stalls the catch-up loop for a visible fraction of a second, and past
    // ~2^24 the `_accumulated -= FixedDeltaSeconds` subtraction rounds to a no-op in float and the loop never
    // terminates. 1024 ticks is ~17 s of lag at 60 Hz — far beyond any legitimate hitch (Max defaults to 250 ms /
    // ~15 ticks). Enforced in the constructor so a bad config fails loudly instead of hanging (audit LL F-1).
    private const int MaxCatchUpTicksPerAdvance = 1024;

    private float _accumulated;

    /// <param name="fixedDeltaSeconds">The simulation step, seconds. Must be &gt; 0.</param>
    /// <param name="maxWallClockDeltaSeconds">
    /// The input clamp (spec decision 5). A wall-clock delta larger than this is treated as this, and the excess is
    /// dropped — one pathological frame (a breakpoint, an OS pause, a blocking resize) makes the simulation slow
    /// down rather than burst-catch-up without bound. Must be &gt;= <paramref name="fixedDeltaSeconds"/> and within
    /// <see cref="MaxCatchUpTicksPerAdvance"/> steps of it.
    /// </param>
    public FixedTimestepAccumulator(float fixedDeltaSeconds, float maxWallClockDeltaSeconds = 0.25f)
    {
        if (!(fixedDeltaSeconds > 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(fixedDeltaSeconds), fixedDeltaSeconds, "The fixed simulation step must be a positive number of seconds.");
        }

        if (!(maxWallClockDeltaSeconds >= fixedDeltaSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxWallClockDeltaSeconds), maxWallClockDeltaSeconds,
                $"The wall-clock clamp ({maxWallClockDeltaSeconds}s) must be at least one fixed step ({fixedDeltaSeconds}s), "
                + "or the accumulator could never reach a whole step.");
        }

        if (maxWallClockDeltaSeconds / fixedDeltaSeconds > MaxCatchUpTicksPerAdvance)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fixedDeltaSeconds), fixedDeltaSeconds,
                $"A {fixedDeltaSeconds}s step under a {maxWallClockDeltaSeconds}s ceiling would run up to "
                + $"{maxWallClockDeltaSeconds / fixedDeltaSeconds:F0} ticks in one Advance — the catch-up loop would "
                + $"stall the frame (and stops progressing entirely past ~2^24 steps, where the float subtraction "
                + $"rounds to a no-op). Cap the ratio at {MaxCatchUpTicksPerAdvance}.");
        }

        FixedDeltaSeconds = fixedDeltaSeconds;
        MaxWallClockDeltaSeconds = maxWallClockDeltaSeconds;
    }

    /// <summary>The fixed simulation step, seconds. Every <see cref="SimulationHost.Tick"/> this type issues uses
    /// exactly this value — so a single tick's determinism never depends on the wall-clock delta.</summary>
    public float FixedDeltaSeconds { get; }

    /// <summary>The input clamp, seconds (see the constructor).</summary>
    public float MaxWallClockDeltaSeconds { get; }

    /// <summary>
    /// How many <see cref="Advance"/> calls received a non-finite or negative wall-clock delta and had it sanitised
    /// to <c>0</c>. Stays <c>0</c> in normal operation; a non-zero value is an upstream clock bug that Release builds
    /// would otherwise swallow entirely — the assert in <see cref="Advance"/> only fires in Debug (audit LL F-2).
    /// </summary>
    public long SanitisedInputCount { get; private set; }

    /// <summary>
    /// Clamps <paramref name="wallClockDeltaSeconds"/> to <see cref="MaxWallClockDeltaSeconds"/> (see
    /// <see cref="Sanitise"/>), adds it to the internal accumulator, then calls
    /// <c>host.Tick(<see cref="FixedDeltaSeconds"/>)</c> once per whole step that fits. Returns the number of ticks
    /// run this call (0 when the frame was faster than one step). Call it once per frame; the frame's
    /// <see cref="SimulationHost.BeginFrame"/> / <see cref="SimulationHost.EndFrame"/> bracket is the caller's
    /// (or use <see cref="AdvanceFrame"/>, which opens it).
    /// </summary>
    public int Advance(SimulationHost host, float wallClockDeltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(host);

        // A non-finite or negative delta is a caller bug. The signal is SanitisedInputCount, not a Debug.Assert: a
        // failed assert terminates the test host (spec R3 / audit LL F-2), which would make the corruption-safety
        // behaviour untestable AND make SanitisedInputCount itself untestable — and a counter that any build,
        // any test and any telemetry can read is a strictly better signal than a Debug-only dialog. Sanitise is
        // extracted so its NaN-first ordering is unit-tested directly.
        if (!(float.IsFinite(wallClockDeltaSeconds) && wallClockDeltaSeconds >= 0f))
        {
            SanitisedInputCount++;
        }

        _accumulated += Sanitise(wallClockDeltaSeconds, MaxWallClockDeltaSeconds);

        var ticks = 0;
        while (_accumulated >= FixedDeltaSeconds)
        {
            _accumulated -= FixedDeltaSeconds;
            host.Tick(FixedDeltaSeconds);
            ticks++;
        }

        return ticks;
    }

    /// <summary>
    /// Opens the frame's self-measurement (<see cref="SimulationHost.BeginFrame"/>) and then <see cref="Advance"/>s
    /// — the exact per-frame sequence a real-time host runs. <see cref="SimulationHost.EndFrame"/> stays with the
    /// caller: it closes after the render (if any) has been submitted, which this type knows nothing about. A
    /// dedicated headless server loop is <c>while (running) { acc.AdvanceFrame(host, clock.Delta); host.EndFrame(); }</c>.
    /// </summary>
    public int AdvanceFrame(SimulationHost host, float wallClockDeltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.BeginFrame();
        return Advance(host, wallClockDeltaSeconds);
    }

    /// <summary>
    /// Turns a raw wall-clock delta into the value the accumulator may add: non-finite or negative → <c>0</c> (lose
    /// the frame — an inert failure, never 15 invented ticks; audit LL F-3), otherwise clamped to
    /// <paramref name="maxWallClockDeltaSeconds"/>.
    /// <para>
    /// The order matters: <see cref="MathF.Min"/> propagates <c>NaN</c>, so the finiteness test must come first or a
    /// single <c>NaN</c> frame would poison <c>_accumulated</c> and freeze the simulation permanently and silently —
    /// the worst case this type exists to prevent (spec F6). Extracted (spec R3 shape) so a test can exercise it
    /// without the <see cref="Advance"/> assert killing the host.
    /// </para>
    /// </summary>
    internal static float Sanitise(float wallClockDeltaSeconds, float maxWallClockDeltaSeconds)
    {
        if (!float.IsFinite(wallClockDeltaSeconds) || wallClockDeltaSeconds < 0f)
        {
            return 0f;
        }

        return MathF.Min(wallClockDeltaSeconds, maxWallClockDeltaSeconds);
    }
}
