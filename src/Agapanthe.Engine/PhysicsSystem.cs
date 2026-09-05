using System.Diagnostics;
using Agapanthe.World;

namespace Agapanthe.Engine;

/// <summary>
/// The engine's physics system (P3-M3): one <see cref="GameWorld.StepPhysics"/> per tick, at a FIXED step, in
/// <see cref="Stage.Simulation"/> — so <see cref="Stage.PostSimulation"/> re-derives world transforms and bounds
/// from the positions it wrote. Opt-in: the application registers it (it is NOT part of
/// <see cref="FrameOrchestrator.CreateDefault"/>), so a non-physics frame is unchanged and its captures stay
/// byte-identical.
/// </summary>
/// <remarks>
/// It borrows the world and holds the immutable <see cref="PhysicsSettings"/>; it owns nothing and disposes
/// nothing, like every other system. It still steps by a FIXED <see cref="PhysicsSettings.FixedDt"/> rather than by
/// <see cref="TickContext.DeltaSeconds"/> (spec decision 3) — but the two must now agree, because the MP-0c
/// fixed-step accumulator is what guarantees a tick is always exactly one fixed step. <see cref="Execute"/> checks
/// that with <see cref="RatesMatch"/>.
/// </remarks>
public sealed class PhysicsSystem : ISystem
{
    private readonly GameWorld _world;
    private readonly PhysicsSettings _settings;

    public PhysicsSystem(GameWorld world, in PhysicsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        _settings = settings;
    }

    /// <summary>
    /// Whether the tick's delta and the physics fixed step agree. Extracted so a unit test can exercise it
    /// directly: a failed <see cref="Debug.Assert"/> goes through <c>DebugProvider.FailCore</c> and terminates the
    /// test host rather than raising a catchable exception (MP-0c R3), so the assert below cannot be the test's
    /// subject. The assert stays as the runtime guard.
    /// </summary>
    internal static bool RatesMatch(float tickDeltaSeconds, float fixedDt) => tickDeltaSeconds == fixedDt;

    public void Execute(in TickContext ctx)
    {
        Debug.Assert(
            RatesMatch(ctx.DeltaSeconds, _settings.FixedDt),
            "PhysicsSystem's fixed step and the accumulator's tick rate have drifted apart — configure them to match.");
        _world.StepPhysics(in _settings);
    }
}
