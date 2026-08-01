using Npgsql;
using TradingStuff.ResearchService.Gateway;

namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// Reads SPX 1-minute bars and VIX daily closes straight out of <c>research.bars</c> for this
/// study's development runner. There is deliberately no write path here and no dependency on the
/// backfill machinery beyond the table it lands rows in — this is a read-only consumer, same as
/// <c>CoverageMonitor</c> and <c>GapDetector</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a known, stated deviation from the pre-registration for VIX.</b> The registration
/// requires "Cboe's official daily index history, not an IBKR index <c>TRADES</c> bar" for VIX,
/// specifically because an index has no trades and a reconstructed bar carries ambiguous
/// construction semantics a registered baseline should not inherit. This development runner has no
/// Cboe feed available and reads the IBKR-recorded <c>research.bars</c> VIX daily close instead. That
/// is acceptable for a development run whose entire purpose is exercising the pipeline's plumbing
/// while the backfill is still draining, and is called out here and in the study runner's output so
/// it is never mistaken for the registered data source when this pipeline is later pointed at a real
/// run.
/// </para>
/// <para>
/// Instrument ids are the seeded rows from migration 001 (<c>research.instruments</c>): 1 = SPX
/// index, 4 = VIX index. Hardcoded rather than looked up, matching
/// <see cref="TradingStuff.ResearchService.Backfill.BackfillJobCatalog"/>'s own job definitions,
/// which hardcode the same ids for the same instruments.
/// </para>
/// </remarks>
public sealed class VolResidualBarLoader(IConfiguration configuration)
{
    private const short SpxIndexInstrumentId = 1;
    private const short VixIndexInstrumentId = 4;

    public string? ConnectionString => configuration.GetConnectionString("trading");

    /// <summary>
    /// SPX 1-minute TRADES/RTH bars, UTC-dated <c>[from, to]</c> inclusive of both calendar dates.
    /// Converted directly to <see cref="TradingStuff.Volatility.IntradayBar"/> via the existing
    /// decimal-to-double boundary adapter — this loader performs no timezone conversion and holds no
    /// session logic of its own, per the platform's UTC-canonical doctrine.
    /// </summary>
    public async Task<List<HistoricalBarDto>> LoadSpxOneMinuteBarsAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toUtcExclusive = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT ts_utc, open, high, low, close, volume " +
            "FROM research.bars " +
            "WHERE instrument_id = $1 AND bar_size = '1 min' AND what_to_show = 'TRADES' AND use_rth = true " +
            "  AND ts_utc >= $2 AND ts_utc < $3 " +
            "ORDER BY ts_utc",
            connection)
        {
            Parameters =
            {
                new() { Value = SpxIndexInstrumentId },
                new() { Value = fromUtc },
                new() { Value = toUtcExclusive },
            },
        };

        var bars = new List<HistoricalBarDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bars.Add(new HistoricalBarDto(
                Timestamp: reader.GetFieldValue<DateTimeOffset>(0),
                TradingDate: null,
                Open: reader.GetFieldValue<decimal>(1),
                High: reader.GetFieldValue<decimal>(2),
                Low: reader.GetFieldValue<decimal>(3),
                Close: reader.GetFieldValue<decimal>(4),
                Volume: reader.IsDBNull(5) ? 0m : reader.GetFieldValue<decimal>(5),
                Count: 0,
                Wap: 0m));
        }

        return bars;
    }

    /// <summary>
    /// VIX daily closes, keyed by <c>trading_date</c>, for <c>[from, to]</c> inclusive. See this
    /// type's remarks for why this is IBKR's recorded daily bar rather than the registration's
    /// Cboe official history.
    /// </summary>
    public async Task<Dictionary<DateOnly, double>> LoadVixDailyClosesAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT trading_date, close " +
            "FROM research.bars " +
            "WHERE instrument_id = $1 AND bar_size = '1 day' AND what_to_show = 'TRADES' AND use_rth = true " +
            "  AND trading_date >= $2 AND trading_date <= $3 " +
            "ORDER BY trading_date",
            connection)
        {
            Parameters =
            {
                new() { Value = VixIndexInstrumentId },
                new() { Value = from },
                new() { Value = to },
            },
        };

        var closes = new Dictionary<DateOnly, double>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0)) continue;
            var date = reader.GetFieldValue<DateOnly>(0);
            var close = (double)reader.GetFieldValue<decimal>(1);
            closes[date] = close;
        }

        return closes;
    }
}
