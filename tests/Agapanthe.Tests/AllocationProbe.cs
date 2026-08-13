namespace Agapanthe.Tests;

/// <summary>
/// Measures the managed allocation of a hot-path routine, deterministically.
/// <para>
/// <b>Why not simply bracket one loop.</b> <c>GC.GetAllocatedBytesForCurrentThread</c> is itself exact — it stays
/// correct across a collection, a GC on another thread adds nothing to this thread's counter, and tiered/OSR
/// recompilation allocates from native loader memory rather than the managed heap. What is genuinely
/// non-deterministic is the <b>lazy initialisation of shared state</b> touched for the first time inside the
/// measured loop: xUnit runs collections in parallel, so which test pays a given static initialisation depends on
/// scheduling. That produces exactly the observed symptom — one failure in hundreds of runs, never reproducible.
/// </para>
/// <para>
/// Measuring several rounds and keeping the <b>minimum</b> closes it without weakening the assertion: a one-off
/// initialisation can only pollute one round, by definition, while a genuine per-call allocation shows up in every
/// round. Zero still has to be zero.
/// </para>
/// </summary>
internal static class AllocationProbe
{
    /// <summary>
    /// Runs <paramref name="body"/> <paramref name="iterations"/> times per round, over <paramref name="rounds"/>
    /// rounds, and returns the smallest total allocation observed.
    /// </summary>
    public static long MeasureMinBytes(Action body, int rounds = 3, int iterations = 100)
    {
        ArgumentNullException.ThrowIfNull(body);

        // At least two warm-up calls, not one: a single call only covers one side of any first-pass branch.
        body();
        body();

        var best = long.MaxValue;
        for (var round = 0; round < rounds; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                body();
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated < best)
            {
                best = allocated;
            }
        }

        return best;
    }

    /// <summary>Asserts <paramref name="body"/> allocates nothing, with the round-minimum protocol above.</summary>
    public static void AssertNoAllocation(string what, Action body)
    {
        var allocated = MeasureMinBytes(body);
        Assert.True(allocated == 0, $"{what} should allocate nothing, observed {allocated} bytes (best of 3 rounds).");
    }
}
