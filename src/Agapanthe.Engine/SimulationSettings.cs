namespace Agapanthe.Engine;

/// <summary>
/// The simulation's protocol-level constants — today only the fixed timestep (MP-0 cap,
/// <c>Agapanthe.App</c> milestone). The netcode milestone will add the tick rate here as a wire
/// constant.
/// <para>
/// <b>This is the single place <c>1/60</c> is DEFINED.</b> <c>FrameOrchestrator</c>'s accumulator
/// reads it (via <c>FrameOrchestrator.ResolveAccumulatorStep</c>); the scene recipes derive their
/// <c>PhysicsSettings.FixedDt</c> from it; <c>samples/HeadlessSim</c> reads it instead of carrying
/// its own literal. <c>PhysicsSettings</c> lives in <c>Agapanthe.World</c>, which cannot reference
/// this assembly, so its own default literal stays — reconciled at runtime by
/// <see cref="PhysicsSystem"/>'s rate assert.
/// </para>
/// </summary>
public sealed class SimulationSettings
{
    /// <summary>The fixed simulation step, seconds. A PERIOD, not a rate. Default <c>1/60</c>.</summary>
    public float FixedDeltaSeconds { get; init; } = 1f / 60f;

    /// <summary>The shared default instance. <c>SimulationHost.CreateDefault(GameWorld)</c> hands this exact
    /// reference to every host that does not ask for its own.</summary>
    public static SimulationSettings Default { get; } = new();
}
