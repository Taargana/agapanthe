using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Agapanthe.Engine;

/// <summary>
/// The buffer between input and the tick (MP-0d). Commands are ordered by <see cref="SimCommand.TargetTick"/>,
/// FIFO within a tick, and drained deterministically at a fixed point in <see cref="SimulationHost.Tick"/>.
/// This is <b>the</b> network seam — a dedicated server receives peer commands here.
/// <para>
/// <b>Single-threaded, owner thread — like <see cref="World.GameWorld"/>.</b> <see cref="Enqueue"/> and
/// <see cref="DrainUpTo"/> carry a <see cref="ConditionalAttribute"/> guard that <b>throws</b>
/// <see cref="InvalidOperationException"/> off-thread (not <c>Debug.Assert</c>, which would tear down a test
/// host). A cross-thread receive path must marshal onto the owner thread first.
/// </para>
/// <para>
/// Pre-grown backing array; 0-alloc after warmup. It grows on overflow — flood protection is a netcode concern,
/// deferred (this matches <c>GameWorld._commands</c>, which is also uncapped).
/// </para>
/// </summary>
public sealed class SimCommandQueue
{
    private const int InitialCapacity = 64;

    private SimCommand[] _items = new SimCommand[InitialCapacity];
    private SimCommand[] _drainScratch = new SimCommand[InitialCapacity];
    private int _count;
    private bool _draining;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

    /// <summary>Commands currently buffered.</summary>
    public int Count => _count;

    /// <summary>
    /// Inserts <paramref name="command"/> keeping the buffer sorted by <see cref="SimCommand.TargetTick"/>
    /// ascending. Insertion shifts only while an existing entry's tick is <b>strictly greater</b>, so a command
    /// lands after every equal-tick command already queued — FIFO within a tick.
    /// </summary>
    public void Enqueue(in SimCommand command)
    {
        AssertOwnerThread();

        if (_count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        var i = _count;
        while (i > 0 && _items[i - 1].TargetTick > command.TargetTick)
        {
            _items[i] = _items[i - 1];
            i--;
        }

        _items[i] = command;
        _count++;
    }

    /// <summary>
    /// Applies every buffered command whose <see cref="SimCommand.TargetTick"/> is <c>&lt;= <paramref name="tick"/></c>,
    /// front to back (ascending tick, FIFO within a tick), via <paramref name="handler"/>. A command already past
    /// its tick runs <b>now</b> — it is never silently dropped, which is what keeps two drivers that chunk frames
    /// differently in agreement. Returns how many ran.
    /// <para>
    /// <b>The due prefix is lifted out and the buffer compacted BEFORE any handler runs</b> (audit F2): a handler
    /// is free to <see cref="Enqueue"/> a derived command (or a netcode relay to), and it lands cleanly for a later
    /// drain rather than being shifted under the cursor mid-iteration. A command a handler enqueues for the tick
    /// currently draining is therefore <i>not</i> applied by this call — it waits for the next drain. Re-entrant
    /// <see cref="DrainUpTo"/> (a handler draining the queue) throws.
    /// </para>
    /// </summary>
    public int DrainUpTo(long tick, SimCommandHandler handler)
    {
        AssertOwnerThread();
        ArgumentNullException.ThrowIfNull(handler);

        if (_draining)
        {
            throw new InvalidOperationException(
                "SimCommandQueue.DrainUpTo is not re-entrant — a command handler must not drain the queue.");
        }

        var run = 0;
        while (run < _count && _items[run].TargetTick <= tick)
        {
            run++;
        }

        if (run == 0)
        {
            return 0;
        }

        if (_drainScratch.Length < run)
        {
            Array.Resize(ref _drainScratch, Math.Max(run, _drainScratch.Length * 2));
        }

        Array.Copy(_items, 0, _drainScratch, 0, run);
        var remaining = _count - run;
        Array.Copy(_items, run, _items, 0, remaining);
        _count = remaining;

        _draining = true;
        try
        {
            for (var i = 0; i < run; i++)
            {
                handler(in _drainScratch[i]);
            }
        }
        finally
        {
            _draining = false;
        }

        return run;
    }

    [Conditional("DEBUG")]
    private void AssertOwnerThread([CallerMemberName] string caller = "")
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                $"SimCommandQueue.{caller} was called from thread {Environment.CurrentManagedThreadId}, but the " +
                $"queue is owned by thread {_ownerThreadId}. It is single-threaded — a receive path must marshal " +
                "onto the owner thread before enqueuing.");
        }
    }
}
