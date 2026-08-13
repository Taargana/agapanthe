namespace Agapanthe.Engine;

/// <summary>
/// A fixed-capacity circular history of one float series, plus the aggregates a graph needs (UI-2).
/// <para>
/// Deliberately not a <c>Queue&lt;float&gt;</c> or a <c>List&lt;float&gt;</c>: this is written once per frame and read
/// once per frame by the profiler that <b>measures per-frame allocation</b>. Anything that allocated here would show
/// up in the very number it reports.
/// </para>
/// </summary>
public sealed class FrameSeries
{
    private readonly float[] _values;
    private int _count;
    private int _next; // write cursor; wraps

    public FrameSeries(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _values = new float[capacity];
    }

    /// <summary>Maximum number of samples retained.</summary>
    public int Capacity => _values.Length;

    /// <summary>Samples currently held (grows to <see cref="Capacity"/>, then stays).</summary>
    public int Count => _count;

    /// <summary>The most recently recorded sample, or 0 when empty.</summary>
    public float Last { get; private set; }

    /// <summary>Mean over the retained samples, or 0 when empty. Maintained incrementally is NOT worth it here —
    /// the window is small and a running sum would drift after millions of frames.</summary>
    public float Average
    {
        get
        {
            if (_count == 0)
            {
                return 0f;
            }

            var sum = 0f;
            for (var i = 0; i < _count; i++)
            {
                sum += _values[i];
            }

            return sum / _count;
        }
    }

    /// <summary>Largest retained sample, or 0 when empty. This is what a frame-time graph must scale to — the
    /// spikes are the interesting part, and averaging them away is how a stutter goes unnoticed.</summary>
    public float Max
    {
        get
        {
            var max = 0f;
            for (var i = 0; i < _count; i++)
            {
                if (_values[i] > max)
                {
                    max = _values[i];
                }
            }

            return max;
        }
    }

    /// <summary>
    /// Appends a sample, overwriting the oldest once full.
    /// </summary>
    public void Record(float value)
    {
        _values[_next] = value;
        _next = (_next + 1) % _values.Length;
        if (_count < _values.Length)
        {
            _count++;
        }

        Last = value;
    }

    /// <summary>
    /// Copies the history into <paramref name="destination"/> in <b>chronological order</b> (oldest first) and
    /// returns the filled slice. The backing array is circular, so it cannot be handed out directly without the
    /// graph drawing a series that jumps at the write cursor.
    /// </summary>
    public ReadOnlySpan<float> CopyChronological(Span<float> destination)
    {
        var n = Math.Min(_count, destination.Length);
        if (n == 0)
        {
            return default;
        }

        // Oldest sample: when the buffer has wrapped it sits at the cursor, otherwise at index 0.
        var oldest = _count == _values.Length ? _next : 0;
        for (var i = 0; i < n; i++)
        {
            // Take the LAST n samples, so a destination smaller than the history keeps the most recent window.
            var source = (oldest + (_count - n) + i) % _values.Length;
            destination[i] = _values[source];
        }

        return destination[..n];
    }

    /// <summary>Drops every sample.</summary>
    public void Clear()
    {
        _count = 0;
        _next = 0;
        Last = 0f;
    }
}

/// <summary>
/// Per-frame engine metrics: how long a frame took, and how much managed memory it allocated (UI-2).
/// <para>
/// <b>Why the allocation series matters most.</b> "Zero managed allocation per frame in steady state" is a blocking
/// project gate, but until now it was only ever checked in a bench run. Sampling it every frame and drawing it turns
/// the gate into something continuously visible: a regression shows up the moment it is introduced, in whatever
/// scene happens to be open, instead of at the next bench.
/// </para>
/// <para>
/// <see cref="Record"/> takes measurements the caller already made rather than sampling them itself — see the method
/// for why the measurement bracket is the whole point.
/// </para>
/// </summary>
public sealed class FrameStats
{
    /// <summary>Frames retained. ~4 s at 60 Hz — long enough to show a stutter in context, short enough that the
    /// graph stays readable at a HUD's width and that <see cref="FrameSeries.Average"/> stays cheap.</summary>
    public const int DefaultCapacity = 240;

    private bool _primed;

    public FrameStats(int capacity = DefaultCapacity)
    {
        FrameTimeMs = new FrameSeries(capacity);
        AllocatedBytes = new FrameSeries(capacity);
    }

    /// <summary>Frame duration in milliseconds.</summary>
    public FrameSeries FrameTimeMs { get; }

    /// <summary>Managed bytes allocated during the frame, on the recording thread.</summary>
    public FrameSeries AllocatedBytes { get; }

    /// <summary>Total frames recorded since construction.</summary>
    public long FrameCount { get; private set; }

    /// <summary>
    /// The last frame's allocation as an EXACT byte count. <see cref="AllocatedBytes"/> stores the same figure as a
    /// <c>float</c>, which is right for a graph (it only needs a ratio) but silently loses integer precision past
    /// 2^24 — 16 MiB, a threshold a loading frame crosses easily. A readout that presents itself as a byte count
    /// should not be approximate.
    /// </summary>
    public long LastAllocatedBytes { get; private set; }

    /// <summary>Frames per second derived from the mean frame time, or 0 before the first sample.</summary>
    public float AverageFps
    {
        get
        {
            var ms = FrameTimeMs.Average;
            return ms > 0f ? 1000f / ms : 0f;
        }
    }

    /// <summary>
    /// Records one completed frame from measurements the caller already took.
    /// <para>
    /// <b>The caller supplies the numbers rather than this type sampling them</b>, because the bracket is the whole
    /// point. Sampling here — once per frame, from a system — would measure from one tick to the next, sweeping in
    /// the host's windowing and input pump, which allocates and which the engine does not control. The readout would
    /// then sit permanently non-zero through no fault of the engine, and a permanently red indicator hides the
    /// regression it exists to catch. <see cref="FrameOrchestrator"/> brackets tick + render instead, which is
    /// exactly what the project's 0-alloc gate means and exactly what the cull-stats bench has always measured.
    /// </para>
    /// </summary>
    /// <param name="frameMs">Milliseconds the frame spent in engine work.</param>
    /// <param name="allocatedBytes">Managed bytes the frame allocated in engine work.</param>
    public void Record(float frameMs, long allocatedBytes)
    {
        // The FIRST call is dropped: on frame 0 the orchestrator has run Tick but its render delegate has not closed
        // a measurement yet, so the values are meaningless — and startup allocation would otherwise dwarf the
        // graph's scale for its whole retention window.
        if (_primed)
        {
            FrameTimeMs.Record(frameMs);
            AllocatedBytes.Record(allocatedBytes);
            LastAllocatedBytes = allocatedBytes;
            FrameCount++;
        }
        else
        {
            _primed = true;
        }
    }
}
