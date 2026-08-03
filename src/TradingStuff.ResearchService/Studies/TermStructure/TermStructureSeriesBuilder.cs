using Npgsql;
using TradingStuff.ResearchContracts;
using TradingStuff.Volatility.ImpliedVolatility;

namespace TradingStuff.ResearchService.Studies.TermStructure;

/// <summary>
/// Builds and persists the A4 term-structure series over a session-date range, exactly per the
/// frozen construction (docs/research/a4-slope-construction.md). This class owns the parts the
/// pure <see cref="TermStructureBuilder"/> cannot: the session calendar (§ 2 — dates come from
/// OUR calendar, never from quote presence), the snapshot instant (§ 3 — 15:30 ET as a UTC
/// instant), slice assembly with per-root settlement moments (§ 4), the underlying diagnostic
/// (§ 7), and the unresolved-vs-absent distinction (§ 8).
/// </summary>
public sealed class TermStructureSeriesBuilder(
    TermStructureStore store,
    ISessionClock sessionClock,
    IConfiguration configuration,
    ILogger<TermStructureSeriesBuilder> logger)
{
    private const string Calendar = "CBOE_SPX_RTH";
    private const short SpxInstrumentId = 1;

    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeOnly SnapshotEt = new(15, 30, 0);
    private static readonly TimeOnly AmSettlementEt = new(9, 30, 0);
    private static readonly TimeOnly PmSettlementEt = new(16, 0, 0);

    public sealed record BuildReport(int Sessions, int Usable, int Unusable, int Unresolved);

    public async Task<BuildReport> BuildAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var rates = await store.LoadRatesAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "research.risk_free_rates is empty. The frozen construction discounts with DTB4WK " +
                "(§ 7); load the rate history before building — a silent flat-rate fallback would " +
                "be a different construction pretending to be the declared one.");

        var builder = new TermStructureBuilder(rates);

        // § 8: every chain expiration ingestion has NOT terminally resolved, sorted. Per session
        // date, the first of these AFTER the date is the boundary: any bracket leg at or beyond
        // it may be wrong (a nearer, still-unfetched expiration could be the true leg), so dates
        // needing that region are 'unresolved', never 'absent'. The boundary must be computed
        // per date — a single global minimum degenerates once it lies in a date's past.
        var unresolvedExpirations = await UnresolvedExpirationsAsync(cancellationToken);

        int usable = 0, unusable = 0, unresolved = 0, sessions = 0;

        foreach (var session in sessionClock.SessionsBetween(Calendar, from, to))
        {
            if (session.Label != "RTH") continue;

            cancellationToken.ThrowIfCancellationRequested();
            sessions++;

            var row = await BuildSessionAsync(session.TradingDate, builder, unresolvedExpirations, cancellationToken);
            await store.SaveAsync(row, cancellationToken);

            switch (row.Status)
            {
                case "usable": usable++; break;
                case "unusable": unusable++; break;
                default: unresolved++; break;
            }
        }

        logger.LogInformation(
            "Term-structure build [{From}..{To}]: {Sessions} session(s) — {Usable} usable, " +
            "{Unusable} unusable, {Unresolved} unresolved (ingestion frontier {Frontier}).",
            from, to, sessions, usable, unusable, unresolved,
            unresolvedExpirations.Count == 0 ? "clear" : unresolvedExpirations[0].ToString("yyyy-MM-dd"));

        return new BuildReport(sessions, usable, unusable, unresolved);
    }

    private async Task<TermStructureRow> BuildSessionAsync(
        DateOnly tradingDate, TermStructureBuilder builder, IReadOnlyList<DateOnly> unresolvedExpirations,
        CancellationToken cancellationToken)
    {
        var snapshotUtc = EtInstant(tradingDate, SnapshotEt);

        var slices = await LoadSlicesAsync(tradingDate, snapshotUtc, cancellationToken);
        var day = builder.BuildDay(tradingDate.ToDateTime(TimeOnly.MinValue), slices);
        var underlying = await UnderlyingAtSnapshotAsync(snapshotUtc, cancellationToken);

        // THIS date's unresolved boundary: the first unresolved expiration strictly after it,
        // as the earliest instant it could settle (its AM settlement). Selected far legs must
        // land strictly before it for the brackets to be trustworthy; a failed point is only
        // trustworthy as genuine absence when no such expiration exists at all.
        var boundary = FirstAfter(unresolvedExpirations, tradingDate);
        var boundaryUtc = boundary is { } b ? EtInstant(b, AmSettlementEt) : DateTime.MaxValue;

        var nineTrustworthy = PointResolved(day.NineDay, snapshotUtc, boundaryUtc);
        var thirtyTrustworthy = PointResolved(day.ThirtyDay, snapshotUtc, boundaryUtc);

        string status;
        string? note;

        if (day.IsUsable && nineTrustworthy && thirtyTrustworthy)
        {
            status = "usable";
            note = null;
        }
        else if (boundary is { } frontier && (!nineTrustworthy || !thirtyTrustworthy))
        {
            // Ingestion has not resolved everything the brackets could need. Fetch-failure is
            // not absence: park the date and let a later rebuild replace it.
            status = "unresolved";
            note = $"Awaiting chain ingestion at or beyond {frontier:yyyy-MM-dd}. " +
                   $"9d: {day.NineDay.Note ?? "ok"}; 30d: {day.ThirtyDay.Note ?? "ok"}";
        }
        else
        {
            status = "unusable";
            note = $"9d: {day.NineDay.Note ?? "ok"}; 30d: {day.ThirtyDay.Note ?? "ok"}";
        }

        return new TermStructureRow(
            tradingDate, status, snapshotUtc,
            status == "usable" ? day.NineDay.Variance : null,
            status == "usable" ? day.ThirtyDay.Variance : null,
            status == "usable" ? day.Slope : null,
            Diag(day.NineDay?.NearTermDays), Diag(day.NineDay?.NextTermDays),
            day.NineDay is { IsUsable: true } n ? n.StrikesUsed : null,
            Diag(day.ThirtyDay?.NearTermDays), Diag(day.ThirtyDay?.NextTermDays),
            day.ThirtyDay is { IsUsable: true } t ? t.StrikesUsed : null,
            underlying, note);
    }

    /// <summary>
    /// A point's brackets are trustworthy when the point computed and its far leg settles
    /// strictly before the unresolved boundary; an unbracketed or failed point is trustworthy
    /// only if no unresolved expiration could have supplied the missing leg.
    /// </summary>
    private static bool PointResolved(TermStructurePoint point, DateTime snapshotUtc, DateTime boundaryUtc)
    {
        if (point is { IsUsable: true })
        {
            return snapshotUtc.AddDays(point.NextTermDays) < boundaryUtc;
        }

        // The point failed. If ANY unresolved expiration lies ahead of this date, it could be
        // the leg this date is missing, so the failure cannot be trusted as absence.
        return boundaryUtc == DateTime.MaxValue;
    }

    /// <summary>First element strictly after <paramref name="date"/>, or null. The list is sorted.</summary>
    private static DateOnly? FirstAfter(IReadOnlyList<DateOnly> sorted, DateOnly date)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] <= date) lo = mid + 1;
            else hi = mid;
        }
        return lo < sorted.Count ? sorted[lo] : null;
    }

    private static double? Diag(double? value) => value is 0.0 ? null : value;

    // ---- data loading ---------------------------------------------------------------------------

    private async Task<List<OptionChainSlice>> LoadSlicesAsync(
        DateOnly tradingDate, DateTime snapshotUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        // § 3: only rows carrying the snapshot instant's timestamp. § 4: slices keyed by
        // (root, settlement moment); the settlement moment is derived from the trading class —
        // AM-settled standard SPX at 09:30 ET on the settlement date, PM-settled SPXW at
        // 16:00 ET on the expiration date.
        await using var command = new NpgsqlCommand(
            """
            SELECT trading_class, expiration, strike, option_right, bid, ask
            FROM research.option_chain_quotes
            WHERE trading_date = $1 AND observed_at = $2
              AND underlying = 'SPX' AND trading_class IN ('SPX', 'SPXW')
            ORDER BY trading_class, expiration, strike
            """,
            connection);

        command.Parameters.Add(new() { Value = tradingDate });
        command.Parameters.Add(new() { Value = (DateTimeOffset)snapshotUtc });

        var slices = new Dictionary<(string Root, DateOnly Expiration), OptionChainSlice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var root = reader.GetString(0);
            var expiration = reader.GetFieldValue<DateOnly>(1);

            if (!slices.TryGetValue((root, expiration), out var slice))
            {
                slice = new OptionChainSlice
                {
                    Root = root,
                    ObservedAt = snapshotUtc,
                    SettlesAt = root == "SPXW"
                        ? EtInstant(expiration, PmSettlementEt)
                        : EtInstant(expiration, AmSettlementEt),
                };
                slices[(root, expiration)] = slice;
            }

            slice.Quotes.Add(new OptionQuote(
                (double)reader.GetDecimal(2),
                reader.GetString(3) == "C" ? OptionRight.Call : OptionRight.Put,
                reader.IsDBNull(4) ? 0.0 : (double)reader.GetDecimal(4),
                reader.IsDBNull(5) ? 0.0 : (double)reader.GetDecimal(5)));
        }

        return [.. slices.Values];
    }

    private async Task<IReadOnlyList<DateOnly>> UnresolvedExpirationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT r.expiration
            FROM research.option_chain_requests r
            JOIN research.option_chain_jobs j ON j.job_id = r.job_id
            WHERE j.underlying = 'SPX' AND r.state NOT IN ('succeeded', 'empty')
            ORDER BY r.expiration
            """,
            connection);

        var expirations = new List<DateOnly>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            expirations.Add(reader.GetFieldValue<DateOnly>(0));
        }

        return expirations;
    }

    private async Task<double?> UnderlyingAtSnapshotAsync(DateTime snapshotUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        // § 7: the SPX 1-minute bar at or last before the snapshot, diagnostics only. The
        // half-hour floor keeps a data hole from silently serving a morning print.
        await using var command = new NpgsqlCommand(
            """
            SELECT close FROM research.bars
            WHERE instrument_id = $1 AND bar_size = '1 min' AND what_to_show = 'TRADES'
              AND use_rth = true AND ts_utc <= $2 AND ts_utc > $3
            ORDER BY ts_utc DESC LIMIT 1
            """,
            connection);

        command.Parameters.Add(new() { Value = SpxInstrumentId });
        command.Parameters.Add(new() { Value = (DateTimeOffset)snapshotUtc });
        command.Parameters.Add(new() { Value = (DateTimeOffset)snapshotUtc.AddMinutes(-30) });

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is decimal close ? (double)close : null;
    }

    private static DateTime EtInstant(DateOnly date, TimeOnly timeEt) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(date.ToDateTime(timeEt), DateTimeKind.Unspecified), Eastern);

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("trading");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("No 'trading' connection string is configured.");

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
