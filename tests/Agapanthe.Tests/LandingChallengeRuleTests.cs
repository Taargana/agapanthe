using Agapanthe.Engine;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free tests for the VS-3 landing-challenge rule (pure function): win on N-in-zone, lose only when the budget is
/// spent AND nothing is still airborne, and the Won/Lost latch (monotonic — terminal states never flip back).
/// N = 3, M = 6 throughout unless noted.
/// </summary>
public sealed class LandingChallengeRuleTests
{
    private static readonly LandingChallengeRule Rule = new(targetCount: 3, shotBudget: 6);

    [Fact]
    public void NoShots_IsInProgress()
        => Assert.Equal(LandingStatus.InProgress, Rule.Evaluate(new LandingCounts(0, 0, 0), 0, LandingStatus.InProgress));

    [Fact]
    public void BelowTarget_WithBudgetLeft_IsInProgress()
        => Assert.Equal(
            LandingStatus.InProgress,
            Rule.Evaluate(new LandingCounts(Total: 2, Airborne: 0, InZone: 2), shotsIssued: 2, LandingStatus.InProgress));

    [Fact]
    public void ReachedTarget_Wins()
        => Assert.Equal(
            LandingStatus.Won,
            Rule.Evaluate(new LandingCounts(Total: 4, Airborne: 0, InZone: 3), shotsIssued: 4, LandingStatus.InProgress));

    [Fact]
    public void ExceededTarget_Wins()
        => Assert.Equal(
            LandingStatus.Won,
            Rule.Evaluate(new LandingCounts(Total: 5, Airborne: 0, InZone: 4), shotsIssued: 5, LandingStatus.InProgress));

    [Fact]
    public void BudgetSpent_AllSettled_ShortOfTarget_Loses()
        => Assert.Equal(
            LandingStatus.Lost,
            Rule.Evaluate(new LandingCounts(Total: 6, Airborne: 0, InZone: 2), shotsIssued: 6, LandingStatus.InProgress));

    [Fact]
    public void BudgetSpent_ButLastShotStillAirborne_StaysInProgress()
        => Assert.Equal(
            LandingStatus.InProgress, // a mid-flight probe could still reach the zone → not Lost yet
            Rule.Evaluate(new LandingCounts(Total: 6, Airborne: 1, InZone: 2), shotsIssued: 6, LandingStatus.InProgress));

    [Fact]
    public void BudgetNotYetSpent_AllSettledShort_StaysInProgress()
        => Assert.Equal(
            LandingStatus.InProgress, // shots remain → player can still drop more
            Rule.Evaluate(new LandingCounts(Total: 4, Airborne: 0, InZone: 2), shotsIssued: 4, LandingStatus.InProgress));

    [Fact]
    public void WonLatch_ProbeSlidesOutOfZone_StaysWon()
        => Assert.Equal(
            LandingStatus.Won, // regressed counts (InZone < N) must NOT un-win
            Rule.Evaluate(new LandingCounts(Total: 5, Airborne: 0, InZone: 1), shotsIssued: 5, LandingStatus.Won));

    [Fact]
    public void LostLatch_PileReSettlesIntoZone_StaysLost()
        => Assert.Equal(
            LandingStatus.Lost, // even if counts now meet the target, a lost game stays lost
            Rule.Evaluate(new LandingCounts(Total: 6, Airborne: 0, InZone: 3), shotsIssued: 6, LandingStatus.Lost));

    [Fact]
    public void WinTakesPriorityOverBudgetSpent()
        => Assert.Equal(
            LandingStatus.Won, // budget spent AND target reached on the same evaluation → Won, not Lost
            Rule.Evaluate(new LandingCounts(Total: 6, Airborne: 0, InZone: 3), shotsIssued: 6, LandingStatus.InProgress));
}
