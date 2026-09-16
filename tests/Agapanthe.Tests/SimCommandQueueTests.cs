using Agapanthe.Core;
using Agapanthe.Engine;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0d W1 — <see cref="SimCommandQueue"/> in isolation: tick ordering, FIFO within a tick, "past-tick runs now",
/// 0-alloc after warmup, and the owner-thread guard that must <b>throw</b> (this is the network seam).
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
