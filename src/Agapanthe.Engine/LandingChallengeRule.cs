using Agapanthe.World;

namespace Agapanthe.Engine;

/// <summary>The state of a landing challenge (VS-3): still playable, or a terminal win/loss.</summary>
public enum LandingStatus
{
    InProgress,
    Won,
    Lost,
}

/// <summary>
/// The VS-3 landing-challenge rule: a pure, latched function that turns a <see cref="LandingCounts"/> spatial
/// snapshot (from <see cref="GameWorld.QuerySurfaceContacts"/>) plus the authoritative shot count into a
/// <see cref="LandingStatus"/>. Gameplay lives here — the orchestration layer — not in the reusable World/Rendering
/// core. Stateless and allocation-free; the caller owns the latched <paramref name="prev"/> and the shot counter.
/// </summary>
public readonly struct LandingChallengeRule
{
    private readonly int _targetCount; // N: probes that must be in the zone to win
    private readonly int _shotBudget;  // M: total probes the player may drop

    public LandingChallengeRule(int targetCount, int shotBudget)
    {
        _targetCount = targetCount;
        _shotBudget = shotBudget;
    }

    /// <summary>
    /// Evaluates the status. <b>Latched/monotonic</b>: once <paramref name="prev"/> is terminal (Won/Lost) it is
    /// returned unchanged — a probe later sliding out of the zone does not un-win, and a re-settling pile does not
    /// flip Lost back. Otherwise: <see cref="LandingStatus.Won"/> as soon as <c>InZone ≥ N</c>;
    /// <see cref="LandingStatus.Lost"/> only when the budget is spent (<c>shotsIssued ≥ M</c>) AND nothing is still
    /// falling (<c>Airborne == 0</c>) AND not won — the <c>Airborne == 0</c> guard keeps the game InProgress while the
    /// last shots are mid-flight. Otherwise <see cref="LandingStatus.InProgress"/>.
    /// </summary>
    public LandingStatus Evaluate(LandingCounts counts, int shotsIssued, LandingStatus prev)
    {
        if (prev is LandingStatus.Won or LandingStatus.Lost)
        {
            return prev; // terminal — the latch
        }

        if (counts.InZone >= _targetCount)
        {
            return LandingStatus.Won;
        }

        if (shotsIssued >= _shotBudget && counts.Airborne == 0)
        {
            return LandingStatus.Lost;
        }

        return LandingStatus.InProgress;
    }
}
