using System.Globalization;
using TradingStuff.Volatility;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the series diagnostics and the CSV export.
/// </summary>
/// <remarks>
/// These exist to make bad intraday data visible before anything is trained on it, so the
/// assertions are about the counts and thresholds actually surfacing — a diagnostic that
/// silently reports zero outliers is worse than none. The CSV header and column order are
/// pinned exactly: anything reading the file positionally breaks silently if they drift.
/// </remarks>
public class SeriesDiagnosticsTests
{
    private static readonly DateTime Start = new(2024, 1, 2);

    private static RealizedVolatilityDay Day(
        DateTime date, double variance, bool complete = true, bool shortSession = false, int stale = 0) =>
        new()
        {
            Symbol = "SPY",
            Date = date,
            TotalVariance = variance,
            IntradayVariance = variance,
            IsComplete = complete,
            IsShortSession = shortSession,
            StaleSamples = stale,
            ReturnCount = 78,
        };

    /// <summary>Variance chosen so annualized volatility is exactly <paramref name="vol"/>.</summary>
    private static RealizedVolatilityDay DayAtVol(DateTime date, double vol) =>
        Day(date, VolatilityScaling.ToMeanDailyVariance(vol));

    // ---------- diagnostics: validation ----------

    [Fact]
    public void SummarizeRejectsMissingOrEmptySeries()
    {
        Assert.Throws<ArgumentNullException>(() => SeriesDiagnostics.Summarize(null!));
        Assert.Throws<ArgumentException>(() => SeriesDiagnostics.Summarize([]));
    }

    // ---------- diagnostics: counts ----------

    [Fact]
    public void SummarizeCountsEachSessionCategory()
    {
        var days = new List<RealizedVolatilityDay>
        {
            Day(Start, 1e-4),
            Day(Start.AddDays(1), 1e-4, complete: false),
            Day(Start.AddDays(2), 1e-4, shortSession: true),
            Day(Start.AddDays(3), 1e-4, stale: 3),
            Day(Start.AddDays(4), 0.0),
        };

        var d = SeriesDiagnostics.Summarize(days);

        Assert.Equal("SPY", d.Symbol);
        Assert.Equal(5, d.TotalSessions);
        // Complete requires both the flag and positive variance, so the zero-variance day
        // and the incomplete day are both excluded.
        Assert.Equal(3, d.CompleteSessions);
        Assert.Equal(1, d.ShortSessions);
        Assert.Equal(1, d.SessionsWithStaleSamples);
        Assert.Equal(1, d.ZeroVarianceSessions);
    }

    [Fact]
    public void ANegativeVarianceIsReportedRatherThanCrashing()
    {
        // Summarize exists to surface data faults before anything is trained on the series,
        // so it must survive one. The outlier scan skips non-positive sessions, which are
        // already accounted for by ZeroVarianceSessions; annualizing one would throw.
        var d = SeriesDiagnostics.Summarize([Day(Start, -1e-4), Day(Start.AddDays(1), 1e-4)]);

        Assert.Equal(2, d.TotalSessions);
        Assert.Equal(1, d.ZeroVarianceSessions);
        Assert.Equal(1, d.CompleteSessions);
        Assert.Empty(d.Outliers);
    }

    [Fact]
    public void ANegativeVarianceDoesNotHideRealOutliers()
    {
        // Skipping the bad row must not skip the scan: a genuine outlier alongside it is
        // still flagged.
        var d = SeriesDiagnostics.Summarize([Day(Start, -1e-4), DayAtVol(Start.AddDays(1), 9.0)]);

        Assert.Single(d.Outliers);
        Assert.Equal(Start.AddDays(1), d.Outliers[0].Date);
    }

    [Fact]
    public void AZeroVarianceSessionIsCountedButNotComplete()
    {
        var d = SeriesDiagnostics.Summarize([Day(Start, 0.0)]);

        Assert.Equal(0, d.CompleteSessions);
        Assert.Equal(1, d.ZeroVarianceSessions);
    }

    [Fact]
    public void SummarizeOrdersBeforeReportingBounds()
    {
        var days = new List<RealizedVolatilityDay>
        {
            Day(Start.AddDays(5), 1e-4), Day(Start, 1e-4), Day(Start.AddDays(2), 1e-4),
        };

        var d = SeriesDiagnostics.Summarize(days);

        Assert.Equal(Start, d.FirstDate);
        Assert.Equal(Start.AddDays(5), d.LastDate);
    }

    [Fact]
    public void TheLargestCalendarGapIsReported()
    {
        var days = new List<RealizedVolatilityDay>
        {
            Day(Start, 1e-4), Day(Start.AddDays(1), 1e-4), Day(Start.AddDays(9), 1e-4), Day(Start.AddDays(10), 1e-4),
        };

        Assert.Equal(8, SeriesDiagnostics.Summarize(days).LargestGapDays);
    }

    [Fact]
    public void ASingleSessionHasNoGap() =>
        Assert.Equal(0, SeriesDiagnostics.Summarize([Day(Start, 1e-4)]).LargestGapDays);

    // ---------- diagnostics: distribution ----------

    [Fact]
    public void TheVolatilityDistributionIsReportedOverCompleteSessionsOnly()
    {
        var days = new List<RealizedVolatilityDay>
        {
            DayAtVol(Start, 0.10),
            DayAtVol(Start.AddDays(1), 0.20),
            DayAtVol(Start.AddDays(2), 0.30),
        };
        // An incomplete session at an extreme level must not move the reported range.
        var incomplete = DayAtVol(Start.AddDays(3), 9.0);
        incomplete.IsComplete = false;
        days.Add(incomplete);

        var d = SeriesDiagnostics.Summarize(days);

        Assert.Equal(0.10, d.MinAnnualizedVolatility, 9);
        Assert.Equal(0.30, d.MaxAnnualizedVolatility, 9);
        Assert.Equal(0.20, d.MedianAnnualizedVolatility, 9);
        Assert.Equal(0.20, d.MeanAnnualizedVolatility, 9);
    }

    [Fact]
    public void TheMedianAveragesTheMiddlePairOnAnEvenCount()
    {
        var days = new List<RealizedVolatilityDay>
        {
            DayAtVol(Start, 0.10), DayAtVol(Start.AddDays(1), 0.20),
            DayAtVol(Start.AddDays(2), 0.30), DayAtVol(Start.AddDays(3), 0.40),
        };

        Assert.Equal(0.25, SeriesDiagnostics.Summarize(days).MedianAnnualizedVolatility, 9);
    }

    [Fact]
    public void ASeriesWithNoCompleteSessionsReportsZeroesRatherThanThrowing()
    {
        var d = SeriesDiagnostics.Summarize([Day(Start, 0.0), Day(Start.AddDays(1), 0.0)]);

        Assert.Equal(0, d.CompleteSessions);
        Assert.Equal(0.0, d.MedianAnnualizedVolatility);
        Assert.Equal(0.0, d.MeanAnnualizedVolatility);
        Assert.Equal(0.0, d.MinAnnualizedVolatility);
        Assert.Equal(0.0, d.MaxAnnualizedVolatility);
    }

    // ---------- diagnostics: outliers ----------

    [Fact]
    public void ImplausibleSessionsAreFlaggedWorstFirst()
    {
        var days = new List<RealizedVolatilityDay>
        {
            DayAtVol(Start, 0.15),
            DayAtVol(Start.AddDays(1), 4.0),
            DayAtVol(Start.AddDays(2), 8.0),
        };

        var outliers = SeriesDiagnostics.Summarize(days).Outliers;

        Assert.Equal(2, outliers.Count);
        Assert.Equal(Start.AddDays(2), outliers[0].Date);
        Assert.Equal(Start.AddDays(1), outliers[1].Date);
    }

    [Fact]
    public void TheDefaultOutlierThresholdIsThreeHundredPercent()
    {
        // 300% is far above anything a broad index realizes over a session, so the default
        // only catches faults. Just under it is not flagged; just over it is.
        Assert.Empty(SeriesDiagnostics.Summarize([DayAtVol(Start, 2.99)]).Outliers);
        Assert.Single(SeriesDiagnostics.Summarize([DayAtVol(Start, 3.01)]).Outliers);
    }

    [Fact]
    public void ExactlyAtTheThresholdIsNotAnOutlier() =>
        // The comparison is strict, so the threshold itself is still plausible.
        Assert.Empty(SeriesDiagnostics.Summarize([DayAtVol(Start, 3.0)], implausibleAnnualizedVolatility: 3.0).Outliers);

    [Fact]
    public void TheOutlierThresholdIsConfigurable()
    {
        var days = new List<RealizedVolatilityDay> { DayAtVol(Start, 0.5), DayAtVol(Start.AddDays(1), 0.15) };

        Assert.Single(SeriesDiagnostics.Summarize(days, implausibleAnnualizedVolatility: 0.3).Outliers);
    }

    [Fact]
    public void OutliersAreFlaggedRegardlessOfCompleteness()
    {
        // A fault severe enough to be an outlier usually also fails the completeness check;
        // it must still be surfaced rather than hidden by that.
        var incomplete = DayAtVol(Start, 9.0);
        incomplete.IsComplete = false;

        Assert.Single(SeriesDiagnostics.Summarize([incomplete]).Outliers);
    }

    [Fact]
    public void TheSummaryRendersItsCountsAndRange()
    {
        var text = SeriesDiagnostics.Summarize(
        [
            DayAtVol(Start, 0.10),
            DayAtVol(Start.AddDays(1), 0.30),
        ]).ToString();

        Assert.Contains("SPY", text, StringComparison.Ordinal);
        Assert.Contains("2 sessions", text, StringComparison.Ordinal);
        Assert.Contains("2024-01-02", text, StringComparison.Ordinal);
        Assert.Contains("outlier session(s) flagged", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSummaryRendersWithoutAnOutlierList() =>
        // Outliers is settable, so a hand-built instance can carry null; ToString must cope.
        Assert.Contains("0 outlier session(s)", new SeriesDiagnostics { Symbol = "SPY", Outliers = null }.ToString(),
            StringComparison.Ordinal);

    // ---------- csv ----------

    private static string WriteToTemp(IEnumerable<RealizedVolatilityDay> days)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");
        RealizedVolatilityCsv.Write(path, days);
        return path;
    }

    [Fact]
    public void CsvWriteRejectsMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => RealizedVolatilityCsv.Write(null!, []));
        Assert.Throws<ArgumentNullException>(() => RealizedVolatilityCsv.Write("x.csv", null!));
    }

    [Fact]
    public void TheHeaderNamesEveryColumnInOrder()
    {
        var path = WriteToTemp([]);
        try
        {
            Assert.Equal(
                "symbol,date,annualized_vol,total_variance,intraday_variance,bipower,jump," +
                "upside,downside,quarticity,overnight_return,close_to_close_return,dividend," +
                "returns,stale,session_open,session_close,first_bar,last_bar,short_session,complete",
                File.ReadAllLines(path)[0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnEmptySeriesStillWritesTheHeader()
    {
        var path = WriteToTemp([]);
        try
        {
            Assert.Single(File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EverySessionBecomesOneRow()
    {
        var days = Enumerable.Range(0, 5).Select(i => Day(Start.AddDays(i), 1e-4)).ToList();
        var path = WriteToTemp(days);
        try
        {
            Assert.Equal(6, File.ReadAllLines(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ARowCarriesEveryFieldInTheHeaderOrder()
    {
        var day = new RealizedVolatilityDay
        {
            Symbol = "SPX",
            Date = new DateTime(2024, 3, 4),
            TotalVariance = 1.5e-4,
            IntradayVariance = 1.25e-4,
            BipowerVariation = 1.1e-4,
            JumpVariation = 1.5e-5,
            UpsideVariance = 6e-5,
            DownsideVariance = 6.5e-5,
            RealizedQuarticity = 2.5e-8,
            OvernightReturn = -0.0025,
            CloseToCloseReturn = 0.0015,
            DividendAdjustment = 1.25,
            ReturnCount = 78,
            StaleSamples = 4,
            SessionOpen = 500.25,
            SessionClose = 502.75,
            FirstBarTime = new DateTime(2024, 3, 4, 9, 31, 0),
            LastBarTime = new DateTime(2024, 3, 4, 16, 0, 0),
            IsShortSession = true,
            IsComplete = false,
        };

        var path = WriteToTemp([day]);
        try
        {
            var fields = File.ReadAllLines(path)[1].Split(',');
            var header = File.ReadAllLines(path)[0].Split(',');

            Assert.Equal(header.Length, fields.Length);
            Assert.Equal("SPX", fields[0]);
            Assert.Equal("2024-03-04", fields[1]);
            Assert.Equal(day.AnnualizedVolatility, double.Parse(fields[2], CultureInfo.InvariantCulture), 15);
            Assert.Equal(1.5e-4, double.Parse(fields[3], CultureInfo.InvariantCulture), 15);
            Assert.Equal(1.25e-4, double.Parse(fields[4], CultureInfo.InvariantCulture), 15);
            Assert.Equal(1.1e-4, double.Parse(fields[5], CultureInfo.InvariantCulture), 15);
            Assert.Equal(1.5e-5, double.Parse(fields[6], CultureInfo.InvariantCulture), 15);
            Assert.Equal(6e-5, double.Parse(fields[7], CultureInfo.InvariantCulture), 15);
            Assert.Equal(6.5e-5, double.Parse(fields[8], CultureInfo.InvariantCulture), 15);
            Assert.Equal(2.5e-8, double.Parse(fields[9], CultureInfo.InvariantCulture), 15);
            Assert.Equal(-0.0025, double.Parse(fields[10], CultureInfo.InvariantCulture), 15);
            Assert.Equal(0.0015, double.Parse(fields[11], CultureInfo.InvariantCulture), 15);
            Assert.Equal(1.25, double.Parse(fields[12], CultureInfo.InvariantCulture), 15);
            Assert.Equal("78", fields[13]);
            Assert.Equal("4", fields[14]);
            Assert.Equal(500.25, double.Parse(fields[15], CultureInfo.InvariantCulture), 15);
            Assert.Equal(502.75, double.Parse(fields[16], CultureInfo.InvariantCulture), 15);
            Assert.Equal("2024-03-04 09:31:00", fields[17]);
            Assert.Equal("2024-03-04 16:00:00", fields[18]);

            // Flags are written as 1/0 rather than True/False so the file loads numerically.
            Assert.Equal("1", fields[19]);
            Assert.Equal("0", fields[20]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FlagsInvertCorrectly()
    {
        var day = Day(Start, 1e-4, shortSession: false);
        day.IsComplete = true;

        var path = WriteToTemp([day]);
        try
        {
            var fields = File.ReadAllLines(path)[1].Split(',');
            Assert.Equal("0", fields[19]);
            Assert.Equal("1", fields[20]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NumbersAreWrittenAtFullPrecisionAndRoundTrip()
    {
        // G17 round-trips a double exactly; a shorter format silently loses the low bits of
        // a variance that is already near 1e-8.
        var day = Day(Start, 1.2345678901234567e-7);

        var path = WriteToTemp([day]);
        try
        {
            var fields = File.ReadAllLines(path)[1].Split(',');
            Assert.Equal(1.2345678901234567e-7, double.Parse(fields[3], CultureInfo.InvariantCulture));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WritingReplacesAnyExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");
        try
        {
            RealizedVolatilityCsv.Write(path, Enumerable.Range(0, 5).Select(i => Day(Start.AddDays(i), 1e-4)));
            RealizedVolatilityCsv.Write(path, [Day(Start, 1e-4)]);

            Assert.Equal(2, File.ReadAllLines(path).Length);
        }
        finally { File.Delete(path); }
    }
}
