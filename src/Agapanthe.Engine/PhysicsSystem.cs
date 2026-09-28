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
    /// The ECS-visible surface <see cref="GameWorld.StepPhysics"/> reads: <c>GlobalId</c>/<c>RigidBody</c> (never
    /// written anywhere in <c>GameWorld.Physics.cs</c>) and <c>InstanceSlot</c> (read via <c>.Get&lt;InstanceSlot&gt;()</c>
    /// to feed <c>MarkDirty</c> — a real ECS access, so a real conflict risk if some future system wrote it
    /// concurrently). Declaring these (Job-2b) is what lets <see cref="RequiresExclusiveExecution"/> below be
    /// <see langword="false"/> instead of the interface default — but see that member's remarks for what these two
    /// lists still cannot represent.
    /// </summary>
    public IReadOnlyList<Type> Reads { get; } = [typeof(GlobalId), typeof(RigidBody), typeof(InstanceSlot)];

    /// <summary>The ECS-visible surface <see cref="GameWorld.StepPhysics"/> writes — the only two <c>.Set&lt;T&gt;()</c>
    /// calls in <c>GameWorld.Physics.cs</c>, both in its final "scatter" pass. See <see cref="Reads"/>.</summary>
    public IReadOnlyList<Type> Writes { get; } = [typeof(WorldPosition), typeof(Velocity)];

    /// <summary>
    /// <see langword="false"/> (Job-2b — was unconditionally <see langword="true"/> before this member's sibling,
    /// <see cref="RequiresOwnerThread"/>, existed): <see cref="Reads"/>/<see cref="Writes"/> above now let this
    /// system correctly conflict with anything else touching the same components, so it no longer needs to
    /// monopolize its whole wave to be safe.
    /// </summary>
    public bool RequiresExclusiveExecution => false;

    /// <summary>
    /// <see langword="true"/> (Job-2b): <see cref="GameWorld.StepPhysics"/> still owns state invisible to
    /// <see cref="Reads"/>/<see cref="Writes"/> no matter how precisely they are declared — the shared physics
    /// broadphase scratch (<c>_cellHead</c>/<c>_cellNext</c>/etc.) AND the render/network dirty-tracking side
    /// tables it feeds via <c>MarkDirty</c>/<c>MarkNetworkDirty</c> (<c>GameWorld.Physics.cs</c>). All of that is
    /// protected purely by <c>GameWorld.AssertOwnerThreadStrict</c>, which a job-system worker thread is never
    /// sanctioned to bypass (Job-1 D2). This system must therefore always run on the scheduler's OWNER thread, even
    /// though — thanks to real <see cref="Reads"/>/<see cref="Writes"/> above — it may now correctly share a wave
    /// with a disjoint system running on a worker.
    /// <para>
    /// <b>This is not mutual protection.</b> A system sharing this wave must itself touch no equivalent hidden
    /// state (own scratch buffers, its own owner-thread-strict-guarded calls) — <see cref="Reads"/>/<see cref="Writes"/>
    /// only arbitrate ECS component conflicts between wave members, never anything outside that model. See
    /// <see cref="ISystem.RequiresOwnerThread"/>'s own remarks for a real, reproduced race this class of hidden
    /// state can open if a future pinned system is not equally careful.
    /// </para>
    /// </summary>
    public bool RequiresOwnerThread => true;

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
