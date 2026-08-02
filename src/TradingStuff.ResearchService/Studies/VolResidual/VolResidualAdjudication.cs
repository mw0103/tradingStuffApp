using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// Turns a scored walk-forward run into the pre-registration's H1 verdict: the margin-adjusted
/// Diebold-Mariano test, the one-sided stationary block bootstrap, the per-fold and per-VIX-half
/// sign checks, and the single sentence the outcome permits.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed the study reported a pooled QLIKE margin and nothing else, which meant H1 —
/// a five-condition conjunction — could not be resolved from the platform at all. A margin on its
/// own is the one condition most likely to look like a result while being sampling noise, so
/// reporting it alone was worse than reporting nothing.
/// </para>
/// <para>
/// <b>The primary test is margin-adjusted.</b> The registration's "SPA, specified precisely" section
/// is unambiguous: the conventional differential's null is "the candidate beats the gate by some
/// positive amount", which is not the gate. The primary statistic here is therefore
/// <c>d_t(0.02) = 0.98*L_gate,t - L_candidate,t</c>. The tau = 0 figure is computed and returned
/// alongside because the registration says it may be reported, and it carries
/// <see cref="VolResidualDmInterpretations.SomeSuperiority"/> so it cannot be quoted as the
/// materiality result.
/// </para>
/// <para>
/// <b>The pooled series is date-ordered across folds.</b> Both the HAC variance and the block
/// bootstrap are statements about serial dependence, so the order matters. The registered folds test
/// on disjoint, increasing windows (2018-19, 2020-21, 2022-23), so concatenating them in date order
/// is the natural sequence; the two seams between fold test blocks are the only places where
/// adjacent observations are not adjacent trading days, which at a mean block length of 20 days is a
/// second-order effect and is left uncorrected rather than papered over with an unregistered
/// adjustment.
/// </para>
/// </remarks>
public static class VolResidualAdjudication
{
    /// <summary>The registered materiality margin: 2%.</summary>
    public const double Tau = 0.02;

    /// <summary>The registration's HAC truncation lag for the session/1-day label.</summary>
    public const int HacLag = 5;

    /// <summary>H1's pooled-margin condition, in percent.</summary>
    public const double MarginThresholdPct = 2.0;

    /// <summary>H1's significance condition on the margin-adjusted one-sided p-value.</summary>
    public const double PValueThreshold = 0.05;

    /// <summary>H1 requires a positive improvement in at least two of the three registered folds.</summary>
    public const int RequiredPositiveFolds = 2;

    /// <summary>The number of folds the registration defines. Fewer scored is itself a failed condition.</summary>
    public const int RegisteredFolds = 3;

    /// <summary>
    /// Fixed so an identical rerun produces an identical interval. Reproducibility is a registered
    /// requirement, so the seed is a constant of the study rather than anything a caller supplies —
    /// a seed that can be passed in is a seed that can be searched over.
    /// </summary>
    public const ulong BootstrapSeed = 0x564F4C52455331UL; // "VOLRES1"

    /// <summary>
    /// The pre-registration's fixed claim-language table. These are the only sentences an outcome of
    /// this study is permitted to be described with.
    /// </summary>
    public static class Claims
    {
        public const string ClearsHarX =
            "adds forecast value beyond a simple model using the same registered information";

        public const string BeatsHarFailsHarX =
            "does not outperform a simple model with the same information";

        public const string BeatsHarXFailsHar =
            "improves on the information-matched specification but not on the simpler realized-only " +
            "reference; no overall forecasting-superiority claim is supported";

        public const string Negative =
            "declared negative under the registered falsification rule: no forecasting-superiority " +
            "claim of any kind is supported";
    }

    /// <summary>Named so a failed condition reads as a condition, not as a number a reader must interpret.</summary>
    public static class Conditions
    {
        public const string Margin = "pooled-margin";
        public const string DieboldMarianoP = "diebold-mariano-p";
        public const string FoldSigns = "fold-signs";
        public const string FoldCount = "fold-count";
        public const string VixHalves = "vix-halves";
        public const string BootstrapCi = "bootstrap-ci";
    }

    /// <summary>
    /// Adjudicates H1 for <paramref name="candidateKey"/> against the gate. Returns null when there
    /// is nothing to adjudicate — fewer than two scored days makes every statistic below undefined,
    /// and an absent verdict is honest where a manufactured one is not.
    /// </summary>
    public static VolResidualH1Verdict? Adjudicate(
        IReadOnlyList<VolResidualFoldResult> foldResults,
        string candidateKey,
        string gateKey = VolResidualModelKeys.Gate,
        string referenceKey = VolResidualModelKeys.Har)
    {
        ArgumentNullException.ThrowIfNull(foldResults);

        var ordered = OrderedDays(foldResults);
        if (ordered.Count < 2) return null;

        var candidateLosses = ordered.Select(d => d.Qlike[candidateKey]).ToList();
        var gateLosses = ordered.Select(d => d.Qlike[gateKey]).ToList();

        var marginAdjusted = Dm(candidateLosses, gateLosses, Tau);
        var unadjusted = Dm(candidateLosses, gateLosses, 0.0);

        var pooledCandidate = candidateLosses.Average();
        var pooledGate = gateLosses.Average();
        var pooledReference = ordered.Select(d => d.Qlike[referenceKey]).Average();
        var marginPct = QlikeRetransformation.ImprovementPercent(pooledCandidate, pooledGate);

        // The bootstrap bounds the MARGIN-ADJUSTED advantage, not the raw one. H1's CI condition
        // exists to address sampling noise around the gate the study registered, and a CI on the
        // tau = 0 advantage would be a CI on a different, weaker claim.
        var advantages = new double[ordered.Count];
        for (var i = 0; i < ordered.Count; i++)
            advantages[i] = (1.0 - Tau) * gateLosses[i] - candidateLosses[i];

        var bootstrap = StationaryBlockBootstrap.LowerBound(advantages, BootstrapSeed);

        var folds = foldResults
            .OrderBy(f => f.TestFrom)
            .Select(f => FoldAdjudication(f, candidateKey, gateKey))
            .ToList();

        var vixHalves = VixHalfResults(ordered, candidateKey, gateKey);

        var marginPasses = marginPct >= MarginThresholdPct;
        var dmPasses = marginAdjusted.PValueOneSided < PValueThreshold;
        var foldsPositive = folds.Count(f => f.Positive);
        var foldsPass = foldsPositive >= RequiredPositiveFolds;
        var vixHalvesPositive = vixHalves.Count == 2 && vixHalves.All(h => h.Positive);

        var failed = new List<string>();
        if (!marginPasses) failed.Add(Conditions.Margin);
        if (!dmPasses) failed.Add(Conditions.DieboldMarianoP);
        if (!foldsPass) failed.Add(Conditions.FoldSigns);
        if (folds.Count < RegisteredFolds) failed.Add(Conditions.FoldCount);
        if (!vixHalvesPositive) failed.Add(Conditions.VixHalves);
        if (!bootstrap.ExcludesZero) failed.Add(Conditions.BootstrapCi);

        var verdict = failed.Count == 0 ? VolResidualVerdicts.Pass : VolResidualVerdicts.Fail;
        var (claim, basis) = SelectClaim(verdict, pooledCandidate, pooledGate, pooledReference);

        return new VolResidualH1Verdict(
            gateKey,
            candidateKey,
            marginPct,
            marginPasses,
            marginAdjusted.Statistic,
            marginAdjusted.PValueOneSided,
            dmPasses,
            foldsPositive,
            folds.Count,
            foldsPass,
            bootstrap.LowerBound,
            bootstrap.ExcludesZero,
            vixHalvesPositive,
            verdict,
            failed,
            marginAdjusted,
            unadjusted,
            new VolResidualBootstrapCi(
                bootstrap.SampleMean, bootstrap.LowerBound, bootstrap.Alpha, bootstrap.Resamples,
                bootstrap.MeanBlockLength, unchecked((long)bootstrap.Seed), bootstrap.ExcludesZero),
            folds,
            vixHalves,
            claim,
            basis);
    }

    /// <summary>
    /// The two Diebold-Mariano rows for a model that is NOT being adjudicated — an exploratory rung.
    /// Deliberately separate from <see cref="Adjudicate"/>: producing a verdict object for a run
    /// outside the registered ladder would be manufacturing the very eligibility the exploratory
    /// tagging exists to deny.
    /// </summary>
    public static (VolResidualDieboldMariano MarginAdjusted, VolResidualDieboldMariano Unadjusted)? CompareOnly(
        IReadOnlyList<VolResidualFoldResult> foldResults, string modelKey, string gateKey = VolResidualModelKeys.Gate)
    {
        ArgumentNullException.ThrowIfNull(foldResults);

        var ordered = OrderedDays(foldResults);
        if (ordered.Count < 2) return null;

        var losses = ordered.Select(d => d.Qlike[modelKey]).ToList();
        var gateLosses = ordered.Select(d => d.Qlike[gateKey]).ToList();

        return (Dm(losses, gateLosses, Tau), Dm(losses, gateLosses, 0.0));
    }

    private static List<VolResidualDailyResult> OrderedDays(IReadOnlyList<VolResidualFoldResult> foldResults) =>
        foldResults.SelectMany(f => f.DailyResults).OrderBy(d => d.Date).ToList();

    private static VolResidualDieboldMariano Dm(
        IReadOnlyList<double> candidateLosses, IReadOnlyList<double> gateLosses, double tau)
    {
        var result = DieboldMariano.CompareWithMargin(candidateLosses, gateLosses, tau, HacLag);

        return new VolResidualDieboldMariano(
            tau,
            tau > 0.0 ? VolResidualDmInterpretations.Materiality : VolResidualDmInterpretations.SomeSuperiority,
            result.MeanLossAdvantage,
            result.Statistic,
            result.OneSidedPValue,
            result.LongRunVariance,
            result.Observations,
            result.HacLag);
    }

    private static VolResidualFoldAdjudication FoldAdjudication(
        VolResidualFoldResult fold, string candidateKey, string gateKey)
    {
        var gate = fold.DailyResults.Average(d => d.Qlike[gateKey]);
        var candidate = fold.DailyResults.Average(d => d.Qlike[candidateKey]);
        var ordinal = int.TryParse(fold.FoldName.TrimStart('F'), out var parsed) ? parsed : 0;

        return new VolResidualFoldAdjudication(
            ordinal, fold.DailyResults.Count, gate, candidate,
            QlikeRetransformation.ImprovementPercent(candidate, gate), candidate < gate);
    }

    /// <summary>
    /// Pools the low and high VIX halves. The half a day belongs to was decided in
    /// <see cref="VolResidualFoldRunner"/> against its OWN fold's TRAINING-window median prior VIX —
    /// splitting here on a median of the evaluation sample would define the regimes using the very
    /// data the regimes are used to judge.
    /// </summary>
    private static List<VolResidualVixHalfResult> VixHalfResults(
        IReadOnlyList<VolResidualDailyResult> ordered, string candidateKey, string gateKey)
    {
        var results = new List<VolResidualVixHalfResult>(2);

        foreach (var regime in new[] { VolResidualVixRegimes.Low, VolResidualVixRegimes.High })
        {
            var days = ordered.Where(d => d.VixRegime == regime).ToList();
            if (days.Count == 0) continue;

            var gate = days.Average(d => d.Qlike[gateKey]);
            var candidate = days.Average(d => d.Qlike[candidateKey]);

            results.Add(new VolResidualVixHalfResult(
                regime, days.Count, gate, candidate,
                QlikeRetransformation.ImprovementPercent(candidate, gate), candidate < gate));
        }

        return results;
    }

    /// <summary>
    /// Picks the row of the registration's claim table this outcome falls in. The near-miss
    /// directions are distinguished because the registration distinguishes them: failing H1 while
    /// beating HAR is NOT "no edge", it is the weaker and different statement that the candidate does
    /// not outperform a simple model holding the same information.
    /// </summary>
    private static (string Claim, string Basis) SelectClaim(
        string verdict, double pooledCandidate, double pooledGate, double pooledReference)
    {
        var beatsGate = pooledCandidate < pooledGate;
        var beatsReference = pooledCandidate < pooledReference;

        if (verdict == VolResidualVerdicts.Pass)
        {
            return (Claims.ClearsHarX, "H1 passed on every registered condition against the HAR-X gate.");
        }

        if (beatsReference)
        {
            return (Claims.BeatsHarFailsHarX,
                "H1 failed against the HAR-X gate while the candidate's pooled QLIKE is below the " +
                "HAR reference. Explicitly NOT 'no edge' — the registration defines this near miss " +
                "as the weaker statement that a simple information-matched model is not outperformed.");
        }

        if (beatsGate)
        {
            return (Claims.BeatsHarXFailsHar,
                "The candidate's pooled QLIKE is below the HAR-X gate's but above the HAR reference's.");
        }

        return (Claims.Negative,
            "The candidate's pooled QLIKE is above both the HAR-X gate's and the HAR reference's.");
    }
}
