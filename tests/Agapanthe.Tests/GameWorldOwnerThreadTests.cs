using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// Job-1 D2/F1 (audit) — <see cref="GameWorld.SetSanctionedWorkerThreads"/> only widens the READ-ONLY guard
/// (<c>AssertOwnerThread</c>, exercised here via <see cref="GameWorld.IsAlive"/>). Every structural mutator uses
/// <c>AssertOwnerThreadStrict</c> instead, which a sanctioned worker never passes — <see cref="GameWorld.Spawn"/>
/// pins that half of the contract. Both audits found the original version of this file exercised the DANGEROUS
/// case (`Spawn` from a worker not throwing) as if it were the intended guarantee; it is the opposite.
/// </summary>
[Collection("World")]
public sealed class GameWorldOwnerThreadTests
{
    [Fact]
    public void IsAlive_FromASanctionedWorkerThread_DoesNotThrow()
    {
        using var world = new GameWorld();
        var thrown = RunOnAnotherThread(
            () => world.IsAlive(default),
            sanctionThisThread: world.SetSanctionedWorkerThreads);

        Assert.Null(thrown);
    }

    [Fact]
    public void IsAlive_FromAnUnsanctionedThread_StillThrows_EvenWithOtherThreadsSanctioned()
    {
        var world = new GameWorld();
        world.SetSanctionedWorkerThreads([-1]); // never equals Environment.CurrentManagedThreadId

        var thrown = RunOnAnotherThread(() => world.IsAlive(default));
        Assert.IsType<InvalidOperationException>(thrown);
    }

    [Fact]
    public void Spawn_FromASanctionedWorkerThread_StillThrows()
    {
        // Job-1 F1: Spawn mutates GameWorld's structural command queue (_pendingSpawn/_commands), which is shared,
        // mutable state invisible to ISystem.Reads/Writes — exactly the class of hazard AssertOwnerThreadStrict
        // exists to protect, and SetSanctionedWorkerThreads must never widen it.
        using var world = new GameWorld();
        var thrown = RunOnAnotherThread(
            () => world.Spawn(Double3.Zero, Quaternion.Identity, 1f),
            sanctionThisThread: world.SetSanctionedWorkerThreads);

        Assert.IsType<InvalidOperationException>(thrown);
    }

    [Fact]
    public void SetSanctionedWorkerThreads_CalledTwice_UnionsRatherThanReplaces()
    {
        // Two independent, still-alive "worker" threads, sanctioned one at a time via two SEPARATE calls — the
        // second call must not un-sanction the first pool's thread (Job-1 F2: two SimulationHosts could plausibly
        // share one borrowed GameWorld, each lazily creating its own pool).
        using var world = new GameWorld();
        using var poolAReady = new SemaphoreSlim(0, 1);
        using var poolAProceed = new SemaphoreSlim(0, 1);
        Exception? poolAResult = null;

        var poolAThread = new Thread(() =>
        {
            world.SetSanctionedWorkerThreads([Environment.CurrentManagedThreadId]);
            poolAReady.Release();
            poolAProceed.Wait();
            try
            {
                world.IsAlive(default);
            }
            catch (Exception ex)
            {
                poolAResult = ex;
            }
        })
        { IsBackground = true };
        poolAThread.Start();
        poolAReady.Wait();

        // Pool B sanctions a DIFFERENT thread after pool A already sanctioned its own.
        var thrownForB = RunOnAnotherThread(
            () => world.IsAlive(default),
            sanctionThisThread: world.SetSanctionedWorkerThreads);
        Assert.Null(thrownForB);

        // Pool A's thread must STILL be sanctioned after pool B's call.
        poolAProceed.Release();
        poolAThread.Join();
        Assert.Null(poolAResult);
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
