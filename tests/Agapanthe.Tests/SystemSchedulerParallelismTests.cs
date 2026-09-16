using System.Diagnostics;
using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// Job-1 D5/D6 — end-to-end proof that <see cref="SystemScheduler"/>'s <see cref="Stage.Simulation"/> waves
/// actually run concurrently, that conflicting/exclusive systems never do, that the sanctioned-thread wiring
/// (Job-1 D2) reaches <see cref="GameWorld"/> and <see cref="SimCommandQueue"/> correctly, and that steady-state
/// dispatch is 0-alloc on both the owner thread and every worker thread. <see cref="SystemSchedulerWaveGroupingTests"/>
/// covers the grouping algorithm in isolation (no threads); this file is the real thing.
/// </summary>
[Collection("World")]
public sealed class SystemSchedulerParallelismTests
{
    private sealed class ComponentA;
    private sealed class ComponentB;

    // Tracks how many TrackedSystem.Execute calls are concurrently inside their busy-wait, and the highest count
    // ever observed — proves (or disproves) real concurrent execution without relying on wall-clock timing alone.
    private sealed class ConcurrencyTracker
    {
        private int _active;
        private int _maxObserved;
        private readonly object _lock = new();

        public IDisposable Enter()
        {
            var now = Interlocked.Increment(ref _active);
            lock (_lock)
            {
                if (now > _maxObserved)
                {
                    _maxObserved = now;
                }
            }

            return new Exit(this);
        }

        private void Leave() => Interlocked.Decrement(ref _active);

        public int MaxObserved
        {
            get { lock (_lock) { return _maxObserved; } }
        }

        private sealed class Exit(ConcurrencyTracker owner) : IDisposable
        {
            public void Dispose() => owner.Leave();
        }
    }

    private sealed class TrackedSystem(
        ConcurrencyTracker tracker,
        TimeSpan busyWait,
        IReadOnlyList<Type>? reads = null,
        IReadOnlyList<Type>? writes = null,
        bool requiresExclusiveExecution = false) : ISystem
    {
        public IReadOnlyList<Type> Reads { get; } = reads ?? [];
        public IReadOnlyList<Type> Writes { get; } = writes ?? [];
        public bool RequiresExclusiveExecution { get; } = requiresExclusiveExecution;

        public void Execute(in TickContext ctx)
        {
            using (tracker.Enter())
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < busyWait)
                {
                    // Busy-wait, not Thread.Sleep: a sleeping thread can mask thread-pool starvation and would
                    // understate the wall-clock savings this test is trying to prove.
                }
            }
        }
    }

    private sealed class LambdaWriteSystem(Action body, IReadOnlyList<Type> writes) : ISystem
    {
        public IReadOnlyList<Type> Writes { get; } = writes;
        public bool RequiresExclusiveExecution => false;
        public void Execute(in TickContext ctx) => body();
    }

    // Calls a pure-read, AssertOwnerThread-guarded GameWorld API from Execute. Proves the sanctioned-thread wiring
    // (Job-1 D2) end-to-end: if SimulationHost failed to relay the worker pool's thread ids to GameWorld, this
    // throws InvalidOperationException the moment it runs on a worker thread. IsAlive is read-only and touches no
    // structural command queue, so two instances calling it concurrently from different worker threads is safe —
    // unlike Spawn/Despawn, which mutate GameWorld's always-shared command queue and must stay
    // RequiresExclusiveExecution == true (the interface's own default) until a future sub-milestone models that
    // queue as a declarable resource.
    private sealed class WorldReadingSystem(GameWorld world, IReadOnlyList<Type> writes) : ISystem
    {
        public IReadOnlyList<Type> Writes { get; } = writes;
        public bool RequiresExclusiveExecution => false;
        public void Execute(in TickContext ctx) => world.IsAlive(default);
    }

    // A no-op system that busy-waits briefly while `warmingUp` is true. CountdownEvent/SemaphoreSlim lazily create
    // their own internal kernel wait object the FIRST time a call genuinely blocks (not at construction) — and once
    // created, that object is reused for the instance's whole lifetime, so the allocation happens at most once. A
    // truly instantaneous NoOpSystem races the owner thread's Wait() against the workers finishing: most of the
    // time the owner's spin-wait fast path wins and no real block (hence no lazy allocation) ever happens, but
    // occasionally OS scheduling flips that outcome — turning "first real block" into a coin flip that can land on
    // any tick, including one inside the measured window. Forcing a real block during an explicit, unmeasured
    // warmup phase makes the lazy allocation happen there instead, deterministically, every run.
    private sealed class WarmupAwareNoOpSystem(IReadOnlyList<Type> writes, Func<bool> warmingUp) : ISystem
    {
        public IReadOnlyList<Type> Writes { get; } = writes;
        public bool RequiresExclusiveExecution => false;

        public void Execute(in TickContext ctx)
        {
            if (!warmingUp())
            {
                return;
            }

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromMilliseconds(5))
            {
            }
        }
    }

    [Fact]
    public void TwoDisjointSystems_ActuallyRunConcurrently_WallClockBeatsTheSumOfBothDelays()
    {
        if (Environment.ProcessorCount < 3)
        {
            // Job-1's worker pool is ProcessorCount - 1: fewer than 2 non-owner cores means the two systems below
            // are forced onto a single worker and would legitimately run sequentially. Nothing to disprove on such
            // hardware — skip rather than produce a false failure.
            return;
        }

        var tracker = new ConcurrencyTracker();
        var delay = TimeSpan.FromMilliseconds(150);
        using var scheduler = new SystemScheduler();
        scheduler.Add(Stage.Simulation, new TrackedSystem(tracker, delay, writes: [typeof(ComponentA)]));
        scheduler.Add(Stage.Simulation, new TrackedSystem(tracker, delay, writes: [typeof(ComponentB)]));

        var wall = Stopwatch.StartNew();
        scheduler.Tick(1f / 60f);
        wall.Stop();

        Assert.True(
            wall.Elapsed < delay + delay,
            $"expected wall-clock < {(delay + delay).TotalMilliseconds} ms (both systems run concurrently), got {wall.Elapsed.TotalMilliseconds} ms");
        Assert.Equal(2, tracker.MaxObserved);
    }

    [Fact]
    public void ParallelWave_ProducesTheExpectedFinalState()
    {
        var results = new int[2];
        using var scheduler = new SystemScheduler();
        scheduler.Add(Stage.Simulation, new LambdaWriteSystem(() => results[0] = 41, [typeof(ComponentA)]));
        scheduler.Add(Stage.Simulation, new LambdaWriteSystem(() => results[1] = 1, [typeof(ComponentB)]));

        scheduler.Tick(1f / 60f);

        Assert.Equal(41, results[0]);
        Assert.Equal(1, results[1]);
    }

    [Fact]
    public void SystemsWithOverlappingWrites_NeverRunConcurrently()
    {
        var tracker = new ConcurrencyTracker();
        var delay = TimeSpan.FromMilliseconds(30);
        using var scheduler = new SystemScheduler();
        scheduler.Add(Stage.Simulation, new TrackedSystem(tracker, delay, writes: [typeof(ComponentA)]));
        scheduler.Add(Stage.Simulation, new TrackedSystem(tracker, delay, writes: [typeof(ComponentA)]));

        scheduler.Tick(1f / 60f);

        Assert.Equal(1, tracker.MaxObserved);
    }

    [Fact]
    public void RequiresExclusiveExecution_NeverRunsConcurrentlyWithAnyOtherSystem()
    {
        var tracker = new ConcurrencyTracker();
        var delay = TimeSpan.FromMilliseconds(30);
        using var scheduler = new SystemScheduler();
        scheduler.Add(Stage.Simulation, new TrackedSystem(tracker, delay, requiresExclusiveExecution: true));
        scheduler.Add(Stage.Simulation, new TrackedSystem(tracker, delay));

        scheduler.Tick(1f / 60f);

        Assert.Equal(1, tracker.MaxObserved);
    }

    [Fact]
    public void MultiSystemWave_SanctionsWorkerThreads_SoAGuardedGameWorldReadDoesNotThrow()
    {
        using var world = new GameWorld();
        using var host = SimulationHost.CreateDefault(world);
        host.Add(Stage.Simulation, new WorldReadingSystem(world, [typeof(ComponentA)]));
        host.Add(Stage.Simulation, new WorldReadingSystem(world, [typeof(ComponentB)]));

        var ex = Record.Exception(() => host.Tick(1f / 60f));

        Assert.Null(ex);
    }

    [Fact]
    public void MultiSystemWave_SanctionsCommandQueueWorkerThreads_SoAGuardedEnqueueDoesNotThrow()
    {
        // Deliberately only ONE system in this wave touches host.Commands. SimCommandQueue.Enqueue itself is NOT
        // thread-safe (a shared array resize/shift/count, exactly like GameWorld's own structural queue) — two
        // systems both calling Enqueue concurrently on the SAME queue would be a genuine data race, not a proof of
        // anything. The point of this test is narrower and still real: that the sanctioned-thread id SimulationHost
        // wires into host.Commands (Job-1 D2) actually reaches it, by calling Enqueue from whichever worker thread
        // picks up this system — paired with a second, disjoint, read-only system so the wave still has 2 systems
        // and a real worker pool is still exercised.
        using var world = new GameWorld();
        using var host = SimulationHost.CreateDefault(world);
        host.Add(Stage.Simulation, new CommandEnqueuingSystem(host.Commands, [typeof(ComponentA)]));
        host.Add(Stage.Simulation, new WorldReadingSystem(world, [typeof(ComponentB)]));

        var ex = Record.Exception(() => host.Tick(1f / 60f));

        Assert.Null(ex);
    }

    private sealed class CommandEnqueuingSystem(SimCommandQueue commands, IReadOnlyList<Type> writes) : ISystem
    {
        public IReadOnlyList<Type> Writes { get; } = writes;
        public bool RequiresExclusiveExecution => false;

        public void Execute(in TickContext ctx)
            => commands.Enqueue(new SimCommand(ctx.TickIndex + 1, 0, default, Double3.Zero, 0f, 0u));
    }

    [Fact]
    public void SteadyState_TickWithActiveParallelWaves_Is0AllocOnTheOwnerThread()
    {
        var warmingUp = true;
        using var scheduler = new SystemScheduler();
        scheduler.Add(Stage.Simulation, new WarmupAwareNoOpSystem([typeof(ComponentA)], () => warmingUp));
        scheduler.Add(Stage.Simulation, new WarmupAwareNoOpSystem([typeof(ComponentB)], () => warmingUp));

        // Deterministically force a real block (see WarmupAwareNoOpSystem's remarks) before the measured window.
        for (var i = 0; i < 10; i++)
        {
            scheduler.Tick(1f / 60f);
        }

        warmingUp = false;

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            scheduler.Tick(1f / 60f);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void SteadyState_TickWithActiveParallelWaves_Is0AllocOnEveryWorkerThread()
    {
        var warmingUp = true;
        using var scheduler = new SystemScheduler();
        scheduler.Add(Stage.Simulation, new WarmupAwareNoOpSystem([typeof(ComponentA)], () => warmingUp));
        scheduler.Add(Stage.Simulation, new WarmupAwareNoOpSystem([typeof(ComponentB)], () => warmingUp));

        // See the owner-thread variant of this test for why warmup must force a real block.
        for (var i = 0; i < 10; i++)
        {
            scheduler.Tick(1f / 60f);
        }

        warmingUp = false;

        var before = scheduler.GetWorkerAllocatedBytesForTest()!.ToArray();
        for (var i = 0; i < 100; i++)
        {
            scheduler.Tick(1f / 60f);
        }

        // WorkerLoop's own bracket (Job-1) is the only thing that can attribute bytes to a worker thread here, since
        // WarmupAwareNoOpSystem allocates nothing once warmingUp is false — a non-zero delta on any worker means the
        // DISPATCH machinery itself allocates, which is exactly what this test exists to catch.
        var after = scheduler.GetWorkerAllocatedBytesForTest()!;
        for (var i = 0; i < after.Length; i++)
        {
            Assert.Equal(0, after[i] - before[i]);
        }
    }

    private sealed class ThrowingSystem(IReadOnlyList<Type> writes, Func<Exception> makeException) : ISystem
    {
        public IReadOnlyList<Type> Writes { get; } = writes;
        public bool RequiresExclusiveExecution => false;
        public void Execute(in TickContext ctx) => throw makeException();
    }

    [Fact]
    public void OneThrowingSystemInAWave_SurfacesOnTheOwnerThread_WithTheOriginalExceptionType()
    {
        // Job-1 audit F4: the exception-propagation path (ExceptionDispatchInfo, single-exception branch) had no
        // coverage at all — every prior test used only healthy systems.
        using var scheduler = new SystemScheduler();
        scheduler.Add(Stage.Simulation, new ThrowingSystem([typeof(ComponentA)], () => new InvalidOperationException("boom")));
        scheduler.Add(Stage.Simulation, new NoOpDisjointSystem([typeof(ComponentB)]));

        var ex = Record.Exception(() => scheduler.Tick(1f / 60f));

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Equal("boom", ex!.Message);
    }

    [Fact]
    public void TwoThrowingSystemsInAWave_SurfaceAsAnAggregateExceptionWithBoth()
    {
        using var scheduler = new SystemScheduler();
        scheduler.Add(Stage.Simulation, new ThrowingSystem([typeof(ComponentA)], () => new InvalidOperationException("a")));
        scheduler.Add(Stage.Simulation, new ThrowingSystem([typeof(ComponentB)], () => new ArgumentException("b")));

        var ex = Record.Exception(() => scheduler.Tick(1f / 60f));

        var aggregate = Assert.IsType<AggregateException>(ex);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Contains(aggregate.InnerExceptions, e => e is InvalidOperationException { Message: "a" });
        Assert.Contains(aggregate.InnerExceptions, e => e is ArgumentException { Message: "b" });
    }

    [Fact]
    public void SchedulerStillTicksCorrectly_OnTheTickAfterAWorkerThrew()
    {
        // Recovery, not just detection: the countdown/semaphore state must be consistent enough that a SUBSEQUENT
        // tick still dispatches and joins correctly after an earlier one threw.
        var throwOnce = true;
        using var scheduler = new SystemScheduler();
        var results = new int[2];
        scheduler.Add(Stage.Simulation, new LambdaWriteSystem(
            () =>
            {
                if (throwOnce)
                {
                    throwOnce = false;
                    throw new InvalidOperationException("first tick only");
                }

                results[0] = 1;
            },
            [typeof(ComponentA)]));
        scheduler.Add(Stage.Simulation, new LambdaWriteSystem(() => results[1] = 2, [typeof(ComponentB)]));

        Assert.Throws<InvalidOperationException>(() => scheduler.Tick(1f / 60f));
        scheduler.Tick(1f / 60f);

        Assert.Equal(1, results[0]);
        Assert.Equal(2, results[1]);
    }

    private sealed class CountingSystem(IReadOnlyList<Type> writes, int[] runCounts, int index) : ISystem
    {
        public IReadOnlyList<Type> Writes { get; } = writes;
        public bool RequiresExclusiveExecution => false;
        public void Execute(in TickContext ctx) => Interlocked.Increment(ref runCounts[index]);
    }

    private sealed class NoOpDisjointSystem(IReadOnlyList<Type> writes) : ISystem
    {
        public IReadOnlyList<Type> Writes { get; } = writes;
        public bool RequiresExclusiveExecution => false;
        public void Execute(in TickContext ctx)
        {
        }
    }

    // 17 mutually distinct, built-in .NET types — used purely as disjoint Writes markers so that many systems land
    // in the SAME wave (no two ever "conflict"), without hand-declaring 17 marker classes. 17 is prime: for any
    // worker count other than exactly 1 or 17, wave.Count % active is non-zero, exercising BuildSimulationWaves's
    // partition remainder (Job-1 audit F4).
    private static readonly Type[] SeventeenDistinctTypes =
    [
        typeof(int), typeof(long), typeof(float), typeof(double), typeof(bool), typeof(byte), typeof(short),
        typeof(uint), typeof(ulong), typeof(sbyte), typeof(char), typeof(string), typeof(decimal), typeof(object),
        typeof(Guid), typeof(DateTime), typeof(TimeSpan),
    ];

    [Fact]
    public void WideWave_EveryPartitionCoversItsSystems_NoneSkippedOrDoubleRun()
    {
        // Job-1 audit F4: every prior test used exactly 2 systems, so baseSize was always 1 and remainder always 0 —
        // the uneven-partition arithmetic (BuildSimulationWaves's baseSize/remainder split) was correct but
        // completely unexercised. 17 mutually disjoint systems force at least one worker to run more than one
        // system on any realistic core count, and (17 being prime) a non-zero remainder almost everywhere.
        var wideCount = SeventeenDistinctTypes.Length;
        var runCounts = new int[wideCount];
        using var scheduler = new SystemScheduler();
        for (var i = 0; i < wideCount; i++)
        {
            scheduler.Add(Stage.Simulation, new CountingSystem([SeventeenDistinctTypes[i]], runCounts, i));
        }

        scheduler.Tick(1f / 60f);

        for (var i = 0; i < wideCount; i++)
        {
            Assert.Equal(1, runCounts[i]);
        }
    }
}
