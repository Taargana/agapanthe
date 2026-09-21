using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Agapanthe.Engine;

/// <summary>
/// The buffer between input and the tick (MP-0d). Commands are ordered by <see cref="SimCommand.TargetTick"/>,
/// FIFO within a tick, and drained deterministically at a fixed point in <see cref="SimulationHost.Tick"/>.
/// This is <b>the</b> network seam — a dedicated server receives peer commands here.
/// <para>
/// <b>Single-threaded, owner thread — like <see cref="World.GameWorld"/>.</b> <see cref="AssertOwnerThread"/>
/// throws <see cref="InvalidOperationException"/> for any thread that is neither the owner nor sanctioned
/// (always active — Job-2 promoted this from Debug-only, since this class is a live network seam and the
/// asymmetry left a non-overlapping off-thread call in Release undetected). <see cref="Enqueue"/>/
/// <see cref="DrainUpTo"/> additionally guard against a genuine CROSS-thread <i>concurrent</i> call from two
/// otherwise-sanctioned callers (Job-2, see <see cref="EnterCriticalSection"/>) — a distinct, narrower hazard
/// `AssertOwnerThread` alone cannot see. A cross-thread receive path must still marshal onto the owner thread
/// first; neither guard makes concurrent access safe, they only make it loud.
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

    // Job-1 D2: mirrors GameWorld.SetSanctionedWorkerThreads — null until a scheduler configures one, in which case
    // AssertOwnerThread's behavior is bit-for-bit what it was before this field existed. Volatile-published for the
    // same reason as GameWorld's copy: the setter is callable at any time, not only inside the scheduler's own
    // Release()/Wait() happens-before chain.
    private HashSet<int>? _sanctionedWorkerThreadIds;

    // Job-2: closes the gap SetSanctionedWorkerThreads' own doc-comment admits — being sanctioned is not the same
    // as being safe against a concurrent caller on this SAME instance. -1 ("free") can never collide with a real
    // Environment.CurrentManagedThreadId (always >= 0).
    private const int FreeSentinel = -1;
    private int _criticalSectionThreadId = FreeSentinel;
    private int _criticalSectionDepth;

    /// <summary>
    /// Lets a job-system worker pool touch this queue without tripping <see cref="AssertOwnerThread"/>. Called once,
    /// by the scheduler that owns the pool, at pool creation (Job-1 D2). Does NOT relax the guard for any other
    /// thread — a genuinely foreign thread still throws. Unions with, never replaces, any set already sanctioned
    /// (Job-1 F2) — two independent pools must not un-sanction each other.
    /// <para>
    /// <b>Being sanctioned to call <see cref="Enqueue"/>/<see cref="DrainUpTo"/> is not the same as it being safe.</b>
    /// This mechanism only removes <see cref="AssertOwnerThread"/>'s tripwire for a thread that is the queue's
    /// ONLY caller this tick — it does not, by itself, arbitrate two sanctioned callers racing each other.
    /// <see cref="EnterCriticalSection"/> is the piece that does (Job-2): it DETECTS a genuine concurrent call
    /// and throws immediately — it does not serialize or block one caller behind the other, so two sanctioned
    /// threads must still take turns rather than truly overlap.
    /// </para>
    /// </summary>
    internal void SetSanctionedWorkerThreads(IReadOnlyCollection<int> threadIds)
    {
        ArgumentNullException.ThrowIfNull(threadIds);
        var merged = new HashSet<int>(threadIds);
        var existing = Volatile.Read(ref _sanctionedWorkerThreadIds);
        if (existing is not null)
        {
            merged.UnionWith(existing);
        }

        Volatile.Write(ref _sanctionedWorkerThreadIds, merged);
    }

    /// <summary>
    /// Commands currently buffered. Unlike <see cref="Enqueue"/>/<see cref="DrainUpTo"/>, this reads
    /// <c>_count</c> with no guard at all (not even <see cref="AssertOwnerThread"/>) — a torn read is
    /// impossible (a naturally-aligned <c>int</c>), but a caller on a different thread than the current owner
    /// gets a value with no happens-before relationship to it: best-effort/diagnostic only, never a basis for a
    /// correctness decision, off the owner thread.
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// Inserts <paramref name="command"/> keeping the buffer sorted by <see cref="SimCommand.TargetTick"/>
    /// ascending. Insertion shifts only while an existing entry's tick is <b>strictly greater</b>, so a command
    /// lands after every equal-tick command already queued — FIFO within a tick.
    /// </summary>
    public void Enqueue(in SimCommand command)
    {
        AssertOwnerThread();

        EnterCriticalSection();
        try
        {
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
        finally
        {
            ExitCriticalSection();
        }
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

        EnterCriticalSection();
        try
        {
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
        finally
        {
            ExitCriticalSection();
        }
    }

    /// <summary>
    /// Job-2: detects a genuine CROSS-thread concurrent call into <see cref="Enqueue"/>/<see cref="DrainUpTo"/> on
    /// this SAME instance and throws immediately — never a blocking <c>lock</c>, which would silently serialize a
    /// real bug instead of surfacing it (this project's established posture: <c>AssertOwnerThreadStrict</c>,
    /// <see cref="DrainUpTo"/>'s own reentrant-drain throw). Always active, Debug AND Release — unlike
    /// <see cref="AssertOwnerThread"/> (a developer-discipline reminder, redundant with correct calling
    /// convention in Release), a genuine race here would corrupt <see cref="_items"/>/<see cref="_count"/>/
    /// <see cref="_drainScratch"/> for real via a torn resize/shift; the guard itself (one
    /// <see cref="Interlocked.CompareExchange(ref int, int, int)"/>) is cheap enough that Release cost is not a
    /// concern.
    /// <para>
    /// <b>Same-thread reentrancy is legitimate and must not throw</b> — a <see cref="DrainUpTo"/> handler calling
    /// <see cref="Enqueue"/> is documented, tested behavior. <see cref="_criticalSectionDepth"/> is only ever
    /// touched by the thread the <see cref="Interlocked.CompareExchange(ref int, int, int)"/> has already
    /// established as the section's owner (or that same thread re-entering) — never two threads at once, by
    /// construction, so no additional synchronization is needed on the depth field itself.
    /// </para>
    /// </summary>
    private void EnterCriticalSection([CallerMemberName] string caller = "")
    {
        var current = Environment.CurrentManagedThreadId;
        var owner = Interlocked.CompareExchange(ref _criticalSectionThreadId, current, FreeSentinel);
        if (owner == FreeSentinel)
        {
            // A FRESH acquisition always starts depth at 1 — never increments whatever was left behind. An
            // audit (csharp-lowlevel, verified by mutation) found that incrementing here let a losing thread's
            // OWN mis-ordered Exit (a bug this method itself cannot cause, but a future third call site might)
            // leave a residual depth that silently wedges the section forever instead of failing loudly.
            _criticalSectionDepth = 1;
            return;
        }

        if (owner == current)
        {
            _criticalSectionDepth++;
            return;
        }

        ThrowConflict(caller, current, owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowConflict(string caller, int current, int owner)
    {
        throw new InvalidOperationException(
            $"SimCommandQueue.{caller} was entered by thread {current} while thread {owner} was already inside " +
            "Enqueue/DrainUpTo on this instance — they are not thread-safe against each other, only against a " +
            "single owner (plus a scheduler's sanctioned workers taking turns, never running concurrently on the " +
            "SAME queue instance).");
    }

    /// <summary>
    /// <b>Callers must invoke <see cref="EnterCriticalSection"/> BEFORE the <c>try</c> block it guards, never
    /// inside it.</b> If a losing thread's thrown conflict originated inside its own <c>try</c>, that thread's
    /// <c>finally</c> would still call this method — decrementing or clearing the WINNING thread's still-in-
    /// progress ownership, corrupting state neither thread actually released. Placed before the <c>try</c> (as
    /// both <see cref="Enqueue"/> and <see cref="DrainUpTo"/> do), a thrown conflict never pairs with an exit at
    /// all — the two <see cref="Debug.Assert"/>s below exist to make a future call site that gets this wrong fail
    /// loudly in Debug rather than silently wedge the section (an audit, csharp-lowlevel, reproduced the silent
    /// wedge by mutation).
    /// <para>
    /// The <see cref="Volatile.Write(ref int, int)"/> below is load-bearing, not a style choice: paired with
    /// <see cref="EnterCriticalSection"/>'s <see cref="Interlocked.CompareExchange(ref int, int, int)"/> (a full
    /// fence), it is what makes handing the queue off between two sequential owners on DIFFERENT threads safe —
    /// <see cref="_items"/> (including a reference reassigned by <see cref="Array.Resize{T}(ref T[], int)"/>),
    /// <see cref="_count"/>, and <see cref="_draining"/> are all published across this release/acquire pair. A
    /// plain write here would silently reopen that hole with no test going red.
    /// </para>
    /// </summary>
    private void ExitCriticalSection()
    {
        Debug.Assert(
            _criticalSectionThreadId == Environment.CurrentManagedThreadId,
            "ExitCriticalSection called from a thread that does not own the section — EnterCriticalSection must " +
            "precede the try block it guards, never sit inside it.");
        Debug.Assert(_criticalSectionDepth > 0, "Unbalanced ExitCriticalSection — depth was already 0.");

        if (--_criticalSectionDepth == 0)
        {
            Volatile.Write(ref _criticalSectionThreadId, FreeSentinel);
        }
    }

    /// <summary>
    /// Job-2: promoted from <see cref="ConditionalAttribute"/>("DEBUG") to always-on. This class is a live
    /// network seam (its own class doc says so) — a Debug-only check left the FAR more likely real-world hazard
    /// undetected in Release: a network-receive thread calling <see cref="Enqueue"/> between two ticks, with no
    /// overlap at all (so <see cref="EnterCriticalSection"/>'s guard never fires), still breaks the single-owner
    /// invariant this class exists to hold — the command's landing tick becomes non-deterministic across peers,
    /// which is exactly what a server-authoritative model depends on this queue to prevent (audit finding,
    /// engine-architect). The check itself is a thread-local read plus one comparison — strictly cheaper than
    /// the <see cref="Interlocked.CompareExchange(ref int, int, int)"/> already paid unconditionally below.
    /// </summary>
    private void AssertOwnerThread([CallerMemberName] string caller = "")
    {
        var callingThreadId = Environment.CurrentManagedThreadId;
        if (callingThreadId == _ownerThreadId)
        {
            return;
        }

        if (Volatile.Read(ref _sanctionedWorkerThreadIds) is { } sanctioned && sanctioned.Contains(callingThreadId))
        {
            return;
        }

        throw new InvalidOperationException(
            $"SimCommandQueue.{caller} was called from thread {callingThreadId}, but the " +
            $"queue is owned by thread {_ownerThreadId}. It is single-threaded — a receive path must marshal " +
            "onto the owner thread before enqueuing, unless the calling thread was sanctioned by a job-system " +
            "worker pool.");
    }
}
