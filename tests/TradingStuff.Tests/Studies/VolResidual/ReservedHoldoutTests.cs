using TradingStuff.ResearchService.Studies.VolResidual;

namespace TradingStuff.Tests.Studies.VolResidual;

/// <summary>
/// Pins the one behaviour the whole study runner depends on being airtight: nothing dated inside
/// the registered holdout (2024-01-01..2026-07-31) may ever come out of
/// <see cref="ReservedHoldout.ClampToExcludeHoldout"/>, no matter what a caller asks for.
/// </summary>
public class ReservedHoldoutTests
{
    private static readonly DateOnly EarliestAvailable = new(2010, 1, 1);

    [Fact]
    public void ARequestEntirelyBeforeTheHoldoutIsUntouched()
    {
        var clamp = ReservedHoldout.ClampToExcludeHoldout(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), EarliestAvailable);

        Assert.False(clamp.WasClamped);
        Assert.False(clamp.IsEmpty);
        Assert.Equal(new DateOnly(2020, 1, 1), clamp.From);
        Assert.Equal(new DateOnly(2020, 12, 31), clamp.To);
    }

    [Fact]
    public void ARequestOverlappingTheHoldoutIsClampedToTheDayBefore()
    {
        // Requested range straddles the holdout boundary: 2023-06-01 .. 2025-01-01.
        var clamp = ReservedHoldout.ClampToExcludeHoldout(new DateOnly(2023, 6, 1), new DateOnly(2025, 1, 1), EarliestAvailable);

        Assert.True(clamp.WasClamped);
        Assert.False(clamp.IsEmpty);
        Assert.Equal(new DateOnly(2023, 6, 1), clamp.From);
        Assert.Equal(new DateOnly(2023, 12, 31), clamp.To); // the day before ReservedHoldout.Start
        Assert.True(clamp.To < ReservedHoldout.Start);
    }

    [Fact]
    public void ARequestEntirelyInsideTheHoldoutProducesAnEmptyWindow()
    {
        var clamp = ReservedHoldout.ClampToExcludeHoldout(new DateOnly(2024, 3, 1), new DateOnly(2024, 6, 1), EarliestAvailable);

        Assert.True(clamp.IsEmpty);
        Assert.True(clamp.From > clamp.To);
    }

    [Fact]
    public void ARequestEntirelyAfterTheHoldoutProducesAnEmptyWindow()
    {
        var clamp = ReservedHoldout.ClampToExcludeHoldout(new DateOnly(2026, 8, 1), new DateOnly(2026, 12, 31), EarliestAvailable);

        Assert.True(clamp.IsEmpty);
    }

    [Fact]
    public void AnUnboundedRequestDefaultsToEarliestAvailableThroughTheDayBeforeTheHoldout()
    {
        var clamp = ReservedHoldout.ClampToExcludeHoldout(null, null, EarliestAvailable);

        Assert.False(clamp.IsEmpty);
        Assert.Equal(EarliestAvailable, clamp.From);
        Assert.Equal(ReservedHoldout.LastUsableDate, clamp.To);
    }

    [Fact]
    public void ARequestBelowTheAvailabilityFloorIsClampedUp()
    {
        var clamp = ReservedHoldout.ClampToExcludeHoldout(new DateOnly(1990, 1, 1), new DateOnly(2015, 1, 1), EarliestAvailable);

        Assert.True(clamp.WasClamped);
        Assert.Equal(EarliestAvailable, clamp.From);
    }

    [Theory]
    [InlineData("2023-12-31", "2024-01-01", true)]   // touches the first holdout day
    [InlineData("2026-07-31", "2026-08-15", true)]   // touches the last holdout day
    [InlineData("2023-01-01", "2023-12-31", false)]  // entirely before
    [InlineData("2026-08-01", "2026-12-31", false)]  // entirely after
    public void OverlapsMatchesTheInclusiveHoldoutBoundary(string from, string to, bool expected) =>
        Assert.Equal(expected, ReservedHoldout.Overlaps(DateOnly.Parse(from), DateOnly.Parse(to)));

    [Fact]
    public void TheRegisteredHoldoutDatesMatchThePreregistration()
    {
        Assert.Equal(new DateOnly(2024, 1, 1), ReservedHoldout.Start);
        Assert.Equal(new DateOnly(2026, 7, 31), ReservedHoldout.End);
    }
}
