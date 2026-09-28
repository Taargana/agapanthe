using Agapanthe.Engine;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0c — <see cref="PhysicsSystem"/> steps by a fixed <c>PhysicsSettings.FixedDt</c> and ignores
/// <c>TickContext.DeltaSeconds</c> (spec decision 3), but the two must agree because the fixed-step accumulator is
/// what guarantees it. <see cref="PhysicsSystem.Execute"/> asserts that with <see cref="PhysicsSystem.RatesMatch"/>;
/// a failed <c>Debug.Assert</c> terminates the test host, so the predicate is tested directly here (R3).
/// </summary>
[Collection("World")]
public sealed class PhysicsSystemTests
{
    [Fact]
    public void RatesMatch_IsTrue_WhenTheTickDeltaEqualsTheFixedStep()
        => Assert.True(PhysicsSystem.RatesMatch(1f / 60f, 1f / 60f));

    [Fact]
    public void RatesMatch_IsFalse_WhenTheAccumulatorAndPhysicsRatesDrift()
        => Assert.False(PhysicsSystem.RatesMatch(1f / 30f, 1f / 60f));

    [Fact]
    public void RequiresExclusiveExecutionIsFalse_AndRequiresOwnerThreadIsTrue()
    {
        // Job-2b: PhysicsSystem now declares real Reads/Writes, so it no longer needs to monopolize its whole wave
        // (RequiresExclusiveExecution == false) — but GameWorld.StepPhysics's broadphase scratch is still invisible
        // to Reads/Writes and protected purely by AssertOwnerThreadStrict, so it must still always run on the
        // scheduler's owner thread (RequiresOwnerThread == true), never a worker.
        using var world = new GameWorld();
        var system = new PhysicsSystem(world, PhysicsSettings.Default(groundY: 0f));

        Assert.False(system.RequiresExclusiveExecution);
        Assert.True(system.RequiresOwnerThread);
    }

    [Fact]
    public void ReadsAndWrites_MatchExactlyWhatStepPhysicsTouches()
    {
        // Job-2b: pins the exact component sets derived from reading GameWorld.Physics.cs in full — GlobalId and
        // RigidBody are read but never written there; InstanceSlot is read (to feed MarkDirty); WorldPosition and
        // Velocity are the only two .Set<T>() calls in the whole file.
        using var world = new GameWorld();
        var system = new PhysicsSystem(world, PhysicsSettings.Default(groundY: 0f));

        Assert.Equal(
            new[] { typeof(GlobalId), typeof(RigidBody), typeof(InstanceSlot) },
            system.Reads);
        Assert.Equal(new[] { typeof(WorldPosition), typeof(Velocity) }, system.Writes);
    }
}
