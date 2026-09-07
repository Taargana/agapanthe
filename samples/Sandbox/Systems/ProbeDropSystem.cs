using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.World;

namespace Sandbox;

// VS-2 planet-drop spawner, as a Stage.Input system: every `every` ticks it drops one probe (a small physics body)
// above the planet's surface anchor via SpawnBodyDeferred. The scheduler's end-of-Input barrier materialises it,
// so Simulation's StepPhysics integrates it the SAME frame (§3.4). Deterministic cadence + fixed-step physics →
// byte-identical headless capture. DropOne() is also called by the B keypress. Zero per-frame alloc on non-drop ticks.
internal sealed class ProbeDropSystem : ISystem
{
    private readonly GameWorld _world;
    private readonly ImportedEntitySpec _spec;
    private readonly Double3 _centre;
    private readonly float _radius;
    private readonly int _every;
    private int _tick;
    private int _dropped;

    public ProbeDropSystem(GameWorld world, in ImportedEntitySpec spec, Double3 centre, float radius, int every)
    {
        _world = world;
        _spec = spec;
        _centre = centre;
        _radius = radius;
        _every = Math.Max(every, 1);
    }

    public void Execute(in TickContext ctx)
    {
        if (_tick++ % _every == 0)
        {
            DropOne();
        }
    }

    // Drops one probe, nudged onto a golden-angle spiral in the local tangent plane (world X/Z at the +Y anchor) so
    // successive probes land in a small pile. Deterministic in _dropped. Zero initial velocity — radial gravity does the rest.
    public void DropOne()
    {
        const float goldenAngle = 2.399963f;
        var (sin, cos) = MathF.SinCos(_dropped * goldenAngle);
        var spread = _radius * 2.5f * MathF.Sqrt((_dropped % 8) + 0.5f);
        var pos = _centre + new Double3(cos * spread, 0f, sin * spread);
        var spec = new ImportedEntitySpec(
            _spec.Mesh, _spec.Material, pos, _spec.RotationScale, _spec.BoundsCenter, _spec.BoundsRadius, _spec.Order);
        _world.SpawnBodyDeferred(in spec, Vector3.Zero, inverseMass: 1f, restitution: 0.4f, radius: _radius);
        _dropped++;
    }
}
