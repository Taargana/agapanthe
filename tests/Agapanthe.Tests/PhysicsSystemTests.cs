using Agapanthe.Engine;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0c — <see cref="PhysicsSystem"/> steps by a fixed <c>PhysicsSettings.FixedDt</c> and ignores
/// <c>TickContext.DeltaSeconds</c> (spec decision 3), but the two must agree because the fixed-step accumulator is
/// what guarantees it. <see cref="PhysicsSystem.Execute"/> asserts that with <see cref="PhysicsSystem.RatesMatch"/>;
/// a failed <c>Debug.Assert</c> terminates the test host, so the predicate is tested directly here (R3).
/// </summary>
public sealed class PhysicsSystemTests
{
    [Fact]
    public void RatesMatch_IsTrue_WhenTheTickDeltaEqualsTheFixedStep()
        => Assert.True(PhysicsSystem.RatesMatch(1f / 60f, 1f / 60f));

    [Fact]
    public void RatesMatch_IsFalse_WhenTheAccumulatorAndPhysicsRatesDrift()
        => Assert.False(PhysicsSystem.RatesMatch(1f / 30f, 1f / 60f));
}
