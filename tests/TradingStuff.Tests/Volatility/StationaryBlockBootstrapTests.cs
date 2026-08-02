using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the Politis-Romano stationary block bootstrap used for H1's confidence-interval condition.
/// </summary>
/// <remarks>
/// Two properties carry the weight. Reproducibility is a registered requirement — a published lower
/// bound that moves between runs is not a bound — and the BLOCKING has to actually happen: a
/// bootstrap that resampled observations independently would ignore the serial dependence it exists
/// to accommodate, and would report a far too narrow interval on exactly the persistent loss
/// differentials this study produces.
/// </remarks>
public class StationaryBlockBootstrapTests
{
    private static double[] PersistentSeries(int n, double mean, int seed)
    {
        var rng = new Random(seed);
        var values = new double[n];
        var level = 0.0;
        for (var i = 0; i < n; i++)
        {
            level = 0.95 * level + (rng.NextDouble() - 0.5) * 0.02;
            values[i] = mean + level;
        }

        return values;
    }

    [Fact]
    public void AnIdenticalRerunProducesAnIdenticalInterval()
    {
        var series = PersistentSeries(500, 0.004, seed: 3);

        var first = StationaryBlockBootstrap.LowerBound(series, seed: 12345);
        var second = StationaryBlockBootstrap.LowerBound(series, seed: 12345);

        // Bit-for-bit, not "close": the requirement is reproducibility, not approximate agreement.
        Assert.Equal(first.LowerBound, second.LowerBound);
        Assert.Equal(first.SampleMean, second.SampleMean);
        Assert.Equal(12345UL, first.Seed);
    }

    [Fact]
    public void ADifferentSeedProducesADifferentInterval()
    {
        // Otherwise "reproducible" would be satisfied by a constant, and the test above would pass
        // against an implementation that never resampled at all.
        var series = PersistentSeries(500, 0.004, seed: 3);

        Assert.NotEqual(
            StationaryBlockBootstrap.LowerBound(series, seed: 1).LowerBound,
            StationaryBlockBootstrap.LowerBound(series, seed: 2).LowerBound);
    }

    [Fact]
    public void TheBlockLengthWidensTheIntervalOnAPersistentSeries()
    {
        // The whole point of blocking. Longer blocks preserve more of the dependence, so the
        // resampled means scatter more and the lower bound falls. A bootstrap that ignored block
        // structure would show no relationship here.
        var series = PersistentSeries(600, 0.004, seed: 5);

        var iid = StationaryBlockBootstrap.LowerBound(series, seed: 99, meanBlockLength: 1.0, resamples: 4000);
        var blocked = StationaryBlockBootstrap.LowerBound(series, seed: 99, meanBlockLength: 20.0, resamples: 4000);

        Assert.True(blocked.LowerBound < iid.LowerBound,
            $"blocked lower bound {blocked.LowerBound} should sit below the iid bound {iid.LowerBound}.");
    }

    [Fact]
    public void AClearlyPositiveMeanExcludesZeroAndAZeroMeanDoesNot()
    {
        var strong = StationaryBlockBootstrap.LowerBound(PersistentSeries(500, 0.5, seed: 7), seed: 4);
        var none = StationaryBlockBootstrap.LowerBound(PersistentSeries(500, 0.0, seed: 7), seed: 4);

        Assert.True(strong.ExcludesZero);
        Assert.False(none.ExcludesZero);
    }

    [Fact]
    public void TheSampleMeanIsTheObservedMeanNotABootstrapArtefact()
    {
        double[] series = [1.0, 2.0, 3.0, 4.0];
        Assert.Equal(2.5, StationaryBlockBootstrap.LowerBound(series, seed: 1, resamples: 10).SampleMean, 12);
    }

    [Fact]
    public void TheDefaultsAreTheRegisteredOnes()
    {
        var result = StationaryBlockBootstrap.LowerBound(PersistentSeries(50, 0.01, seed: 2), seed: 1);

        Assert.Equal(20.0, result.MeanBlockLength);
        Assert.Equal(10000, result.Resamples);
        Assert.Equal(0.05, result.Alpha);
    }

    [Fact]
    public void MalformedInputIsRejected()
    {
        Assert.Equal("differentials",
            Assert.Throws<ArgumentNullException>(() => StationaryBlockBootstrap.LowerBound(null!, 1)).ParamName);
        Assert.Equal("differentials",
            Assert.Throws<ArgumentException>(() => StationaryBlockBootstrap.LowerBound([1.0], 1)).ParamName);
        Assert.Equal("meanBlockLength", Assert.Throws<ArgumentOutOfRangeException>(
            () => StationaryBlockBootstrap.LowerBound([1.0, 2.0], 1, meanBlockLength: 0.5)).ParamName);
        Assert.Equal("resamples", Assert.Throws<ArgumentOutOfRangeException>(
            () => StationaryBlockBootstrap.LowerBound([1.0, 2.0], 1, resamples: 0)).ParamName);
        Assert.Equal("alpha", Assert.Throws<ArgumentOutOfRangeException>(
            () => StationaryBlockBootstrap.LowerBound([1.0, 2.0], 1, alpha: 1.0)).ParamName);
    }
}
