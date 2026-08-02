using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Studies.VolResidual;

/// <summary>
/// H1 adjudication, checked against the real 2010-2023 loss series and against numbers computed
/// outside this codebase.
/// </summary>
/// <remarks>
/// The headline requirement is that H1 comes out FAIL on this data, and fails on the
/// Diebold-Mariano condition specifically. The pooled margin is +3.02% — comfortably past the
/// registered 2% — so a verdict that read only the margin would say "pass", and a verdict object
/// that says pass on this data is a defect, not a result.
/// </remarks>
public class VolResidualAdjudicationTests
{
    private static VolResidualH1Verdict Verdict(Func<PooledQlikeFixture.Row, string>? regime = null) =>
        VolResidualAdjudication.Adjudicate(
            PooledQlikeFixture.AsFoldResults(regime), VolResidualModelKeys.Corrected)!;

    // ---------- the fixture reproduces the independently computed statistics ----------

    [Fact]
    public void TheFixtureIsTheRealRun()
    {
        Assert.Equal(1509, PooledQlikeFixture.Rows.Count);
        Assert.Equal(0.217662, PooledQlikeFixture.Losses(VolResidualModelKeys.Har).Average(), 6);
        Assert.Equal(0.263031, PooledQlikeFixture.Losses(VolResidualModelKeys.Vix).Average(), 6);
        Assert.Equal(0.199100, PooledQlikeFixture.Losses(VolResidualModelKeys.HarX).Average(), 6);
        Assert.Equal(0.193084, PooledQlikeFixture.Losses(VolResidualModelKeys.Corrected).Average(), 6);
    }

    [Fact]
    public void TheUnadjustedStatisticMatchesTheIndependentlyComputedValue()
    {
        // Computed in Python, outside this codebase, before this implementation existed:
        //   CORRECTED vs HARX, tau=0   mean_d=+0.006016  DM=+1.526  p1=0.0636
        var dm = DieboldMariano.CompareWithMargin(
            PooledQlikeFixture.Losses(VolResidualModelKeys.Corrected),
            PooledQlikeFixture.Losses(VolResidualModelKeys.HarX),
            tau: 0.0,
            hacLag: 5);

        Assert.Equal(0.006016, dm.MeanLossAdvantage, 6);
        Assert.Equal(1.5255, dm.Statistic, 4);
        Assert.Equal(0.06356, dm.OneSidedPValue, 5);
        Assert.Equal(1509, dm.Observations);
        Assert.Equal(5, dm.HacLag);
    }

    [Fact]
    public void TheMarginAdjustedStatisticMatchesTheIndependentlyComputedValue()
    {
        //   CORRECTED vs HARX, tau=0.02  mean_d=+0.002034  DM=+0.517  p1=0.3027
        var dm = DieboldMariano.CompareWithMargin(
            PooledQlikeFixture.Losses(VolResidualModelKeys.Corrected),
            PooledQlikeFixture.Losses(VolResidualModelKeys.HarX),
            tau: VolResidualAdjudication.Tau,
            hacLag: VolResidualAdjudication.HacLag);

        Assert.Equal(0.002034, dm.MeanLossAdvantage, 6);
        Assert.Equal(0.5167, dm.Statistic, 4);
        Assert.Equal(0.30268, dm.OneSidedPValue, 5);
    }

    [Fact]
    public void TheGateReallyIsHarderThanTheReference()
    {
        //   HARX vs HAR, tau=0  mean_d=+0.018561  DM=+3.053  p1=0.0011
        // HAR-X beats HAR decisively, which is why beating HAR is not evidence for H1.
        var dm = DieboldMariano.CompareWithMargin(
            PooledQlikeFixture.Losses(VolResidualModelKeys.HarX),
            PooledQlikeFixture.Losses(VolResidualModelKeys.Har),
            tau: 0.0,
            hacLag: 5);

        Assert.Equal(0.018561, dm.MeanLossAdvantage, 6);
        Assert.Equal(3.0531, dm.Statistic, 4);
        Assert.Equal(0.001132, dm.OneSidedPValue, 6);
    }

    [Fact]
    public void TheMarginAdjustmentActuallyChangesTheTest()
    {
        // A tau applied to the reported mean but not to the tested series is the easy bug: the
        // p-value would be unchanged and the gate would quietly revert to "beats HAR-X by any
        // positive amount". Both the p-value AND the long-run variance must move.
        var candidate = PooledQlikeFixture.Losses(VolResidualModelKeys.Corrected);
        var gate = PooledQlikeFixture.Losses(VolResidualModelKeys.HarX);

        var plain = DieboldMariano.CompareWithMargin(candidate, gate, 0.0, 5);
        var adjusted = DieboldMariano.CompareWithMargin(candidate, gate, 0.02, 5);

        Assert.NotEqual(plain.OneSidedPValue, adjusted.OneSidedPValue);
        Assert.NotEqual(plain.LongRunVariance, adjusted.LongRunVariance);
        Assert.True(adjusted.OneSidedPValue > plain.OneSidedPValue);

        // And it is decisive here: the conventional test is close to 0.05, the registered one is not.
        Assert.True(plain.OneSidedPValue < 0.10);
        Assert.True(adjusted.OneSidedPValue > 0.25);
    }

    // ---------- the verdict ----------

    [Fact]
    public void H1FailsOnThisDataAndFailsOnTheDieboldMarianoCondition()
    {
        var verdict = Verdict();

        Assert.Equal(VolResidualVerdicts.Fail, verdict.Verdict);
        Assert.False(verdict.DmPasses);
        Assert.Contains(VolResidualAdjudication.Conditions.DieboldMarianoP, verdict.FailedConditions);
    }

    [Fact]
    public void ThePartialPassReadsAsAPartialPass()
    {
        var verdict = Verdict();

        // The margin condition PASSES: +3.02% against a 2% threshold. This is exactly why a verdict
        // that collapsed to a single boolean would be misleading.
        Assert.Equal(3.0214, verdict.MarginPct, 4);
        Assert.True(verdict.MarginPasses);
        Assert.DoesNotContain(VolResidualAdjudication.Conditions.Margin, verdict.FailedConditions);

        // Two of the three folds are positive (+5.70%, -0.65%, +5.48%), so the fold condition passes.
        Assert.Equal(3, verdict.FoldsTotal);
        Assert.Equal(2, verdict.FoldsPositive);
        Assert.True(verdict.FoldsPass);
        Assert.Equal([5.70, -0.65, 5.48], verdict.Folds.Select(f => Math.Round(f.ImprovementPct, 2)));
    }

    [Fact]
    public void TheMarginAdjustedStatisticIsTheOneTheVerdictReports()
    {
        // The primary test is tau = 0.02. Reporting the tau = 0 statistic in DmStatistic/DmPValue
        // would flip this verdict's DM condition from fail to a near-miss and misstate the gate.
        var verdict = Verdict();

        Assert.Equal(VolResidualAdjudication.Tau, verdict.MarginAdjusted.Tau);
        Assert.Equal(verdict.MarginAdjusted.Statistic, verdict.DmStatistic);
        Assert.Equal(verdict.MarginAdjusted.PValueOneSided, verdict.DmPValue);
        Assert.Equal(0.5167, verdict.DmStatistic, 4);
        Assert.Equal(0.30268, verdict.DmPValue, 5);
    }

    [Fact]
    public void TheUnadjustedRowIsReportedAndIsLabelledAsNotTheMaterialityClaim()
    {
        var verdict = Verdict();

        Assert.Equal(0.0, verdict.Unadjusted.Tau);
        Assert.Equal(0.06356, verdict.Unadjusted.PValueOneSided, 5);
        Assert.Equal(VolResidualDmInterpretations.SomeSuperiority, verdict.Unadjusted.Interpretation);
        Assert.Contains("may never stand in for the materiality claim", verdict.Unadjusted.Interpretation);
        Assert.Equal(VolResidualDmInterpretations.Materiality, verdict.MarginAdjusted.Interpretation);
    }

    [Fact]
    public void TheClaimIsTheOneThePreRegistrationPermitsAndIsNotNoEdge()
    {
        // CORRECTED beats HAR (0.1931 vs 0.2177) but fails the HAR-X gate. The registration fixes
        // that sentence in advance precisely so it cannot be narrated as "no edge".
        var verdict = Verdict();

        Assert.Equal(VolResidualAdjudication.Claims.BeatsHarFailsHarX, verdict.PermittedClaim);
        Assert.Equal("does not outperform a simple model with the same information", verdict.PermittedClaim);
        Assert.DoesNotContain("no edge", verdict.PermittedClaim, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheBootstrapIsReproducibleAndBoundsTheMarginAdjustedAdvantage()
    {
        var first = Verdict();
        var second = Verdict();

        Assert.Equal(first.BootstrapLower, second.BootstrapLower);
        Assert.Equal(first.Bootstrap.SampleMeanAdvantage, second.Bootstrap.SampleMeanAdvantage);

        // The interval is on the MARGIN-ADJUSTED advantage: its sample mean must be the tau = 0.02
        // mean (+0.002034), not the tau = 0 one (+0.006016). Bounding the weaker quantity would
        // answer a question H1 did not ask.
        Assert.Equal(0.002034, first.Bootstrap.SampleMeanAdvantage, 6);
        Assert.Equal(20.0, first.Bootstrap.MeanBlockLength);
        Assert.Equal(10000, first.Bootstrap.Resamples);
        Assert.Equal(0.05, first.Bootstrap.Alpha);
    }

    [Fact]
    public void TheBootstrapConditionFailsOnThisDataToo()
    {
        var verdict = Verdict();

        Assert.False(verdict.BootstrapExcludesZero);
        Assert.True(verdict.BootstrapLower < 0.0);
        Assert.Contains(VolResidualAdjudication.Conditions.BootstrapCi, verdict.FailedConditions);
    }

    // ---------- the conditions are separable ----------

    [Fact]
    public void EachConditionIsEvaluatedSeparatelyRatherThanCollapsedIntoOne()
    {
        // Both halves negative: only the VIX condition should newly fail, and the ones that were
        // already passing must be unaffected.
        var allLow = Verdict(_ => VolResidualVixRegimes.Low);
        Assert.False(allLow.VixHalvesPositive); // one half present, not two
        Assert.Contains(VolResidualAdjudication.Conditions.VixHalves, allLow.FailedConditions);
        Assert.True(allLow.MarginPasses);
        Assert.True(allLow.FoldsPass);

        // Split by calendar parity — an arbitrary but balanced assignment, purely to exercise the
        // two-half path. The candidate happens to beat the gate in both, so the condition passes.
        var split = Verdict(r => r.Date.DayNumber % 2 == 0
            ? VolResidualVixRegimes.Low
            : VolResidualVixRegimes.High);

        Assert.Equal(2, split.VixHalves.Count);
        Assert.True(split.VixHalvesPositive);
        Assert.DoesNotContain(VolResidualAdjudication.Conditions.VixHalves, split.FailedConditions);

        // Still fails overall, on DM and the CI, which is the point: no single passing condition
        // rescues the verdict.
        Assert.Equal(VolResidualVerdicts.Fail, split.Verdict);
    }

    [Fact]
    public void APassIsReachableSoTheFailIsNotStructural()
    {
        // A verdict object that can only ever say "fail" would be a decoration. Adjudicating HAR-X
        // AGAINST HAR — a comparison that genuinely clears every registered condition — must produce
        // a pass, on the same code path.
        var folds = PooledQlikeFixture.AsFoldResults(r =>
            r.Date.DayNumber % 2 == 0 ? VolResidualVixRegimes.Low : VolResidualVixRegimes.High);

        var verdict = VolResidualAdjudication.Adjudicate(
            folds,
            candidateKey: VolResidualModelKeys.HarX,
            gateKey: VolResidualModelKeys.Har,
            referenceKey: VolResidualModelKeys.Vix)!;

        Assert.Equal(VolResidualVerdicts.Pass, verdict.Verdict);
        Assert.Empty(verdict.FailedConditions);
        Assert.True(verdict.DmPasses);
        Assert.True(verdict.BootstrapExcludesZero);
        Assert.True(verdict.MarginPasses);
        Assert.Equal(VolResidualAdjudication.Claims.ClearsHarX, verdict.PermittedClaim);
    }

    [Fact]
    public void NothingToAdjudicateReturnsNoVerdictRatherThanAManufacturedOne()
    {
        Assert.Null(VolResidualAdjudication.Adjudicate([], VolResidualModelKeys.Corrected));
    }
}
