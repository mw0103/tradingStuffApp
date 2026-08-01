using System;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Assertion helpers for the ported volatility suite, backed by xunit.
/// </summary>
/// <remarks>
/// <para>
/// The volatility and implied-variance tests arrived from the TradingBotIBKR
/// <c>TradingMLModel.Tests</c> console harness, where they ran against a hand-rolled
/// <c>TestAssert</c>. Keeping that call shape — <c>Check</c> for a tolerance comparison,
/// <c>IsTrue</c> for a predicate — and re-pointing it at xunit ports all 294 original checks
/// verbatim instead of restating them. Restating is where a numerical suite silently loses
/// tolerances, and these tolerances are the assertions.
/// </para>
/// <para>
/// The one rename: the original helper was <c>Assert</c>, which collides with the
/// <see cref="Xunit.Assert"/> type this project imports globally.
/// </para>
/// </remarks>
internal static class VolatilityAssert
{
    /// <summary>Asserts <paramref name="actual"/> is within <paramref name="tolerance"/> of <paramref name="expected"/>.</summary>
    public static void Check(string name, double actual, double expected, double tolerance)
    {
        // G8 rather than a fixed precision: these values span variances near 1e-8 and
        // quarticities in the thousands, and a fixed format renders one of them as "0".
        Xunit.Assert.True(
            Math.Abs(actual - expected) <= tolerance,
            string.Format("{0} (got {1:G8}, expected {2:G8}, tolerance {3:G8})", name, actual, expected, tolerance));
    }

    /// <summary>Asserts <paramref name="condition"/> holds.</summary>
    public static void IsTrue(string name, bool condition) => Xunit.Assert.True(condition, name);

    /// <summary>Records a diagnostic value that is measured but not asserted on.</summary>
    public static void Info(string message) => Console.WriteLine("    [info] " + message);

    /// <summary>Asserts <paramref name="action"/> throws.</summary>
    public static void Throws(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            return;
        }

        Xunit.Assert.Fail(name + " (expected an exception, none thrown)");
    }

    /// <summary>Box-Muller standard normal, so the simulations are reproducible from a seed.</summary>
    public static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
