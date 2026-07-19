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
/// nothing, like every other system. <see cref="TickContext.DeltaSeconds"/> is deliberately ignored — physics
/// steps by frame count (spec §2), which is what keeps a scene reproducible run-to-run.
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

    public void Execute(in TickContext ctx) => _world.StepPhysics(in _settings);
}
