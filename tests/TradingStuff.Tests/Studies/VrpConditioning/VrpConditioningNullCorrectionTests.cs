using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.ResearchService.Studies.VrpConditioning;

namespace TradingStuff.Tests.Studies.VrpConditioning;

/// <summary>
/// On the real 2010-2023 SPX/VIX data every fold's inner blocked CV shrinks the residual model to an
/// intercept, and the corrected arm's forecasts then come out EXACTLY equal to the gate's rather than
/// merely close. That is arithmetic, not coincidence, and these tests pin both halves of it: the
/// identity itself, and the fact that the run says so out loud.
/// </summary>
/// <remarks>
/// Without this, two identical rows in the arms table read as "the correction agreed with the gate",
/// which is a different and much more flattering finding than "the correction does not exist"
/// (docs/LESSONS.md #3). Worse, a later reader who noticed the identity might "fix" it by dropping
/// the retransformation from the corrected arm — which would hand it an advantage the registration
/// explicitly forbids, since the rule is that EVERY model estimated on a transformed target is
/// retransformed from its own training window.
/// </remarks>
public class VrpConditioningNullCorrectionTests
{
    [Fact]
    public void AnInterceptOnlyCorrectionIsCancelledExactlyByTheModelSpecificRetransformationFactor()
    {
        // A gate model's raw exponentiated log forecasts and the realized outcomes they are scored
        // against. Nothing here is special: arbitrary positive numbers with real spread.
        double[] actuals = [0.0121, 0.0044, 0.0310, 0.0087, 0.0195, 0.0062, 0.0410, 0.0138];
        double[] gateRaw = [0.0100, 0.0060, 0.0250, 0.0090, 0.0170, 0.0070, 0.0350, 0.0150];

        var gateFactor = QlikeRetransformation.FitFactor(actuals, gateRaw);

        // An intercept-only elastic net adds a constant c in LOG space, so the corrected raw
        // forecast is exp(c) * gateRaw. Any c at all.
        foreach (var c in new[] { 1.4826e-3, -0.25, 0.9 })
        {
            var correctedRaw = gateRaw.Select(x => Math.Exp(c) * x).ToArray();
            var correctedFactor = QlikeRetransformation.FitFactor(actuals, correctedRaw);

            for (var i = 0; i < actuals.Length; i++)
            {
                var gateForecast = gateFactor * gateRaw[i];
                var correctedForecast = correctedFactor * correctedRaw[i];

                // The cancellation is EXACT in real arithmetic; in doubles it survives to round-off,
                // which is a few ULP. Asserting on a relative tolerance rather than a fixed number of
                // decimal places is the honest form of the claim — a fixed-decimal assertion on a
                // ~1e-2 forecast would silently be a much weaker test on a ~1e-5 one.
                AssertRelativelyEqual(gateForecast, correctedForecast);
                AssertRelativelyEqual(
                    QlikeRetransformation.Loss(actuals[i], gateForecast),
                    QlikeRetransformation.Loss(actuals[i], correctedForecast));
            }

            // The factor moves by exactly exp(-c) — the mechanism, not just the outcome.
            AssertRelativelyEqual(gateFactor * Math.Exp(-c), correctedFactor);
        }
    }

    [Fact]
    public void AFoldWhoseCorrectionSelectedNothingSaysSoAndItsCorrectedArmMatchesTheGateExactly()
    {
        var dates = VrpConditioningFixture.TradingDates(300, new DateOnly(2016, 1, 4));
        var rows = VrpConditioningFixture.Rows(dates);

        var fold = new TradingStuff.Volatility.Forecasting.WalkForwardFold
        {
            Name = "T1",
            TrainStart = dates[0].ToDateTime(TimeOnly.MinValue),
            TrainEnd = dates[159].ToDateTime(TimeOnly.MinValue),
            ValidationStart = dates[160].ToDateTime(TimeOnly.MinValue),
            ValidationEnd = dates[179].ToDateTime(TimeOnly.MinValue),
            TestStart = dates[180].ToDateTime(TimeOnly.MinValue),
            TestEnd = dates[299].ToDateTime(TimeOnly.MinValue),
        };

        var result = VrpConditioningFoldRunner.Run(
            VolResidualSplitter.Split(rows, r => r.Date, [fold], VrpConditioningHorizon.PurgeRows).Single());

        var fit = result.CorrectionFit;
        Assert.Equal("T1", fit.Fold);
        Assert.Equal(13, fit.TotalFeatures);
        Assert.Equal(fit.NonZeroCoefficients == 0, fit.IsNullModel);

        if (fit.IsNullModel)
        {
            Assert.Contains("NULL MODEL", fit.Note);
            Assert.Contains("IDENTICAL", fit.Note);

            // The identity, measured rather than argued.
            Assert.All(result.DailyResults, d => AssertRelativelyEqual(
                d.Forecasts[VrpConditioningArms.HarX], d.Forecasts[VrpConditioningArms.Corrected]));
        }
        else
        {
            Assert.Contains("non-zero coefficient", fit.Note);

            // A live correction must actually move something, or the note is lying.
            Assert.Contains(result.DailyResults, d =>
                RelativeGap(d.Forecasts[VrpConditioningArms.HarX], d.Forecasts[VrpConditioningArms.Corrected]) > 1e-9);
        }
    }

    /// <summary>Equal to within floating-point round-off, scaled to the magnitude being compared.</summary>
    private static void AssertRelativelyEqual(double expected, double actual)
    {
        var gap = RelativeGap(expected, actual);
        Assert.True(gap < 1e-12,
            $"expected {expected:R} and {actual:R} to agree to round-off; relative gap was {gap:E3}.");
    }

    private static double RelativeGap(double a, double b)
    {
        var scale = Math.Max(Math.Abs(a), Math.Abs(b));
        return scale < 1e-300 ? 0.0 : Math.Abs(a - b) / scale;
    }
}
