using Agapanthe.Engine;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free tests for the profiler's data layer (UI-2). The circular history and its aggregates are pure logic, and
/// they must be allocation-free: this is the code that MEASURES per-frame allocation, so anything it allocates
/// corrupts the very number it reports.
/// </summary>
public class FrameStatsTests
{
    // ---- FrameSeries: circular history --------------------------------------------------------------------------

    [Fact]
    public void Series_StartsEmpty()
    {
        var series = new FrameSeries(4);

        Assert.Equal(0, series.Count);
        Assert.Equal(0f, series.Last);
        Assert.Equal(0f, series.Average);
        Assert.Equal(0f, series.Max);
    }

    [Fact]
    public void Series_FillsUpToCapacity()
    {
        var series = new FrameSeries(4);

        series.Record(1f);
        series.Record(2f);

        Assert.Equal(2, series.Count);
        Assert.Equal(2f, series.Last);
        Assert.Equal(1.5f, series.Average, 4);
        Assert.Equal(2f, series.Max);
    }

    [Fact]
    public void Series_OverwritesOldestOnceFull()
    {
        var series = new FrameSeries(3);
        series.Record(1f);
        series.Record(2f);
        series.Record(3f);

        series.Record(4f); // evicts the 1

        Assert.Equal(3, series.Count); // never grows past capacity
        Assert.Equal(4f, series.Last);
        Assert.Equal(3f, series.Average, 4); // (2+3+4)/3
    }

    [Fact]
    public void Series_MaxTracksSpikesNotAverages()
    {
        // A stutter is exactly what a frame-time graph exists to reveal; the scale must follow the spike.
        var series = new FrameSeries(4);
        series.Record(16f);
        series.Record(120f); // one bad frame
        series.Record(16f);

        Assert.Equal(120f, series.Max);
    }

    [Fact]
    public void Series_MaxForgetsAnEvictedSpike()
    {
        var series = new FrameSeries(2);
        series.Record(100f);
        series.Record(10f);

        series.Record(10f); // the spike falls out of the window

        Assert.Equal(10f, series.Max);
    }

    // ---- Chronological copy -------------------------------------------------------------------------------------

    [Fact]
    public void CopyChronological_ReturnsOldestFirst_BeforeWrapping()
    {
        var series = new FrameSeries(4);
        series.Record(1f);
        series.Record(2f);
        series.Record(3f);

        Span<float> buffer = stackalloc float[4];
        var copied = series.CopyChronological(buffer);

        Assert.Equal(3, copied.Length);
        Assert.Equal([1f, 2f, 3f], copied.ToArray());
    }

    [Fact]
    public void CopyChronological_ReturnsOldestFirst_AfterWrapping()
    {
        // The backing array is circular: handed out raw, the series would jump at the write cursor and the graph
        // would show a discontinuity that never happened.
        var series = new FrameSeries(3);
        series.Record(1f);
        series.Record(2f);
        series.Record(3f);
        series.Record(4f);
        series.Record(5f);

        Span<float> buffer = stackalloc float[3];
        var copied = series.CopyChronological(buffer);

        Assert.Equal([3f, 4f, 5f], copied.ToArray());
    }

    [Fact]
    public void CopyChronological_SmallDestinationKeepsTheMostRecentWindow()
    {
        // A graph narrower than the history must show the LATEST frames, not the oldest.
        var series = new FrameSeries(8);
        for (var i = 1; i <= 6; i++)
        {
            series.Record(i);
        }

        Span<float> buffer = stackalloc float[3];
        var copied = series.CopyChronological(buffer);

        Assert.Equal([4f, 5f, 6f], copied.ToArray());
    }

    [Fact]
    public void CopyChronological_EmptySeriesReturnsEmpty()
    {
        Span<float> buffer = stackalloc float[4];
        Assert.True(new FrameSeries(4).CopyChronological(buffer).IsEmpty);
    }

    [Fact]
    public void Clear_EmptiesTheSeries()
    {
        var series = new FrameSeries(4);
        series.Record(5f);

        series.Clear();

        Assert.Equal(0, series.Count);
        Assert.Equal(0f, series.Last);
    }

    // ---- FrameStats ---------------------------------------------------------------------------------------------

    [Fact]
    public void Stats_FirstRecordOnlyPrimesTheBaseline()
    {
        // Everything allocated during startup would otherwise be attributed to frame 1 and dwarf the graph's scale
        // for its entire retention window.
        var stats = new FrameStats(capacity: 8);

        stats.Record(16f, 0);

        Assert.Equal(0, stats.FrameCount);
        Assert.Equal(0, stats.FrameTimeMs.Count);
        Assert.Equal(0, stats.AllocatedBytes.Count);
    }

    [Fact]
    public void Stats_RecordsFrameTimeInMilliseconds()
    {
        var stats = new FrameStats(capacity: 8);
        stats.Record(16f, 0); // prime

        stats.Record(16f, 0);

        Assert.Equal(1, stats.FrameCount);
        Assert.Equal(16f, stats.FrameTimeMs.Last, 3);
    }

    [Fact]
    public void Stats_AverageFpsDerivesFromMeanFrameTime()
    {
        var stats = new FrameStats(capacity: 8);
        stats.Record(20f, 0); // prime
        stats.Record(20f, 0);
        stats.Record(20f, 0);

        Assert.Equal(50f, stats.AverageFps, 2); // 20 ms → 50 fps
    }

    [Fact]
    public void Stats_AverageFpsIsZeroBeforeAnySample()
    {
        Assert.Equal(0f, new FrameStats(capacity: 8).AverageFps);
    }

    [Fact]
    public void Stats_RecordsTheAllocationItIsGiven()
    {
        // Record does not sample: the ORCHESTRATOR measures, bracketing tick + render, and hands the figure over.
        // That bracket is deliberate — sampling here would span one tick to the next and sweep in the host's
        // windowing pump, which allocates and which the engine does not control.
        var stats = new FrameStats(capacity: 8);
        stats.Record(16f, 0); // prime

        stats.Record(16f, 65536);

        Assert.Equal(65536f, stats.AllocatedBytes.Last);
        Assert.Equal(65536f, stats.AllocatedBytes.Max);
    }

    [Fact]
    public void Stats_RecordAllocatesNothingItself()
    {
        // The whole point: a profiler that allocates corrupts the metric it displays. An idle frame must read as
        // exactly zero bytes, not "a few hundred bytes of profiler overhead".
        var stats = new FrameStats(capacity: 64);
        stats.Record(16f, 0); // prime

        AllocationProbe.AssertNoAllocation("FrameStats.Record", () => stats.Record(16f, 0));
        Assert.Equal(0f, stats.AllocatedBytes.Last); // and it must REPORT zero for those idle frames
    }

    [Fact]
    public void Stats_AggregatesAndCopyAllocateNothing()
    {
        var stats = new FrameStats(capacity: 64);
        for (var i = 0; i < 70; i++)
        {
            stats.Record(16f, 0); // fill past capacity so the copy exercises the wrapped path
        }

        var scratch = new float[64];
        AllocationProbe.AssertNoAllocation("reading stats", () =>
        {
            _ = stats.FrameTimeMs.Average;
            _ = stats.FrameTimeMs.Max;
            _ = stats.AverageFps;
            _ = stats.FrameTimeMs.CopyChronological(scratch);
        });
    }
}
