using Agapanthe.Engine;

namespace Agapanthe.Tests;

/// <summary>
/// Job-1 D1/D3 — <see cref="SystemScheduler"/>'s greedy wave-grouping for <see cref="Stage.Simulation"/>: computed
/// once at the first <see cref="SystemScheduler.Tick"/>, cached, and inspectable via
/// <c>GetSimulationWavesForTest</c>. No threading is exercised here — that is
/// <c>SystemSchedulerParallelismTests</c> (JS-006/JS-007); this file only proves the grouping itself is correct.
/// </summary>
public sealed class SystemSchedulerWaveGroupingTests
{
    private sealed class DeclaringSystem(
        IReadOnlyList<Type>? reads = null,
        IReadOnlyList<Type>? writes = null,
        bool requiresExclusiveExecution = false,
        bool requiresOwnerThread = false) : ISystem
    {
        public IReadOnlyList<Type> Reads { get; } = reads ?? [];
        public IReadOnlyList<Type> Writes { get; } = writes ?? [];
        public bool RequiresExclusiveExecution { get; } = requiresExclusiveExecution;
        public bool RequiresOwnerThread { get; } = requiresOwnerThread;
        public void Execute(in TickContext ctx)
        {
        }
    }

    private sealed class ComponentA;
    private sealed class ComponentB;
    private sealed class ComponentC;
    private sealed class ComponentD;

    [Fact]
    public void TwoSystems_WithDisjointReadsAndWrites_ShareAWave()
    {
        using var scheduler = new SystemScheduler();
        var a = new DeclaringSystem(writes: [typeof(ComponentA)]);
        var b = new DeclaringSystem(writes: [typeof(ComponentB)]);
        scheduler.Add(Stage.Simulation, a);
        scheduler.Add(Stage.Simulation, b);

        scheduler.Tick(1f / 60f);

        var waves = scheduler.GetSimulationWavesForTest()!;
        Assert.Single(waves);
        Assert.Equal(new ISystem[] { a, b }, waves[0]);
    }

    [Fact]
    public void TwoSystems_WithOverlappingWrites_EndUpInDifferentWaves()
    {
        using var scheduler = new SystemScheduler();
        var a = new DeclaringSystem(writes: [typeof(ComponentA)]);
        var b = new DeclaringSystem(writes: [typeof(ComponentA)]);
        scheduler.Add(Stage.Simulation, a);
        scheduler.Add(Stage.Simulation, b);

        scheduler.Tick(1f / 60f);

        var waves = scheduler.GetSimulationWavesForTest()!;
        Assert.Equal(2, waves.Count);
        Assert.Equal([a], waves[0]);
        Assert.Equal([b], waves[1]);
    }

    [Fact]
    public void WriteReadOverlap_AlsoConflicts()
    {
        using var scheduler = new SystemScheduler();
        var writer = new DeclaringSystem(writes: [typeof(ComponentA)]);
        var reader = new DeclaringSystem(reads: [typeof(ComponentA)]);
        scheduler.Add(Stage.Simulation, writer);
        scheduler.Add(Stage.Simulation, reader);

        scheduler.Tick(1f / 60f);

        var waves = scheduler.GetSimulationWavesForTest()!;
        Assert.Equal(2, waves.Count);
    }

    [Fact]
    public void TwoSystems_WithOnlyOverlappingReads_ShareAWave()
    {
        using var scheduler = new SystemScheduler();
        var a = new DeclaringSystem(reads: [typeof(ComponentA)]);
        var b = new DeclaringSystem(reads: [typeof(ComponentA)]);
        scheduler.Add(Stage.Simulation, a);
        scheduler.Add(Stage.Simulation, b);

        scheduler.Tick(1f / 60f);

        var waves = scheduler.GetSimulationWavesForTest()!;
        Assert.Single(waves);
    }

    [Fact]
    public void OwnerPinnedAndWorkerEligibleSystems_StablePartitionWithinTheirSharedWave()
    {
        // Job-2b: 4 mutually disjoint (never-conflicting) systems registered in an INTERLEAVED order
        // (worker, pinned, worker, pinned) so a naive "just move pinned ones to the end" that didn't preserve
        // each sub-group's own relative order would be caught. Expected result: all 4 share one wave (no
        // Reads/Writes conflict), reordered to [a, b, c, d] — worker-eligible (a, b) first in THEIR original
        // relative order, owner-pinned (c, d) last in THEIR original relative order.
        using var scheduler = new SystemScheduler();
        var a = new DeclaringSystem(writes: [typeof(ComponentA)]);
        var c = new DeclaringSystem(writes: [typeof(ComponentC)], requiresOwnerThread: true);
        var b = new DeclaringSystem(writes: [typeof(ComponentB)]);
        var d = new DeclaringSystem(writes: [typeof(ComponentD)], requiresOwnerThread: true);
        scheduler.Add(Stage.Simulation, a);
        scheduler.Add(Stage.Simulation, c);
        scheduler.Add(Stage.Simulation, b);
        scheduler.Add(Stage.Simulation, d);

        scheduler.Tick(1f / 60f);

        var waves = scheduler.GetSimulationWavesForTest()!;
        var workerEligibleCounts = scheduler.GetSimulationWaveWorkerEligibleCountForTest()!;
        Assert.Single(waves);
        Assert.Equal(new ISystem[] { a, b, c, d }, waves[0]);
        Assert.Equal(2, workerEligibleCounts[0]);
    }

    [Fact]
    public void ExclusiveAndOwnerPinned_TogetherAreHarmlesslyRedundant_StillASoloWave()
    {
        // Job-2b audit F7: the one combination of the two flags no earlier test exercised. RequiresOwnerThread is
        // meaningless when RequiresExclusiveExecution is true (a solo wave already always runs inline on the owner
        // thread) — pins that this combination is not treated specially and does not break the solo-wave path.
        using var scheduler = new SystemScheduler();
        var exclusiveAndPinned = new DeclaringSystem(requiresExclusiveExecution: true, requiresOwnerThread: true);
        scheduler.Add(Stage.Simulation, exclusiveAndPinned);

        scheduler.Tick(1f / 60f);

        var waves = scheduler.GetSimulationWavesForTest()!;
        var workerEligibleCounts = scheduler.GetSimulationWaveWorkerEligibleCountForTest()!;
        Assert.Single(waves);
        Assert.Equal([exclusiveAndPinned], waves[0]);
        Assert.Equal(0, workerEligibleCounts[0]);
    }

    [Fact]
    public void RequiresExclusiveExecution_AlwaysGetsItsOwnSoloWave()
    {
        using var scheduler = new SystemScheduler();
        var exclusive = new DeclaringSystem(requiresExclusiveExecution: true);
        var harmless = new DeclaringSystem();
        scheduler.Add(Stage.Simulation, exclusive);
        scheduler.Add(Stage.Simulation, harmless);

        scheduler.Tick(1f / 60f);

        var waves = scheduler.GetSimulationWavesForTest()!;
        var exclusivity = scheduler.GetSimulationWaveExclusivityForTest()!;
        Assert.Equal(2, waves.Count);
        var exclusiveWaveIndex = waves.ToList().FindIndex(w => w.Contains(exclusive));
        Assert.Equal([exclusive], waves[exclusiveWaveIndex]);
        Assert.True(exclusivity[exclusiveWaveIndex]); // structural check, not inferred from wave size (Job-1 audit)
    }

    [Fact]
    public void RequiresExclusiveExecution_BlocksLaterSystemsFromJoiningItsWave()
    {
        using var scheduler = new SystemScheduler();
        var exclusive = new DeclaringSystem(requiresExclusiveExecution: true);
        var laterHarmless = new DeclaringSystem();
        scheduler.Add(Stage.Simulation, exclusive);
        scheduler.Add(Stage.Simulation, laterHarmless);

        scheduler.Tick(1f / 60f);

        var waves = scheduler.GetSimulationWavesForTest()!;
        var exclusiveWave = waves.First(w => w.Contains(exclusive));
        Assert.Single(exclusiveWave);
    }

    [Fact]
    public void WaveComputation_IsCachedAcrossTicks()
    {
        using var scheduler = new SystemScheduler();
        scheduler.Add(Stage.Simulation, new DeclaringSystem());

        scheduler.Tick(1f / 60f);
        var firstWaves = scheduler.GetSimulationWavesForTest();
        scheduler.Tick(1f / 60f);
        var secondWaves = scheduler.GetSimulationWavesForTest();

        Assert.Same(firstWaves, secondWaves);
    }

    [Fact]
    public void Add_AfterFirstTick_StillThrows_WaveComputationDoesNotBypassTheFreezeGuard()
    {
        using var scheduler = new SystemScheduler();
        scheduler.Add(Stage.Simulation, new DeclaringSystem());
        scheduler.Tick(1f / 60f);

        Assert.Throws<InvalidOperationException>(
            () => scheduler.Add(Stage.Simulation, new DeclaringSystem()));
    }

    [Fact]
    public void ThreeUnauditedSystems_AreEachSoloAndExclusive_RunSequentiallyInRegistrationOrder()
    {
        // RecordingDeclaringSystem overrides nothing, so it inherits the interface's default
        // RequiresExclusiveExecution == true (D6) — each of the three below gets its OWN solo wave, never sharing
        // one, which is why they are safe to have all write the SAME shared `log` list without a race. This is
        // NOT a test of concurrent-but-conflict-free execution (see SystemSchedulerParallelismTests for that) — it
        // pins that three unaudited systems stay fully sequential, in registration order, exactly as before Job-1.
        var log = new List<string>();
        using var scheduler = new SystemScheduler();
        scheduler.Add(Stage.Simulation, new RecordingDeclaringSystem(log, "a"));
        scheduler.Add(Stage.Simulation, new RecordingDeclaringSystem(log, "b"));
        scheduler.Add(Stage.Simulation, new RecordingDeclaringSystem(log, "c"));

        scheduler.Tick(1f / 60f);

        Assert.Equal(["a", "b", "c"], log);
        Assert.Equal(3, scheduler.GetSimulationWavesForTest()!.Count); // 3 solo waves, not 1 shared wave
        Assert.All(scheduler.GetSimulationWaveExclusivityForTest()!, Assert.True);
    }

    private sealed class RecordingDeclaringSystem(List<string> log, string name) : ISystem
    {
        public void Execute(in TickContext ctx) => log.Add(name);
    }
}
