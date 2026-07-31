using Microsoft.Extensions.Options;

namespace TradingStuff.ResearchService.Sessions;

/// <summary>
/// Knobs for keeping <c>research.sessions</c> current, bound from the <c>Sessions</c> configuration
/// section.
/// </summary>
public sealed class SessionCalendarOptions
{
    /// <summary>
    /// On by default, unlike <c>Backfill:Enabled</c>. Syncing costs one bulk upsert of a few tens of
    /// thousands of rows and burns no TWS request budget, and switching it off leaves every SQL-side
    /// consumer without the session boundaries it joins against.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Earliest trading date to materialise. Per-calendar <c>effectiveFrom</c> clamps this upward, so
    /// a date before an instrument existed produces nothing rather than inventing sessions. The
    /// default is the SPY head timestamp's year — the deepest history any backfill job targets.
    /// </summary>
    public DateOnly SyncFrom { get; set; } = new(1993, 1, 1);

    /// <summary>
    /// How far past today to project sessions. Kept modest on purpose: past the calendar data's
    /// <c>knownGoodThrough</c> horizon a generated session is a rule projection containing no closure
    /// that has not yet been announced, and <see cref="SessionCalendarService.SyncAsync"/> logs a
    /// warning for every calendar that crosses it. A ~3-month horizon is enough for "what is tomorrow's
    /// session" without turning that warning into background noise.
    /// </summary>
    public int SyncForwardDays { get; set; } = 90;

    /// <summary>
    /// How often the range is re-materialised. Twice a day is enough for the only two things that
    /// change without a redeploy: the forward horizon rolling over midnight, and an operator editing
    /// a closure into <c>exchange-calendars.json</c> in a sibling process.
    /// </summary>
    public int ResyncIntervalHours { get; set; } = 12;

    /// <summary>How long to wait before retrying a failed pass (Postgres still starting, migration 004 not yet applied).</summary>
    public int RetrySeconds { get; set; } = 15;
}

/// <summary>What the last sync pass did, for <c>GET /research/sessions</c>.</summary>
public sealed record SessionCalendarSyncState(
    string Status, // disabled | not-configured | pending | synced | failed
    DateTimeOffset? LastSyncedAt,
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<SessionCalendarSyncResult> Results,
    string? Error);

/// <summary>
/// Keeps <c>research.sessions</c> equal to what <see cref="SessionGenerator"/> produces, on startup
/// and then on a slow timer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a background service at all</b>, given that the generator is pure and the table is
/// only a materialised view of it. Two reasons, and neither is "because we can":
/// </para>
/// <list type="number">
/// <item>
/// The table is the SQL side's only access to session boundaries — <see cref="Recording.CoverageMonitor"/>
/// takes its denominator from it, and the Phase 3/4 snapshot and cutoff joins will too. Nothing else
/// writes it. Without a writer the table is empty, and an empty session table does not fail loudly:
/// it shrinks every denominator, which makes coverage read HIGHER. A one-shot startup sync would fix
/// that, which brings us to the second reason.
/// </item>
/// <item>
/// The synced range ends at <c>today + SyncForwardDays</c>, and "today" moves. This service is
/// long-running by design (it lives beside the recorder, which runs for weeks), so a startup-only
/// sync would silently stop covering the present after the horizon elapsed. The timer exists to move
/// the horizon, not to detect change — a pass that finds nothing to do reports zeros and writes no
/// rows, because <see cref="SessionCalendarService"/>'s upsert skips rows that are already identical.
/// </item>
/// </list>
/// <para>
/// It is deliberately NOT a trigger-on-demand path from the coverage endpoint. Regenerating the
/// calendar is the one operation that can retire a published session row, and that belongs on a
/// predictable schedule an operator can read in the logs, not on whoever happens to refresh a page.
/// </para>
/// </remarks>
public sealed class SessionCalendarSynchronizer(
    SessionCalendarService calendar,
    IConfiguration configuration,
    IOptions<SessionCalendarOptions> options,
    ILogger<SessionCalendarSynchronizer> logger)
    : BackgroundService
{
    private readonly SessionCalendarOptions _options = options.Value;

    private volatile SessionCalendarSyncState _state = new("pending", null, null, null, [], null);

    /// <summary>What the last pass did. Reported by <c>GET /research/sessions</c>.</summary>
    public SessionCalendarSyncState State => _state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogWarning(
                "Session calendar sync is disabled; research.sessions will not be maintained and every " +
                "SQL-side consumer of session boundaries (coverage denominators first) will read an " +
                "out-of-date table.");
            _state = new SessionCalendarSyncState("disabled", null, null, null, [], null);

            return;
        }

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("trading")))
        {
            logger.LogWarning("No 'trading' connection string; the session calendar cannot be maintained.");
            _state = new SessionCalendarSyncState(
                "not-configured", null, null, null, [], "No 'trading' connection string.");

            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var from = _options.SyncFrom;
            var to = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(_options.SyncForwardDays);
            TimeSpan nextDelay;

            try
            {
                var results = await calendar.SyncAllAsync(from, to, stoppingToken);

                _state = new SessionCalendarSyncState(
                    "synced", DateTimeOffset.UtcNow, from, to, results, null);
                nextDelay = TimeSpan.FromHours(_options.ResyncIntervalHours);

                var changed = results.Sum(result => result.Inserted + result.Updated + result.Deleted);

                if (changed > 0)
                {
                    logger.LogInformation(
                        "Session calendar synced {From:yyyy-MM-dd}..{To:yyyy-MM-dd} across {Calendars} calendar(s); " +
                        "{Changed} row(s) changed.",
                        from, to, results.Count, changed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Overwhelmingly this is the schema not being there yet: MigrationRunner retries
                // migration 004 in the background and research.sessions does not exist until it lands.
                _state = new SessionCalendarSyncState("failed", _state.LastSyncedAt, from, to, _state.Results, ex.Message);
                nextDelay = TimeSpan.FromSeconds(_options.RetrySeconds);

                logger.LogWarning(
                    ex, "Session calendar sync failed; retrying in {Delay}s.", nextDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(nextDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
