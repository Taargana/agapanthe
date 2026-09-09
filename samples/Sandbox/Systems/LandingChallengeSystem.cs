using System.Numerics;
using Agapanthe.App;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.World;

namespace Sandbox;

// VS-3 landing-challenge system, as a Stage.PostSimulation system (reads positions AFTER the Simulation-stage
// physics). Each tick it queries the world (generic spatial counts), evaluates the pure latched rule, and — only
// when a shown value changed — rewrites the window title (0-alloc in steady state) and logs terminal transitions.
// TryShoot() (called by the B keypress) drops an AIMED probe radially below the camera, budget-checked by the
// authoritative _shotsIssued. Both counters seed from the world on the FIRST TICK — not the constructor: Contenu-3a
// (D7) moved the AGAPANTHE_LOAD restore to AFTER Build() returns, so at construction the world is still empty on a
// resume. Seeding on the first Execute is after any host-applied restore, by construction, and order-independent.
internal sealed class LandingChallengeSystem : ISystem
{
    private readonly GameWorld _world;
    private readonly IWindow _window;
    private readonly Double3 _attractorCenter;
    private readonly double _surfaceRadius;
    private readonly double _surfaceBand;
    private readonly Double3 _zoneCenter;
    private readonly double _zoneRadius;
    private readonly ImportedEntitySpec _probeSpec;
    private readonly float _probeRadius;
    private readonly double _dropHeight;
    private readonly LandingChallengeRule _rule;
    private readonly int _targetCount;
    private readonly int _shotBudget;
    private int _shotsIssued;
    private LandingStatus _status;
    private bool _seeded;
    private int _lastInZone = -1;
    private int _lastShots = -1;
    private LandingStatus _lastStatus = (LandingStatus)(-1);

    public LandingChallengeSystem(
        GameWorld world, IWindow window, Double3 attractorCenter, double surfaceRadius, double surfaceBand,
        Double3 zoneCenter, double zoneRadius, in ImportedEntitySpec probeSpec, float probeRadius, double dropHeight,
        int targetCount, int shotBudget)
    {
        _world = world;
        _window = window;
        _attractorCenter = attractorCenter;
        _surfaceRadius = surfaceRadius;
        _surfaceBand = surfaceBand;
        _zoneCenter = zoneCenter;
        _zoneRadius = zoneRadius;
        _probeSpec = probeSpec;
        _probeRadius = probeRadius;
        _dropHeight = dropHeight;
        _targetCount = targetCount;
        _shotBudget = shotBudget;
        _rule = new LandingChallengeRule(targetCount, shotBudget);
        _status = LandingStatus.InProgress;
    }

    public void Execute(in TickContext ctx)
    {
        var counts = _world.QuerySurfaceContacts(_attractorCenter, _surfaceRadius, _surfaceBand, _zoneCenter, _zoneRadius);

        if (!_seeded)
        {
            // First tick — the host has applied any AGAPANTHE_LOAD restore by now (Contenu-3a D7). Seed the
            // authoritative shot count from the bodies already at rest, exactly as the pre-3a constructor did.
            _seeded = true;
            _shotsIssued = counts.Total;
            _status = _rule.Evaluate(counts, _shotsIssued, LandingStatus.InProgress);
        }

        var prev = _status;
        _status = _rule.Evaluate(counts, _shotsIssued, _status);

        if (counts.InZone != _lastInZone || _shotsIssued != _lastShots || _status != _lastStatus)
        {
            _window.Title =
                $"Agapanthe — landed {counts.InZone}/{_targetCount} · shots {_shotsIssued}/{_shotBudget} · {Label(_status)}";
            _lastInZone = counts.InZone;
            _lastShots = _shotsIssued;
            _lastStatus = _status;
        }

        if (_status != prev && _status is LandingStatus.Won or LandingStatus.Lost)
        {
            Log.Info($"Sandbox: [challenge] {Label(_status)} — {counts.InZone}/{_targetCount} landed in {_shotsIssued}/{_shotBudget} shots.");
        }
    }

    // Drops one AIMED probe: radially below the camera, from a fixed height above the surface, zero velocity.
    // Budget-checked against _shotsIssued so a pending drop still counts; no-op once over or the budget is spent.
    public void TryShoot(Double3 cameraPosition)
    {
        // A B-key command can be drained (input phase) before this system's first Execute (PostSimulation) — seed
        // here too so a shot fired on the resume tick counts against the restored budget, not a stale zero.
        if (!_seeded)
        {
            var seed = _world.QuerySurfaceContacts(_attractorCenter, _surfaceRadius, _surfaceBand, _zoneCenter, _zoneRadius);
            _seeded = true;
            _shotsIssued = seed.Total;
            _status = _rule.Evaluate(seed, _shotsIssued, LandingStatus.InProgress);
        }

        if (_status != LandingStatus.InProgress || _shotsIssued >= _shotBudget)
        {
            return;
        }

        var d = cameraPosition - _attractorCenter;
        var dist = d.Length;
        if (dist < 1e-9)
        {
            return;
        }

        var n = d * (1.0 / dist);
        var spawn = _attractorCenter + (n * (_surfaceRadius + _dropHeight));
        var spec = new ImportedEntitySpec(
            _probeSpec.Mesh, _probeSpec.Material, spawn, _probeSpec.RotationScale,
            _probeSpec.BoundsCenter, _probeSpec.BoundsRadius, _probeSpec.Order, _probeSpec.Identity); // Contenu-3a: identity
        _world.SpawnBodyDeferred(in spec, Vector3.Zero, inverseMass: 1f, restitution: 0.4f, radius: _probeRadius);
        _shotsIssued++;
    }

    private static string Label(LandingStatus s) => s switch
    {
        LandingStatus.Won => "WON",
        LandingStatus.Lost => "LOST",
        _ => "IN PROGRESS",
    };
}
