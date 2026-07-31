using System.Numerics;
using Agapanthe.Core;

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
        : this(gravity, groundY, fixedDt, Double3.Zero, mu: 0.0, surfaceRadius: 0.0)
    {
    }

    // Full ctor (VS-2): the attractor fields are set only through WithAttractor, so a readonly struct never needs an
    // object initializer. mu = 0 means "no attractor" — the uniform-gravity + flat-ground path stays byte-identical.
    private PhysicsSettings(
        Vector3 gravity, float groundY, float fixedDt, Double3 attractorCenter, double mu, double surfaceRadius)
    {
        Gravity = gravity;
        GroundY = groundY;
        FixedDt = fixedDt;
        AttractorCenter = attractorCenter;
        Mu = mu;
        SurfaceRadius = surfaceRadius;
    }

    /// <summary>Gravitational acceleration, m/s² (the UNIFORM field). Ignored when an attractor is set
    /// (<see cref="Mu"/> &gt; 0), which supplies a radial field instead. Default sandbox value is <c>(0, -9.81, 0)</c>.</summary>
    public Vector3 Gravity { get; }

    /// <summary>The Y of the FLAT collision ground half-space: a body rests when <c>pos.Y - radius = GroundY</c>.
    /// Used only on the uniform path; the radial path uses <see cref="SurfaceRadius"/> instead.</summary>
    public float GroundY { get; }

    /// <summary>The fixed step, seconds. Default <c>1/60</c>. One substep per tick in v1.</summary>
    public float FixedDt { get; }

    /// <summary>World-space centre of the single Newtonian attractor (VS-2). Meaningful only when <see cref="Mu"/> &gt; 0.</summary>
    public Double3 AttractorCenter { get; }

    /// <summary>The attractor's standard gravitational parameter μ = G·M (VS-2). <c>0</c> disables Newtonian gravity:
    /// <see cref="GameWorld.StepPhysics"/> then integrates under the uniform <see cref="Gravity"/> + flat
    /// <see cref="GroundY"/> exactly as in P3-M3 (regression-critical — the <c>drop</c> scene stays byte-identical).</summary>
    public double Mu { get; }

    /// <summary>Radius R of the radial ground half-space (the attractor's surface), world metres (VS-2). A body rests
    /// when <c>|pos - AttractorCenter| - radius = SurfaceRadius</c>. Meaningful only when <see cref="Mu"/> &gt; 0.</summary>
    public double SurfaceRadius { get; }

    /// <summary>The default: earth gravity, a flat ground at <paramref name="groundY"/>, a 60 Hz step, no attractor.</summary>
    public static PhysicsSettings Default(float groundY)
        => new(new Vector3(0f, -9.81f, 0f), groundY, 1f / 60f);

    /// <summary>
    /// Returns a copy with a Newtonian point-attractor (VS-2): radial inverse-square gravity toward
    /// <paramref name="center"/> (μ = <paramref name="mu"/>) and a radial ground half-space of radius
    /// <paramref name="surfaceRadius"/>. The uniform <see cref="Gravity"/>/<see cref="GroundY"/> are then ignored by
    /// <see cref="GameWorld.StepPhysics"/>. A <c>readonly struct</c> copy — never mutate in place.
    /// </summary>
    public PhysicsSettings WithAttractor(Double3 center, double mu, double surfaceRadius)
        => new(Gravity, GroundY, FixedDt, center, mu, surfaceRadius);
}
