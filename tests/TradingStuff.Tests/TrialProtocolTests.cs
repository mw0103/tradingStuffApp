using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Trials;

namespace TradingStuff.Tests;

/// <summary>
/// The pre-registered trial protocol's arithmetic — no database required.
/// </summary>
/// <remarks>
/// These rules are the reason the registry exists, so they are pinned here rather than only in the
/// Postgres suite a machine without a database will skip. Each threshold is asserted on both sides
/// of its boundary: a gate that is merely approximately right is a gate that can be argued with
/// after the fact, which is exactly what pre-registration is meant to foreclose.
/// </remarks>
public sealed class TrialProtocolTests
{
    [Fact]
    public void TheCapAndBaseThresholdAreTheRegisteredValues()
    {
        Assert.Equal(10, TrialProtocol.VariantCap);
        Assert.Equal(0.05, TrialProtocol.BasePThreshold);
        Assert.Equal(5, TrialProtocol.SuperiorPredictiveAbilityTrigger);
    }

    // ---------- deflation ----------

    [Theory]
    [InlineData(1, 0.05)]
    [InlineData(2, 0.025)]
    [InlineData(5, 0.01)]
    [InlineData(10, 0.005)]
    public void TheThresholdDeflatesWithTheRegisteredCount(int variants, double expected) =>
        Assert.Equal(expected, TrialProtocol.DeflatedPThreshold(variants), 12);

    [Fact]
    public void AThresholdNeedsAtLeastOneRegisteredVariant()
    {
        // Zero would divide by zero and produce an infinite threshold — a gate nothing could fail.
        Assert.Throws<ArgumentOutOfRangeException>(() => TrialProtocol.DeflatedPThreshold(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrialProtocol.DeflatedPThreshold(-1));
    }

    [Fact]
    public void MoreVariantsMakeTheGateStrictlyHarder()
    {
        // The direction matters more than any single value: registering another variant must never
        // make an existing result easier to call significant.
        for (var n = 1; n < TrialProtocol.VariantCap; n++)
        {
            Assert.True(TrialProtocol.DeflatedPThreshold(n + 1) < TrialProtocol.DeflatedPThreshold(n));
        }
    }

    // ---------- the cap ----------

    [Theory]
    [InlineData(0, true)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(11, false)]
    public void TheCapAllowsExactlyTenVariants(int registered, bool canRegister) =>
        Assert.Equal(canRegister, TrialProtocol.CanRegisterAnother(registered));

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public void AnSpaTestIsRequiredBeyondFiveVariants(int registered, bool required) =>
        // Strictly beyond: the registration says "N > 5", so five itself does not trigger it.
        Assert.Equal(required, TrialProtocol.RequiresSuperiorPredictiveAbilityTest(registered));

    // ---------- the H1 gate ----------

    private static string Judge(
        double gain = 0.06, double p = 0.001, int improved = 3, int total = 3,
        double yearShare = 0.30, double threshold = 0.05) =>
        TrialProtocol.Judge(gain, p, improved, total, yearShare, threshold);

    [Fact]
    public void AResultClearingEveryClauseIsValidated() =>
        Assert.Equal(TrialVerdicts.Validated, Judge());

    [Fact]
    public void EachClauseCanFailTheGateOnItsOwn()
    {
        // The registration states them conjunctively, so each is asserted alone against an
        // otherwise passing result. A gate that only fails when several things go wrong is not
        // the gate that was registered.
        Assert.Equal(TrialVerdicts.Negative, Judge(gain: 0.005));                  // below any bar
        Assert.Equal(TrialVerdicts.Negative, Judge(p: 0.20));                      // not significant
        Assert.Equal(TrialVerdicts.Negative, Judge(improved: 1, total: 3));        // carried by one fold
        Assert.Equal(TrialVerdicts.Negative, Judge(yearShare: 0.80));              // carried by one year
    }

    [Theory]
    [InlineData(0.009, TrialVerdicts.Negative)]
    [InlineData(0.015, TrialVerdicts.Negative)]                       // real but under H1's 2%
    [InlineData(0.02, TrialVerdicts.InsufficientMagnitude)]           // clears H1, under the economic bar
    [InlineData(0.049, TrialVerdicts.InsufficientMagnitude)]
    [InlineData(0.05, TrialVerdicts.Validated)]                       // the economic bar, inclusive
    [InlineData(0.20, TrialVerdicts.Validated)]
    public void TheGainBandsAreBoundedAsRegistered(double gain, string expected) =>
        Assert.Equal(expected, Judge(gain: gain));

    [Fact]
    public void SignificanceIsJudgedAgainstTheDeflatedThresholdNotTheBaseOne()
    {
        // A p-value of 0.03 clears an undeflated 0.05 and fails once three variants are
        // registered. This is the entire practical effect of the registry on a decision.
        Assert.Equal(TrialVerdicts.Validated, Judge(p: 0.03, threshold: TrialProtocol.DeflatedPThreshold(1)));
        Assert.Equal(TrialVerdicts.Negative, Judge(p: 0.03, threshold: TrialProtocol.DeflatedPThreshold(3)));
    }

    [Fact]
    public void TheSignificanceComparisonIsStrict()
    {
        // Exactly at the threshold does not clear it.
        Assert.Equal(TrialVerdicts.Negative, Judge(p: 0.05, threshold: 0.05));
        Assert.Equal(TrialVerdicts.Validated, Judge(p: 0.0499, threshold: 0.05));
    }

    [Fact]
    public void TheConcentrationCheckIsInclusiveAtAHalf()
    {
        // "no single year > 50% of gain" — a half exactly is still acceptable.
        Assert.Equal(TrialVerdicts.Validated, Judge(yearShare: 0.50));
        Assert.Equal(TrialVerdicts.Negative, Judge(yearShare: 0.5001));
    }

    [Fact]
    public void TwoImprovedFoldsAreEnoughAndOneIsNot()
    {
        // "improvement positive in >= 2 of 3 walk-forward folds".
        Assert.Equal(TrialVerdicts.Validated, Judge(improved: 2, total: 3));
        Assert.Equal(TrialVerdicts.Negative, Judge(improved: 1, total: 3));
    }

    [Fact]
    public void AGateNeedsAtLeastOneFold() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Judge(total: 0));

    [Fact]
    public void ANegativeGainIsNegativeWhateverElseHolds() =>
        // A model that is worse than the baseline cannot be rescued by a small p-value.
        Assert.Equal(TrialVerdicts.Negative, Judge(gain: -0.10, p: 1e-9));
}
