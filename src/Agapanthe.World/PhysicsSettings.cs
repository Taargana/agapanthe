using System.Numerics;

namespace Agapanthe.World;

/// <summary>
/// The tunables of one physics step (P3-M3). A GPU-free value struct: the application (or a bench) sets it and
/// hands it to <see cref="GameWorld.StepPhysics"/> through the engine's physics system.
/// <para>
/// <b>Fixed timestep, on purpose.</b> <see cref="FixedDt"/> is a constant, not the frame's wall-clock delta: the
/// captures are deterministic <i>by frame count</i> (like <c>BenchSpinSystem</c>), so a variable substep count
/// would make two runs of the same scene diverge. A wall-clock accumulator + render interpolation is deferred to
/// the backlog.
/// </para>
/// </summary>
public readonly struct PhysicsSettings
{
    public PhysicsSettings(Vector3 gravity, float groundY, float fixedDt)
    {
        Gravity = gravity;
        GroundY = groundY;
        FixedDt = fixedDt;
    }

    /// <summary>Gravitational acceleration, m/s². Default sandbox value is <c>(0, -9.81, 0)</c>.</summary>
    public Vector3 Gravity { get; }

    /// <summary>The Y of the collision ground half-space: a body rests when <c>pos.Y - radius = GroundY</c>.</summary>
    public float GroundY { get; }

    /// <summary>The fixed step, seconds. Default <c>1/60</c>. One substep per tick in v1.</summary>
    public float FixedDt { get; }

    /// <summary>The default: earth gravity, a ground at <paramref name="groundY"/>, a 60 Hz step.</summary>
    public static PhysicsSettings Default(float groundY)
        => new(new Vector3(0f, -9.81f, 0f), groundY, 1f / 60f);
}
