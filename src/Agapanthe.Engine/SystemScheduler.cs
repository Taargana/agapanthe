using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Agapanthe.Engine;

/// <summary>
/// Runs the registered simulation systems, stage by stage, in a guaranteed order (P3-M2, decision D1). This type is
/// the tick order — the invariant that used to live in an application's closure, where nothing protected it.
/// <para>
/// <b>Simulation and rendering are ticked separately, and that is not cosmetic.</b> The renderer skips a frame
/// whenever the swapchain is out of date (a resize, a minimize): <c>FrameRenderer.DrawFrame</c> silently returns
/// without invoking its callback. Had the stages been driven from inside that callback, every window resize would
/// have skipped the whole simulation — the delta time lost, the deferred despawns stuck in the queue. So:
/// <see cref="Tick"/> always runs (Input → Simulation → PostSimulation), and the render stage — now owned by
/// <c>RenderSystemScheduler</c> in <c>Agapanthe.Engine.Render</c> (MP-0a) — runs only when there is a frame to draw.
/// </para>
/// <para>
/// <b>Zero-alloc.</b> One list per stage, allocated at registration; iteration by index; the structural barrier is a
/// delegate captured once at construction. Nothing here allocates per tick.
/// </para>
/// <para>
/// <b><see cref="Stage.Simulation"/> runs in parallel waves (Job-1).</b> At the first <see cref="Tick"/>, registered
/// <see cref="Stage.Simulation"/> systems are greedily grouped into waves by their declared
/// <see cref="ISystem.Reads"/>/<see cref="ISystem.Writes"/> — systems in the same wave share no conflict and may
/// run on different threads at once; a system with <see cref="ISystem.RequiresExclusiveExecution"/> (the default)
/// always gets a solo wave. Within a shared wave, a member may additionally declare
/// <see cref="ISystem.RequiresOwnerThread"/> (Job-2b) to keep its OWN execution pinned to the owner thread while
/// the rest of the wave still runs on workers — thread affinity, orthogonal to resource exclusivity. Waves
/// themselves execute strictly one after another (a full join between them, and before the stage's structural
/// barrier), on a persistent worker-thread pool created once and reused every tick — never
/// <c>Task.Run</c>/<c>Parallel.Invoke</c>, which would allocate per call. <see cref="Stage.Input"/> and
/// <see cref="Stage.PostSimulation"/> are untouched by any of this and stay fully sequential.
/// </para>
/// </summary>
public sealed class SystemScheduler : IDisposable
{
    // Input / Simulation / PostSimulation. Render is not here at all since MP-0a: IRenderSystem carries GPU types,
    // so it lives in a different assembly entirely — the disjointness is now enforced by the build graph.
    private const int TickStageCount = 3;

    private readonly List<ISystem>[] _stages;

    // Job-1 audit F8: SystemScheduler is now the only engine type that owns threads, yet had no owner-thread anchor
    // of its own — GameWorld and SimCommandQueue both do. Debug-only, [Conditional]'d away in Release, mirrors their
    // exact pattern (AssertOwnerThreadOnCaller, below).
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

    // Applies the deferred structural changes (spawns, despawns, reparenting) at the end of every stage: a system must
    // never see the entity storage move under its own iteration. Held as a field so calling it costs no allocation;
    // null in tests that have no world.
    private readonly Action? _barrier;

    private bool _frozen;
    private long _tickIndex;

    // Job-1 D1/D3 — computed ONCE, at the _frozen transition, and reused every tick (0-alloc after warmup). Each
    // inner list is a "wave": systems in the same wave declare no Reads/Writes conflict against each other and may
    // run concurrently (Job-1 D5 adds the actual worker dispatch on top of this grouping — this type only decides
    // WHO may run together, never HOW). Built once from Stage.Simulation's registered systems only; Stage.Input and
    // Stage.PostSimulation are untouched and keep the flat sequential loop below.
    private List<List<ISystem>>? _simulationWaves;

    // A wave that holds a RequiresExclusiveExecution == true system is exclusive: it holds exactly that one system,
    // and no other system may ever be placed into it. Parallel to _simulationWaves by index. Exposed only via
    // GetSimulationWaveExclusivityForTest() — the algorithm itself never reads it back (BuildSimulationWaves uses
    // its own local `exclusiveFlags`), it exists so a test can assert exclusivity structurally instead of inferring
    // it from wave size (a wave of size 1 is not necessarily exclusive — it could just be the only non-conflicting
    // placement found).
    private List<bool>? _simulationWaveExclusive;

    // Job-2b: how many of each wave's LEADING entries are worker-eligible (ISystem.RequiresOwnerThread == false) —
    // BuildSimulationWaves stable-partitions each wave's list in place so those entries are contiguous at the
    // front, with any owner-pinned entries (RequiresOwnerThread == true) contiguous at the back. Parallel to
    // _simulationWaves by index — unlike _simulationWaveExclusive (structural-test-only, the grouping algorithm
    // never reads it back), THIS field IS read every tick, by RunSimulationWaves, to decide each wave's execution
    // branch. Also exposed read-only via GetSimulationWaveWorkerEligibleCountForTest() for structural assertions.
    // A stable partition cannot change correctness: two systems in the same wave are conflict-free by construction
    // (Conflicts/ConflictsWithAny), so their relative execution order was never guaranteed to matter — only each
    // sub-group's OWN relative order is preserved.
    private List<int>? _simulationWaveWorkerEligibleCount;

    // The largest worker-eligible count across every wave BuildSimulationWaves produced (Job-2b audit F3: NOT the
    // largest wave.Count — an owner-pinned member never consumes a worker slot) — the most concurrency this
    // scheduler's Stage.Simulation could ever actually use. Caps the worker pool below at this instead of a flat
    // ProcessorCount-1, so a 2-system wave on a 32-core machine spins up 1 worker, not 31 that would never all be
    // used (Job-1 audit F6).
    private int _maxSimulationWaveWidth = 1;

    // Job-1 D5: called ONCE, the first time a wave with more than one system actually needs to run concurrently —
    // never at construction, never for a scheduler whose Stage.Simulation never needs more than one worker (today's
    // overwhelmingly common case: at most one Stage.Simulation system exists anywhere in this codebase). This is a
    // deliberate refinement on top of the spec's "created once at pool creation": spinning up ProcessorCount-1
    // background threads for every SystemScheduler instance — including the hundreds a test run constructs — would
    // leak live threads across the whole test suite for zero benefit. Left null, sanctioning never happens, which is
    // bit-for-bit today's behavior.
    private readonly Action<IReadOnlyCollection<int>>? _onWorkerThreadsSanctioned;

    // --- Job-1 D5: persistent worker pool, created lazily (see _onWorkerThreadsSanctioned above) -------------------
    private Thread[]? _workers;
    private SemaphoreSlim[]? _workerGoSignals;
    private WorkerJob[]? _workerJobs;
    private Exception?[]? _workerExceptions;
    private CountdownEvent? _waveCountdown;

    // Cumulative bytes GC.GetAllocatedBytesForCurrentThread() attributes to each worker's OWN dispatch loop (job
    // lookup + the systems it ran), bracketed once per wave. GC.GetAllocatedBytesForCurrentThread() can only ever
    // report the CALLING thread's own count — there is no way to query another thread's allocations from outside
    // it — so this is the only way a test can prove a worker thread allocates nothing after warmup, mirroring every
    // other 0-alloc gate in this codebase (e.g. FrameStats' per-frame bracket), just measured on a worker instead of
    // the owner thread.
    private long[]? _workerAllocatedBytesForTest;

    // Written once per wave dispatch, BEFORE any _workerGoSignals[i].Release() call, and read by a worker only
    // AFTER its own Wait() returns — SemaphoreSlim's release/wait pair establishes the happens-before relationship
    // that makes this plain (non-volatile) field safe to share, exactly like the barrier established by a lock.
    private TickContext _workerCtx;

    // Set once, by Dispose(), and observed by every worker right after its own Wait() returns — the same
    // release/wait happens-before relationship as _workerCtx justifies not making this volatile.
    private bool _disposeRequested;
    private bool _disposed;

    private readonly struct WorkerJob(List<ISystem> wave, int startIndex, int count)
    {
        public readonly List<ISystem> Wave = wave;
        public readonly int StartIndex = startIndex;
        public readonly int Count = count;
    }

    public SystemScheduler(Action? structuralBarrier = null, Action<IReadOnlyCollection<int>>? onWorkerThreadsSanctioned = null)
    {
        _barrier = structuralBarrier;
        _onWorkerThreadsSanctioned = onWorkerThreadsSanctioned;
        _stages = new List<ISystem>[TickStageCount];
        for (var i = 0; i < TickStageCount; i++)
        {
            _stages[i] = new List<ISystem>();
        }
    }

    /// <summary>Ticks run so far. A tick is not a frame: a frame can be skipped, a tick never is.</summary>
    public long TickIndex => _tickIndex;

    /// <summary>
    /// Registers a simulation system. Systems in the same stage run in <b>registration order</b> — that is a
    /// guarantee, not an accident of the container.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="stage"/> is <see cref="Stage.Render"/> — render systems
    /// are registered on <c>RenderSystemScheduler</c> (<c>Agapanthe.Engine.Render</c>); they receive a context
    /// carrying GPU handles that this assembly cannot even name.</exception>
    /// <exception cref="InvalidOperationException">The scheduler has already run: see <see cref="Add(Stage, ISystem)"/>
    /// remarks.</exception>
    /// <remarks>
    /// Registration is <b>frozen after the first tick</b>. Adding a system mid-run would mutate a list while it is
    /// being iterated — the class of bug one does not want to go looking for at 2 a.m. Compose the schedule up front;
    /// systems that come and go are a data problem (an enabled flag), not a scheduling one.
    /// </remarks>
    public void Add(Stage stage, ISystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        ThrowIfFrozen();
        ThrowIfRenderStage(stage);

        _stages[(int)stage].Add(system);
    }

    /// <summary>Simulation systems registered in <paramref name="stage"/> (diagnostics and tests).</summary>
    /// <exception cref="ArgumentException"><paramref name="stage"/> is <see cref="Stage.Render"/> — this scheduler
    /// holds no render systems; ask <c>RenderSystemScheduler.Count</c>. Throwing rather than returning 0 keeps the
    /// answer from being a plausible-looking lie.</exception>
    public int CountIn(Stage stage)
    {
        ThrowIfRenderStage(stage);
        return _stages[(int)stage].Count;
    }

    private static void ThrowIfRenderStage(Stage stage)
    {
        if (stage == Stage.Render)
        {
            throw new ArgumentException(
                "Stage.Render is not handled by SystemScheduler: render systems receive a RenderContext with GPU "
                + "handles and live on RenderSystemScheduler (Agapanthe.Engine.Render).",
                nameof(stage));
        }
    }

    /// <summary>
    /// Runs Input → Simulation → PostSimulation, each closed by a structural barrier. <b>Always</b> runs, even on a
    /// frame the renderer will skip — the simulation does not stop because the window is being resized.
    /// </summary>
    public void Tick(float deltaSeconds)
    {
        AssertOwnerThread();

        if (!_frozen)
        {
            _frozen = true;
            BuildSimulationWaves();
        }

        var ctx = new TickContext(deltaSeconds, _tickIndex);

        for (var stage = 0; stage < TickStageCount; stage++)
        {
            if ((Stage)stage == Stage.Simulation)
            {
                RunSimulationWaves(in ctx);
            }
            else
            {
                var systems = _stages[stage];
                for (var i = 0; i < systems.Count; i++)
                {
                    systems[i].Execute(in ctx);
                }
            }

            _barrier?.Invoke(); // end-of-stage structural barrier
        }

        _tickIndex++;
    }

    // Job-1 D5 / Job-2b: runs the precomputed waves in order, full join between waves. A wave with no owner-pinned
    // members (ISystem.RequiresOwnerThread == false for every entry — today's overwhelmingly common case) behaves
    // exactly as before Job-2b: wave.Count <= 1 executes inline on the calling thread with no worker pool ever
    // created, otherwise the whole wave dispatches to the persistent worker pool. A wave that is ENTIRELY
    // owner-pinned (workerEligibleCount == 0) runs sequentially inline, even for 2+ systems — the worker pool is
    // never touched. A MIXED wave forks: the worker-eligible prefix dispatches to workers exactly as
    // RunWaveInParallel always has, while the owner-pinned suffix runs inline on the calling thread, overlapping
    // the workers rather than waiting for them first.
    private void RunSimulationWaves(in TickContext ctx)
    {
        var waves = _simulationWaves!;
        var workerEligibleCounts = _simulationWaveWorkerEligibleCount!;
        for (var w = 0; w < waves.Count; w++)
        {
            var wave = waves[w];
            var workerEligibleCount = workerEligibleCounts[w];

            if (workerEligibleCount == wave.Count)
            {
                if (wave.Count <= 1)
                {
                    if (wave.Count == 1)
                    {
                        wave[0].Execute(in ctx);
                    }

                    continue;
                }

                RunWaveInParallel(wave, workerEligibleCount, in ctx);
                continue;
            }

            if (workerEligibleCount == 0)
            {
                // Entirely owner-pinned: sequential, inline, on the calling thread — the worker pool is never
                // created for this wave. Propagation is direct (no try/catch): a single thread is involved, so
                // there is no concurrent exception to merge, exactly like today's wave.Count == 1 case.
                for (var i = 0; i < wave.Count; i++)
                {
                    wave[i].Execute(in ctx);
                }

                continue;
            }

            RunWaveInParallel(wave, workerEligibleCount, in ctx);
        }
    }

    // workerEligibleCount == wave.Count: identical to Job-1's original behavior (the whole wave dispatches to
    // workers). workerEligibleCount < wave.Count (Job-2b): only the worker-eligible PREFIX [0, workerEligibleCount)
    // is partitioned across the worker pool — the stable partition BuildSimulationWaves already performed
    // guarantees that prefix is contiguous and holds exactly the worker-eligible members. The owner-pinned suffix
    // [workerEligibleCount, wave.Count) then runs inline on the calling thread, released to run IMMEDIATELY after
    // signaling the workers (never before) so the two genuinely overlap rather than merely being sequenced.
    private void RunWaveInParallel(List<ISystem> wave, int workerEligibleCount, in TickContext ctx)
    {
        EnsureWorkerPool();

        var active = Math.Min(workerEligibleCount, _workers!.Length);
        _workerCtx = ctx;
        _waveCountdown!.Reset(active);

        var baseSize = workerEligibleCount / active;
        var remainder = workerEligibleCount % active;
        var start = 0;
        for (var i = 0; i < active; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            _workerJobs![i] = new WorkerJob(wave, start, size);
            start += size;
        }

        for (var i = 0; i < active; i++)
        {
            _workerGoSignals![i].Release();
        }

        // Job-2b: the owner-pinned suffix runs HERE, on the calling thread, while the workers released above are
        // already running — real overlap, not owner-then-worker or worker-then-owner sequencing. On exception,
        // stop this loop immediately (mirrors WorkerLoop's own catch: remaining pinned members are skipped for
        // this tick, not run) and fold the exception into the same `errors` collection the workers report into
        // below — collected sequentially on this single thread, so there is no concurrent writer to race with.
        List<Exception>? errors = null;
        for (var i = workerEligibleCount; i < wave.Count; i++)
        {
            try
            {
                wave[i].Execute(in ctx);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
                break;
            }
        }

        WaitForWaveOrDiagnoseHang(active);

        // Never let a system's exception vanish on a worker thread (audit posture: silence there is worse than a
        // thrown exception here) — rethrow on the calling thread, preserving the original stack via
        // ExceptionDispatchInfo, once every worker in this wave has finished.
        for (var i = 0; i < active; i++)
        {
            var ex = _workerExceptions![i];
            if (ex is null)
            {
                continue;
            }

            _workerExceptions[i] = null;
            (errors ??= []).Add(ex);
        }

        if (errors is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }
        else if (errors is { Count: > 1 })
        {
            throw new AggregateException(
                $"{errors.Count} systems threw while running in the same Stage.Simulation wave.", errors);
        }
    }

    // WorkerLoop's outer catch (see its remarks) makes an unbounded Wait() here safe against a worker that silently
    // died, but a genuine deadlock (a system's Execute itself hangs) would otherwise still be indistinguishable
    // from a slow frame — a frozen window with no diagnostic. In Debug only, time out generously and throw a named
    // exception instead; Release keeps the unbounded Wait() (a timeout there would silently proceed with an
    // incomplete wave — reading partially-written results — which is worse than hanging).
    private void WaitForWaveOrDiagnoseHang(int active)
    {
#if DEBUG
        if (!_waveCountdown!.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException(
                $"A Job-1 Stage.Simulation wave of {active} system(s) did not finish within 30s — a system's " +
                "Execute is likely hung. See SystemScheduler.WorkerLoop's outer catch for the other failure mode " +
                "this guards against (a worker thread dying silently).");
        }
#else
        _waveCountdown!.Wait();
#endif
    }

    // Job-1 D5: created once, the first time a wave actually needs more than one concurrent worker (see
    // _onWorkerThreadsSanctioned's remarks). Not Task.Run/Parallel.Invoke — those allocate a Task per call, which
    // would violate the project's locked 0-alloc-per-frame gate. A dedicated, persistent thread pool with low-level
    // semaphore signaling instead: nothing here allocates once warmed up.
    private void EnsureWorkerPool()
    {
        if (_workers is not null)
        {
            return;
        }

        var workerCount = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, _maxSimulationWaveWidth));
        _workers = new Thread[workerCount];
        _workerGoSignals = new SemaphoreSlim[workerCount];
        _workerJobs = new WorkerJob[workerCount];
        _workerExceptions = new Exception?[workerCount];
        _workerAllocatedBytesForTest = new long[workerCount];
        _waveCountdown = new CountdownEvent(1);

        for (var i = 0; i < workerCount; i++)
        {
            _workerGoSignals[i] = new SemaphoreSlim(0, 1);
            var workerIndex = i;
            // IsBackground = true is still a deliberate belt-and-braces choice even with Dispose() below: a
            // SystemScheduler an owner forgets to dispose (e.g. a test that throws before reaching a `using`'s
            // Dispose) still never blocks process/test-host exit.
            var thread = new Thread(() => WorkerLoop(workerIndex))
            {
                IsBackground = true,
                Name = $"Agapanthe.Job1.Worker{workerIndex}",
            };
            _workers[i] = thread;
            thread.Start();
        }

        var ids = new int[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            ids[i] = _workers[i].ManagedThreadId;
        }

        _onWorkerThreadsSanctioned?.Invoke(ids);
    }

    private void WorkerLoop(int workerIndex)
    {
        var go = _workerGoSignals![workerIndex];
        while (true)
        {
            try
            {
                go.Wait();

                // Dispose() releases every worker's semaphore to wake it for exactly this check — see
                // _disposeRequested's remarks for why reading it here, right after Wait() returns, is safe without
                // a volatile field.
                if (_disposeRequested)
                {
                    _waveCountdown!.Signal();
                    return;
                }

                var allocBefore = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    var job = _workerJobs![workerIndex];
                    for (var i = 0; i < job.Count; i++)
                    {
                        job.Wave[job.StartIndex + i].Execute(in _workerCtx);
                    }
                }
                catch (Exception ex)
                {
                    _workerExceptions![workerIndex] = ex;
                }
                finally
                {
                    _workerAllocatedBytesForTest![workerIndex] += GC.GetAllocatedBytesForCurrentThread() - allocBefore;
                    _waveCountdown!.Signal();
                }
            }
            catch (Exception outer)
            {
                // Something OUTSIDE a system's own Execute call failed (a disposed semaphore/countdown, an
                // out-of-band exception from Wait() itself) — the audit-flagged hazard this catch closes: without
                // it, this worker thread would die silently and the owner's Wait() below would block forever, with
                // no diagnostic, indistinguishable from a GPU hang. Best-effort: still try to unblock the owner,
                // record the failure so it surfaces through the normal exception-propagation path if possible, then
                // let this worker end rather than spin on whatever broke.
                _workerExceptions![workerIndex] = outer;
                try
                {
                    _waveCountdown?.Signal();
                }
                catch
                {
                    // The countdown itself may be the thing that is broken — nothing more can be done from here.
                }

                return;
            }
        }
    }

    /// <summary>
    /// Shuts down the persistent worker pool (a no-op if one was never created — see <see cref="EnsureWorkerPool"/>).
    /// Wakes every worker, joins each thread, then disposes the semaphores and the countdown event. Safe to call
    /// more than once. <see cref="SimulationHost"/> does not yet call this for its own scheduler — a persistent pool
    /// tied to the application's own process lifetime needs no explicit teardown there today; this exists so a
    /// short-lived owner (a unit test that forces a multi-system wave) can avoid leaking threads and kernel wait
    /// handles past its own lifetime, matching the project's "IDisposable partout" convention.
    /// </summary>
    public void Dispose()
    {
        if (_disposed || _workers is null)
        {
            _disposed = true;
            return;
        }

        _disposed = true;
        _disposeRequested = true;

        var countdown = _waveCountdown!;
        countdown.Reset(_workers.Length);
        for (var i = 0; i < _workers.Length; i++)
        {
            _workerGoSignals![i].Release();
        }

        countdown.Wait();

        for (var i = 0; i < _workers.Length; i++)
        {
            _workers[i].Join();
            _workerGoSignals![i].Dispose();
        }

        countdown.Dispose();
    }

    // Greedy earliest-non-conflicting-wave placement, in registration order (Job-1 D1/D3). A system with
    // RequiresExclusiveExecution == true always starts a brand-new, marked-exclusive wave — it can never join an
    // existing wave, and nothing can ever join its wave afterward (checked by the "wave is exclusive" skip below).
    private void BuildSimulationWaves()
    {
        var systems = _stages[(int)Stage.Simulation];
        var waves = new List<List<ISystem>>();
        var exclusiveFlags = new List<bool>();

        for (var i = 0; i < systems.Count; i++)
        {
            var candidate = systems[i];

            if (candidate.RequiresExclusiveExecution)
            {
                waves.Add([candidate]);
                exclusiveFlags.Add(true);
                continue;
            }

            var placed = false;
            for (var w = 0; w < waves.Count; w++)
            {
                if (exclusiveFlags[w])
                {
                    continue;
                }

                if (!ConflictsWithAny(candidate, waves[w]))
                {
                    waves[w].Add(candidate);
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                waves.Add([candidate]);
                exclusiveFlags.Add(false);
            }
        }

        // Job-2b: stable-partition each wave's list in place — worker-eligible members first, owner-pinned members
        // last — so RunWaveInParallel can dispatch a contiguous PREFIX to workers via the existing
        // WorkerJob(wave, start, size) shape, with no new per-tick allocation. Stable, so each sub-group keeps its
        // own relative registration order; only the group BOUNDARY moves. Done ONCE here (not per tick): every
        // ISystem predating Job-2b inherits RequiresOwnerThread's false default, so this is a strict no-op for any
        // wave built before this member existed — nothing to move, workerEligibleCount == wave.Count always.
        var workerEligibleCounts = new List<int>(waves.Count);
        for (var w = 0; w < waves.Count; w++)
        {
            var wave = waves[w];
            var ownerPinned = new List<ISystem>();
            var writeIndex = 0;
            for (var i = 0; i < wave.Count; i++)
            {
                if (wave[i].RequiresOwnerThread)
                {
                    ownerPinned.Add(wave[i]);
                }
                else
                {
                    wave[writeIndex] = wave[i];
                    writeIndex++;
                }
            }

            for (var i = 0; i < ownerPinned.Count; i++)
            {
                wave[writeIndex] = ownerPinned[i];
                writeIndex++;
            }

            workerEligibleCounts.Add(wave.Count - ownerPinned.Count);
        }

        _simulationWaves = waves;
        _simulationWaveExclusive = exclusiveFlags;
        _simulationWaveWorkerEligibleCount = workerEligibleCounts;

        // Job-2b audit F3: sized on workerEligibleCounts, not waves[w].Count — an owner-pinned member never
        // consumes a worker slot (RunWaveInParallel dispatches only the worker-eligible PREFIX), so counting it
        // here would over-provision the pool by exactly the number of pinned members in the widest wave. A fully
        // pinned wave (workerEligibleCount == 0) never creates a pool at all regardless of this value.
        var maxWidth = 1;
        for (var w = 0; w < workerEligibleCounts.Count; w++)
        {
            if (workerEligibleCounts[w] > maxWidth)
            {
                maxWidth = workerEligibleCounts[w];
            }
        }

        _maxSimulationWaveWidth = maxWidth;
    }

    private static bool ConflictsWithAny(ISystem candidate, List<ISystem> wave)
    {
        for (var i = 0; i < wave.Count; i++)
        {
            if (Conflicts(candidate, wave[i]))
            {
                return true;
            }
        }

        return false;
    }

    // Two systems conflict when either writes something the other reads or writes (Job-1 D3): Writes∩Writes,
    // Writes∩Reads, or Reads∩Writes. Reads∩Reads alone never conflicts.
    private static bool Conflicts(ISystem a, ISystem b)
    {
        return Intersects(a.Writes, b.Writes) || Intersects(a.Writes, b.Reads) || Intersects(a.Reads, b.Writes);
    }

    private static bool Intersects(IReadOnlyList<Type> left, IReadOnlyList<Type> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            for (var j = 0; j < right.Count; j++)
            {
                if (left[i] == right[j])
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Test/inspection accessor: the computed <see cref="Stage.Simulation"/> wave grouping, one inner list
    /// per wave, in the order systems will execute — <see langword="null"/> before the first <see cref="Tick"/>.
    /// </summary>
    internal IReadOnlyList<IReadOnlyList<ISystem>>? GetSimulationWavesForTest() => _simulationWaves;

    /// <summary>Test/inspection accessor: parallel to <see cref="GetSimulationWavesForTest"/> — whether each wave
    /// holds a <see cref="ISystem.RequiresExclusiveExecution"/> system, structurally rather than inferred from
    /// wave size.</summary>
    internal IReadOnlyList<bool>? GetSimulationWaveExclusivityForTest() => _simulationWaveExclusive;

    /// <summary>Test/inspection accessor (Job-2b): parallel to <see cref="GetSimulationWavesForTest"/> — how many of
    /// each wave's LEADING entries are worker-eligible (<see cref="ISystem.RequiresOwnerThread"/> == <c>false</c>);
    /// the remainder, at the end of that same wave's list, are owner-pinned.</summary>
    internal IReadOnlyList<int>? GetSimulationWaveWorkerEligibleCountForTest() => _simulationWaveWorkerEligibleCount;

    /// <summary>Test-only accessor: cumulative bytes each worker thread has allocated across every wave it has run
    /// (see <see cref="_workerAllocatedBytesForTest"/>) — <see langword="null"/> if no worker pool was ever created
    /// (no <see cref="Stage.Simulation"/> wave has ever needed more than one concurrent system).</summary>
    internal long[]? GetWorkerAllocatedBytesForTest() => _workerAllocatedBytesForTest;

    /// <summary>Debug-only guard, mirroring <c>GameWorld.AssertOwnerThreadStrict</c>: <see cref="Tick"/> must run
    /// on the thread that constructed this scheduler — never a Job-1 worker thread, which only ever calls
    /// <see cref="ISystem.Execute"/> via <see cref="WorkerLoop"/>, never <see cref="Tick"/> itself.</summary>
    [Conditional("DEBUG")]
    private void AssertOwnerThread([CallerMemberName] string caller = "")
    {
        var callingThreadId = Environment.CurrentManagedThreadId;
        if (callingThreadId == _ownerThreadId)
        {
            return;
        }

        throw new InvalidOperationException(
            $"SystemScheduler.{caller} was called from thread {callingThreadId}, but the scheduler is owned by " +
            $"thread {_ownerThreadId}. Only ISystem.Execute may run on a Job-1 worker thread — the scheduler " +
            "itself is driven from one owner thread, exactly like GameWorld and SimCommandQueue.");
    }

    private void ThrowIfFrozen()
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                "Systems cannot be registered after the scheduler has run: it would mutate a stage's list while that " +
                "list is being iterated. Compose the schedule before the first tick.");
        }
    }
}
