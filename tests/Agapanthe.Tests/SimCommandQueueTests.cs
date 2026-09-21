using Agapanthe.Core;
using Agapanthe.Engine;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0d W1 — <see cref="SimCommandQueue"/> in isolation: tick ordering, FIFO within a tick, "past-tick runs now",
/// 0-alloc after warmup, and the owner-thread guard that must <b>throw</b> (this is the network seam).
/// <para>
/// Job-2 adds: a genuine CROSS-thread concurrent entry into <see cref="SimCommandQueue.Enqueue"/>/
/// <see cref="SimCommandQueue.DrainUpTo"/> must throw (its own Job-1 doc-comment admitted this was previously
/// unguarded — "does not arbitrate concurrent callers"), while the existing legitimate SAME-thread reentrancy
/// (a drain handler enqueuing, <see cref="HandlerEnqueuingDuringDrain_DoesNotCorrupt_AndTheNewCommandWaitsForTheNextDrain"/>
/// below) must keep working unchanged.
/// </para>
/// </summary>
public sealed class SimCommandQueueTests
{
    private static SimCommand Cmd(long tick, byte kind = 0)
        => new(tick, kind, default, Double3.Zero, 0f, 0u);

    private static List<byte> DrainAll(SimCommandQueue queue, long upTo)
    {
        var seen = new List<byte>();
        queue.DrainUpTo(upTo, (in SimCommand c) => seen.Add(c.Kind));
        return seen;
    }

    [Fact]
    public void DrainUpTo_RunsOnlyCommandsAtOrBeforeTheTick()
    {
        var queue = new SimCommandQueue();
        queue.Enqueue(Cmd(3, kind: 1));
        queue.Enqueue(Cmd(5, kind: 2));
        queue.Enqueue(Cmd(9, kind: 3));

        var ran = queue.DrainUpTo(5, static (in SimCommand _) => { });

        Assert.Equal(2, ran);
        Assert.Equal(1, queue.Count);
        Assert.Equal(new byte[] { 3 }, DrainAll(queue, long.MaxValue).ToArray());
    }

    [Fact]
    public void DrainUpTo_AppliesAscendingTick_FifoWithinATick()
    {
        var queue = new SimCommandQueue();
        // Enqueued out of order: B@5, A@5, C@3 -> drain order must be C, B, A.
        queue.Enqueue(Cmd(5, kind: (byte)'B'));
        queue.Enqueue(Cmd(5, kind: (byte)'A'));
        queue.Enqueue(Cmd(3, kind: (byte)'C'));

        Assert.Equal(
            new[] { (byte)'C', (byte)'B', (byte)'A' },
            DrainAll(queue, long.MaxValue).ToArray());
    }

    [Fact]
    public void DrainUpTo_RunsACommandAlreadyPastItsTick_NeverDrops()
    {
        var queue = new SimCommandQueue();
        queue.Enqueue(Cmd(2, kind: 7));

        var ran = queue.DrainUpTo(100, static (in SimCommand _) => { });

        Assert.Equal(1, ran);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void EnqueueAndDrain_AreZeroAllocAfterWarmup()
    {
        var queue = new SimCommandQueue();
        SimCommandHandler noop = static (in SimCommand _) => { };

        // Warm up: grow the backing array once, JIT the paths.
        for (var i = 0; i < 256; i++)
        {
            queue.Enqueue(Cmd(i % 8));
        }

        queue.DrainUpTo(long.MaxValue, noop);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 1_000; frame++)
        {
            queue.Enqueue(Cmd(frame + 2));
            queue.Enqueue(Cmd(frame));
            queue.Enqueue(Cmd(frame + 1));
            queue.DrainUpTo(frame, noop);
        }

        queue.DrainUpTo(long.MaxValue, noop);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Same-thread reentrancy (a drain handler enqueuing) must NOT throw under Job-2's new cross-thread
    /// concurrency guard — this test predates Job-2 and already covers the behavior; it doubles as that
    /// guard's regression pin without needing a duplicate.</summary>
    [Fact]
    public void HandlerEnqueuingDuringDrain_DoesNotCorrupt_AndTheNewCommandWaitsForTheNextDrain()
    {
        var queue = new SimCommandQueue();
        queue.Enqueue(Cmd(5, kind: (byte)'A'));
        queue.Enqueue(Cmd(5, kind: (byte)'B'));

        var seen = new List<byte>();
        var reentered = false;
        SimCommandHandler? handler = null;
        handler = (in SimCommand c) =>
        {
            seen.Add(c.Kind);
            if (!reentered)
            {
                reentered = true;
                queue.Enqueue(Cmd(3, kind: (byte)'C')); // lower tick — the old code would shuffle it under the cursor
            }
        };

        var ranFirst = queue.DrainUpTo(5, handler);

        // Both original commands ran exactly once, in order; C did NOT run in this drain.
        Assert.Equal(2, ranFirst);
        Assert.Equal(new[] { (byte)'A', (byte)'B' }, seen.ToArray());
        Assert.Equal(1, queue.Count);

        // C is applied by the next drain — never dropped.
        Assert.Equal(new[] { (byte)'C' }, DrainAll(queue, long.MaxValue).ToArray());
    }

    [Fact]
    public void ReentrantDrain_Throws()
    {
        var queue = new SimCommandQueue();
        queue.Enqueue(Cmd(0));

        var ex = Record.Exception(() =>
            queue.DrainUpTo(0, (in SimCommand _) => queue.DrainUpTo(0, static (in SimCommand _) => { })));

        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void Enqueue_ThrowsOnAForeignThread()
    {
        var queue = new SimCommandQueue();
        var thrown = RunOnAnotherThread(() => queue.Enqueue(Cmd(0)));
        Assert.IsType<InvalidOperationException>(thrown);
    }

    [Fact]
    public void DrainUpTo_ThrowsOnAForeignThread()
    {
        var queue = new SimCommandQueue();
        var thrown = RunOnAnotherThread(() => queue.DrainUpTo(0, static (in SimCommand _) => { }));
        Assert.IsType<InvalidOperationException>(thrown);
    }

    [Fact]
    public void Enqueue_FromASanctionedWorkerThread_DoesNotThrow()
    {
        var queue = new SimCommandQueue();
        var thrown = RunOnAnotherThread(
            () => queue.Enqueue(Cmd(0)),
            sanctionThisThread: queue.SetSanctionedWorkerThreads);

        Assert.Null(thrown);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Enqueue_FromAnUnsanctionedThread_StillThrows_EvenWithOtherThreadsSanctioned()
    {
        // Regression guard for Job-1 D2: sanctioning a worker pool must not accidentally disable the check for
        // everyone — an unrelated foreign thread (not in the pool) must still be rejected.
        var queue = new SimCommandQueue();
        queue.SetSanctionedWorkerThreads([-1]); // a thread id that will never be Environment.CurrentManagedThreadId

        var thrown = RunOnAnotherThread(() => queue.Enqueue(Cmd(0)));
        Assert.IsType<InvalidOperationException>(thrown);
    }

    /// <summary>
    /// The core Job-2 guarantee: two REAL threads, one already inside <see cref="SimCommandQueue.DrainUpTo"/>
    /// (via a handler parked on a <see cref="ManualResetEventSlim"/> — never <c>Thread.Sleep</c>, so the overlap
    /// is deterministic, not timing-luck), the other attempting a concurrent <see cref="SimCommandQueue.Enqueue"/>.
    /// The second thread must throw immediately; the first must be entirely unaffected by that failure.
    /// </summary>
    [Fact]
    public void ConcurrentEnqueue_FromTwoRealThreads_OneThrows()
    {
        var queue = new SimCommandQueue();
        queue.Enqueue(Cmd(0));
        var t1Entered = new ManualResetEventSlim(false);
        var t1CanExit = new ManualResetEventSlim(false);
        Exception? t1Exception = null;
        Exception? t2Exception = null;

        var t1 = new Thread(() =>
        {
            try
            {
                queue.SetSanctionedWorkerThreads([Environment.CurrentManagedThreadId]);
                queue.DrainUpTo(0, (in SimCommand _) =>
                {
                    t1Entered.Set();
                    t1CanExit.Wait();
                });
            }
            catch (Exception ex)
            {
                // Captured, not swallowed: an unhandled exception on a foreground Thread terminates the test
                // host process instead of failing this test (audit finding, csharp-lowlevel) — a regression
                // that made T1 itself throw would otherwise crash the run rather than show as a red test.
                t1Exception = ex;
            }
        });
        t1.Start();
        t1Entered.Wait();

        var t2 = new Thread(() =>
        {
            try
            {
                queue.SetSanctionedWorkerThreads([Environment.CurrentManagedThreadId]);
                queue.Enqueue(Cmd(1));
            }
            catch (Exception ex)
            {
                t2Exception = ex;
            }
        });
        t2.Start();
        t2.Join();

        t1CanExit.Set();
        t1.Join();

        Assert.Null(t1Exception);
        Assert.IsType<InvalidOperationException>(t2Exception);
    }

    /// <summary>
    /// Job-2's ordering-hazard regression: <c>EnterCriticalSection()</c> must be called BEFORE the guarded
    /// method's <c>try</c> block, never inside it — otherwise a losing thread's thrown conflict would still run
    /// a <c>finally</c> that calls <c>ExitCriticalSection()</c>, corrupting the WINNING thread's still-in-progress
    /// ownership.
    /// <para>
    /// <b>The discriminating assertion is T3's throw, not T1's probe succeeding.</b> An earlier version of this
    /// test asserted only that T1's own same-thread probe (<c>queue.Enqueue(Cmd(9, ...))</c>) succeeded after
    /// T2's failed attempt — an audit (csharp-lowlevel) proved BY MUTATION that this passes even with
    /// <c>EnterCriticalSection()</c> moved inside the <c>try</c> (the exact bug this test exists to catch): T2's
    /// misplaced <c>Exit</c> frees the section entirely, so T1's probe just re-acquires it from scratch and
    /// "succeeds" — for the wrong reason. The section being ACTUALLY still held only shows up as a SECOND
    /// concurrent attempt (T3) also throwing, because a wrongly-freed section would let T3 through.
    /// </para>
    /// </summary>
    [Fact]
    public void PostFailureIntegrity_ALosingThreadsThrowDoesNotCorruptTheWinner()
    {
        var queue = new SimCommandQueue();
        queue.Enqueue(Cmd(0));
        var t1Entered = new ManualResetEventSlim(false);
        var t2Done = new ManualResetEventSlim(false);
        var t3Done = new ManualResetEventSlim(false);
        Exception? t1Exception = null;
        Exception? t2Exception = null;
        Exception? t3Exception = null;
        var probeSucceeded = false;

        var t1 = new Thread(() =>
        {
            try
            {
                queue.SetSanctionedWorkerThreads([Environment.CurrentManagedThreadId]);
                queue.DrainUpTo(0, (in SimCommand _) =>
                {
                    t1Entered.Set();
                    t2Done.Wait();
                    t3Done.Wait();

                    // Same-thread probe: legitimate reentrancy, must succeed regardless of what T2/T3 just did.
                    queue.Enqueue(Cmd(9, kind: 42));
                    probeSucceeded = true;
                });
            }
            catch (Exception ex)
            {
                t1Exception = ex;
            }
        });
        t1.Start();
        t1Entered.Wait();

        var t2 = new Thread(() =>
        {
            try
            {
                queue.SetSanctionedWorkerThreads([Environment.CurrentManagedThreadId]);
                queue.Enqueue(Cmd(1));
            }
            catch (Exception ex)
            {
                t2Exception = ex;
            }
            finally
            {
                t2Done.Set();
            }
        });
        t2.Start();
        t2.Join();

        // The discriminating probe: if T2's failed attempt had wrongly freed the section (the mutation this
        // test exists to catch), T3 would succeed instead of throwing.
        var t3 = new Thread(() =>
        {
            try
            {
                queue.SetSanctionedWorkerThreads([Environment.CurrentManagedThreadId]);
                queue.Enqueue(Cmd(2));
            }
            catch (Exception ex)
            {
                t3Exception = ex;
            }
            finally
            {
                t3Done.Set();
            }
        });
        t3.Start();
        t3.Join();
        t1.Join();

        Assert.Null(t1Exception);
        Assert.IsType<InvalidOperationException>(t2Exception);
        Assert.IsType<InvalidOperationException>(t3Exception);
        Assert.True(probeSucceeded);
        Assert.Equal(new byte[] { 42 }, DrainAll(queue, long.MaxValue).ToArray());
    }

    /// <summary>
    /// The guard's actual intended future use is a sanctioned WORKER thread calling <see cref="SimCommandQueue.Enqueue"/>
    /// (Job-1's own <c>_workerAllocatedBytesForTest</c> precedent) — <see cref="GC.GetAllocatedBytesForCurrentThread"/>
    /// is per-thread, so the owner-thread measurement in <see cref="EnqueueAndDrain_AreZeroAllocAfterWarmup"/>
    /// cannot see an allocation the guard introduces only on a worker thread's path. Measured separately here.
    /// </summary>
    [Fact]
    public void EnqueueAndDrainUpTo_AreZeroAllocAfterWarmup_OnASanctionedWorkerThread()
    {
        var queue = new SimCommandQueue();
        SimCommandHandler noop = static (in SimCommand _) => { };
        long? workerDelta = null;
        Exception? workerException = null;

        var worker = new Thread(() =>
        {
            try
            {
                queue.SetSanctionedWorkerThreads([Environment.CurrentManagedThreadId]);

                for (var i = 0; i < 64; i++)
                {
                    queue.Enqueue(Cmd(i % 8));
                    queue.DrainUpTo(long.MaxValue, noop);
                }

                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var frame = 0; frame < 256; frame++)
                {
                    queue.Enqueue(Cmd(frame));
                    queue.DrainUpTo(long.MaxValue, noop);
                }

                workerDelta = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (Exception ex)
            {
                workerException = ex;
            }
        });
        worker.Start();
        worker.Join();

        Assert.Null(workerException);
        Assert.Equal(0, workerDelta);
    }

    private static Exception? RunOnAnotherThread(Action action, Action<IReadOnlyCollection<int>>? sanctionThisThread = null)
    {
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try
            {
                sanctionThisThread?.Invoke([Environment.CurrentManagedThreadId]);
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        t.Start();
        t.Join();
        return captured;
    }
}
