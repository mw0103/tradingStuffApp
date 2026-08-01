using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Threading.Channels;
using Npgsql;
using NpgsqlTypes;
using TradingStuff.ResearchContracts;

namespace TradingStuff.IbkrGateway.Recording;

/// <summary>
/// What <see cref="RecordingTickSink"/> needs from the recorder. Narrow on purpose: it lets the
/// sink's tick-accumulation logic — the part most worth testing in isolation — be exercised without
/// any Postgres dependency.
/// </summary>
internal interface IObservationSink
{
    void EnqueueOption(OptionQuoteObservation observation);

    void EnqueueUnderlying(UnderlyingTickObservation observation);

    /// <summary>
    /// A tick was observed on this lease after a re-issue: whatever gap its scope holds is closed.
    /// </summary>
    /// <param name="effectiveMarketDataType">
    /// What TWS has reported for the NEW ticker so far, or null if it has not reported yet. A
    /// non-live value here re-opens the non-live gap AFTER the close, because the close cannot tell
    /// one open reason from another and would otherwise silently retire an alarm nothing reopens.
    /// </param>
    void NotifyGapClosed(Guid leaseId, short? effectiveMarketDataType);

    /// <summary>
    /// TWS reported an effective market-data type other than 1 (live) for this lease's ticker: the
    /// recording continues, and it is recorded as a gap so it cannot pass silently for a live one.
    /// </summary>
    void NotifyNonLiveMarketData(Guid leaseId, int marketDataType);

    /// <summary>TWS reported live (1) for this lease's ticker; retires the non-live alarm.</summary>
    void NotifyLiveMarketData(Guid leaseId);
}

/// <summary>
/// Append-only recording of standing-subscription ticks into Postgres, and the permanent record of
/// when recording had a gap.
/// </summary>
/// <remarks>
/// The one deliberately over-engineered component in this platform (see
/// docs/plans/ibkr-edge-research-roadmap.md § architecture): live option data is unrecoverable, so
/// every enqueue call — invoked directly from the EReader pump thread via
/// <see cref="RecordingTickSink"/> — must be non-blocking, and every failure mode (Postgres down,
/// buffer saturation) degrades to a recorded gap rather than an exception that could reach the
/// pump. Draining happens on background tasks via Npgsql binary <c>COPY</c>, batched by size or
/// time, whichever comes first.
/// </remarks>
public sealed class ObservationRecorder : IObservationSink, IAsyncDisposable
{
    private const int ChannelCapacity = 50_000;

    // Hysteresis on the buffer-overflow gap: it opens at capacity (where DropOldest starts actually
    // dropping) but does not close until the backlog has halved. Without a low-water mark the gap
    // flaps once per drained batch under sustained saturation — the drain takes BatchSize, depth
    // dips below capacity, the pump refills it — which is ~10 rows a second into recorder_gaps, each
    // costing the drain loop an INSERT and an UPDATE on its critical path and so making the very
    // backlog it is reporting worse. The cost is that the gap slightly OVERSTATES the lossy
    // interval, covering some time when the buffer was merely deep rather than dropping. That is the
    // safe direction: an overstated gap fails a coverage gate that a human then reads, while an
    // understated one silently admits a day with holes in it into a study.
    private const int OverflowClearedDepth = ChannelCapacity / 2;

    private const int BatchSize = 5_000;
    private const int RetryDelayMilliseconds = 250;
    private const string OptionWriteScope = "recorder:write:option";
    private const string UnderlyingWriteScope = "recorder:write:underlying";
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultShutdownDrainTimeout = TimeSpan.FromSeconds(30);

    // How long the drain loops get to stop AFTER the deadline has already expired and cancellation
    // has been signalled. They only have to record what they are abandoning, so this covers one
    // failed round trip and no more; the metric is incremented before the round trip so the count
    // survives even when the gap row cannot be written.
    private static readonly TimeSpan AbandonTimeout = TimeSpan.FromSeconds(5);

    private readonly NpgsqlDataSource? _dataSource;
    private readonly ILogger<ObservationRecorder> _logger;
    private readonly Channel<OptionQuoteObservation>? _optionChannel;
    private readonly Channel<UnderlyingTickObservation>? _underlyingChannel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task[] _drainTasks;
    private readonly TimeSpan _shutdownDrainTimeout;

    private readonly OverflowGap _optionOverflow = new("recorder:option-buffer");
    private readonly OverflowGap _underlyingOverflow = new("recorder:underlying-buffer");

    // Process-local guard against opening a duplicate gap row for a scope that already has one
    // open. Gap rows themselves are the durable record; this dictionary only avoids spamming the
    // table while a condition (e.g. sustained buffer saturation) persists across many ticks.
    //
    // The REASON is carried alongside the id because one caller needs to close only the gap IT
    // opened: a non-live market-data alarm must retire when TWS reports live, but must not retire a
    // 'disconnect' row that happens to occupy the same scope — closing that one would stamp
    // closed_by='observed' ("a tick resumed") on an outage where nothing of the sort had happened,
    // and truncate it early. That is a fabricated measurement, which is the thing
    // docs/DECISIONS.md §8 exists to forbid.
    private readonly ConcurrentDictionary<string, OpenGap> _openGapIdByScope = new();

    /// <summary>One scope's currently-open gap row.</summary>
    /// <remarks>
    /// A readonly record struct so <see cref="ConcurrentDictionary{TKey,TValue}"/>'s
    /// compare-and-remove overload can be used: removing only if the value is still the one that
    /// was read makes "close the gap I looked at" a single atomic step, rather than a read followed
    /// by a remove that can take a row someone else opened in between.
    /// </remarks>
    private readonly record struct OpenGap(long GapId, string Reason);

    private readonly Counter<long> _eventsPersisted;
    private readonly Counter<long> _writeFailures;
    private readonly Counter<long> _bufferOverflows;

    // Only for logging: the orphan reconciliation is retried until it succeeds, and one error per
    // retry every sweep interval would bury the rest of the log.
    private int _orphanReconcileFailures;

    /// <summary>
    /// The buffer-overflow gap for one channel, held as a DESIRED state (is this buffer saturated?)
    /// that a reconciler converges the gap row onto, rather than as a pair of edges that whoever
    /// happens to notice them must act on.
    /// </summary>
    /// <remarks>
    /// The edge-driven version was wrong in a way that only shows up in production. The open was
    /// fired from the enqueue path and published its gap id into <c>_openGapIdByScope</c> only once
    /// its INSERT round trip had returned; the close was attempted by a LATER enqueue that observed
    /// both headroom and an entry in that dictionary. An enqueue landing inside the INSERT's window
    /// saw headroom but no entry, concluded there was nothing to close, and never looked again — so
    /// the row stayed open until some further tick on the same channel happened to arrive. For
    /// <c>recorder:underlying-buffer</c> that is SPX/VIX, whose index levels do not update through
    /// Cboe GTH at all: an overflow just before the RTH close leaves the row open all night and all
    /// weekend, and <c>CoverageMonitor</c> reads an unended gap as an outage overlapping EVERY later
    /// window. This is the third instance of the immortal-gap defect (Phases 1 and 2, docs/STATE.md).
    /// <para>
    /// The structural answer is that neither the open nor the close is an event anyone can miss.
    /// The pump thread only sets <see cref="MarkSaturated"/>; the drain loop — which is what
    /// actually frees capacity, and which unlike the pump may await — clears it, and then compares
    /// desired against actual on EVERY iteration, i.e. at least once per flush interval for as long
    /// as the recorder runs. A reconcile that loses a race, reads a stale desired state, or fails
    /// against a down Postgres is therefore retried within one flush interval instead of waiting for
    /// a tick that may never come. <see cref="TryBeginReconcile"/> keeps one scope's open/close
    /// strictly serial, which is what closes the in-flight-INSERT window: the dictionary is only
    /// mutated for a buffer scope from inside that critical section, so no one can observe the
    /// half-open state at all.
    /// </para>
    /// </remarks>
    private sealed class OverflowGap(string scope)
    {
        private int _saturated;
        private int _reconciling;

        public string Scope { get; } = scope;

        public bool Saturated => Volatile.Read(ref _saturated) == 1;

        /// <summary>Returns true only on the not-saturated → saturated edge.</summary>
        public bool MarkSaturated() => Interlocked.Exchange(ref _saturated, 1) == 0;

        public void ClearSaturated() => Volatile.Write(ref _saturated, 0);

        /// <summary>
        /// Non-blocking: a caller that loses gets nothing and returns. Both callers are threads that
        /// must not sit on a Postgres round trip — the EReader pump (never) and the drain loop
        /// (whose stall is itself buffer pressure) — and the periodic re-check makes losing harmless.
        /// </summary>
        public bool TryBeginReconcile() => Interlocked.CompareExchange(ref _reconciling, 1, 0) == 0;

        public void EndReconcile() => Volatile.Write(ref _reconciling, 0);
    }

    public ObservationRecorder(IConfiguration configuration, IMeterFactory meterFactory, ILogger<ObservationRecorder> logger)
    {
        _logger = logger;
        _shutdownDrainTimeout = ReadShutdownDrainTimeout(configuration);

        var meter = meterFactory.Create("TradingStuff.IbkrGateway");
        _eventsPersisted = meter.CreateCounter<long>("gateway.recorder.events_persisted");
        _writeFailures = meter.CreateCounter<long>("gateway.recorder.write_failures");
        _bufferOverflows = meter.CreateCounter<long>("gateway.recorder.buffer_overflows");
        meter.CreateObservableGauge("gateway.recorder.buffer_depth", GetTotalBufferDepth);

        var connectionString = configuration.GetConnectionString("trading");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning(
                "No 'trading' connection string; the observation recorder is DISABLED. " +
                "Standing-subscription ticks will not be persisted.");
            _drainTasks = [];
            return;
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);

        var channelOptions = new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            // All ticks originate on the one EReader pump thread — never actually concurrent — but
            // this is not asserted anywhere else in the codebase, so SingleWriter is left false to
            // stay correct even if that assumption is ever revisited.
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        };

        _optionChannel = Channel.CreateBounded<OptionQuoteObservation>(channelOptions);
        _underlyingChannel = Channel.CreateBounded<UnderlyingTickObservation>(channelOptions);

        _drainTasks =
        [
            Task.Run(() => DrainLoopAsync(
                _optionChannel.Reader, WriteOptionBatchAsync, OptionWriteScope, _optionOverflow, _lifetime.Token)),
            Task.Run(() => DrainLoopAsync(
                _underlyingChannel.Reader, WriteUnderlyingBatchAsync, UnderlyingWriteScope, _underlyingOverflow, _lifetime.Token)),
        ];
    }

    /// <summary>
    /// How long <see cref="DisposeAsync"/> lets the drain loops flush before what is left is
    /// declared lost. Configurable only so a test can drive the expiry path in bounded time;
    /// the default is generous because the alternative to draining is unrecoverable data loss, and
    /// because nothing truncates it — this recorder is a DI singleton, so it is disposed when the
    /// service provider is, which is after the host's own shutdown timeout has already elapsed.
    /// </summary>
    private static TimeSpan ReadShutdownDrainTimeout(IConfiguration configuration)
    {
        var configured = configuration["Recorder:ShutdownDrainSeconds"];

        return double.TryParse(configured, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultShutdownDrainTimeout;
    }

    public bool Enabled => _dataSource is not null;

    private long GetTotalBufferDepth() =>
        (_optionChannel?.Reader.Count ?? 0) + (_underlyingChannel?.Reader.Count ?? 0);

    // ---- enqueue (called from the EReader pump thread — must never block) --------------------

    public void EnqueueOption(OptionQuoteObservation observation)
    {
        if (_optionChannel is not { } channel)
        {
            return;
        }

        NoteBufferDepth(channel.Reader.Count, _optionOverflow);
        channel.Writer.TryWrite(observation);
    }

    public void EnqueueUnderlying(UnderlyingTickObservation observation)
    {
        if (_underlyingChannel is not { } channel)
        {
            return;
        }

        NoteBufferDepth(channel.Reader.Count, _underlyingOverflow);
        channel.Writer.TryWrite(observation);
    }

    /// <summary>
    /// The pump thread's only contribution to buffer-overflow gap bookkeeping: raise the saturated
    /// flag. It deliberately never lowers it and never decides that a gap should close — see
    /// <see cref="OverflowGap"/> for why "the next enqueue looks and sees headroom" is not a sound
    /// close condition. Unlike the per-lease "disconnect"/"line_evicted" scopes, this one is
    /// process-wide and routinely recoverable, so it is reconciled continuously rather than closed
    /// by a tick resuming on a specific lease.
    /// </summary>
    private void NoteBufferDepth(int currentDepth, OverflowGap overflow)
    {
        if (currentDepth < ChannelCapacity)
        {
            return;
        }

        _bufferOverflows.Add(1);

        if (!overflow.MarkSaturated())
        {
            return; // already flagged; the reconciler owns it from here
        }

        // Task.Run rather than a bare fire-and-forget call: an async method invoked without await
        // still runs SYNCHRONOUSLY up to its first incomplete await, and the reconcile's first
        // awaits (an uncontended flag, then Npgsql command setup) can complete synchronously — so
        // "fire and forget" would have executed part of a Postgres round trip on the EReader pump
        // thread. Nothing may block that thread. This hop only happens on the saturation edge.
        _ = Task.Run(() => ReconcileOverflowGapAsync(overflow));
    }

    /// <summary>
    /// Converges this buffer's gap row onto its desired state. Both directions run here and nowhere
    /// else, which is what makes the open's INSERT window unobservable: <c>_openGapIdByScope</c> is
    /// only mutated for a buffer scope inside this critical section.
    /// </summary>
    private async Task ReconcileOverflowGapAsync(OverflowGap overflow)
    {
        if (_dataSource is null || !overflow.TryBeginReconcile())
        {
            return;
        }

        try
        {
            var saturated = overflow.Saturated;
            var open = _openGapIdByScope.ContainsKey(overflow.Scope);

            if (saturated && !open)
            {
                await OpenGapAsync(overflow.Scope, "buffer_overflow", CancellationToken.None);
            }
            else if (!saturated && open)
            {
                await CloseGapAsync(overflow.Scope);
            }
        }
        finally
        {
            overflow.EndReconcile();
        }
    }

    // ---- gap bookkeeping -----------------------------------------------------------------------

    /// <summary>
    /// Bounds every gap left open by a previous process, which by definition cannot still be
    /// ongoing: no gap this recorder holds open is touched, and every other open row belongs to a
    /// process that is gone. Returns false when the attempt failed and is worth retrying.
    /// </summary>
    /// <remarks>
    /// Only the recorder that opened a gap ever closes it, so a process that dies mid-gap — crash,
    /// OOM, redeploy, Ctrl-C — leaves <c>ended_at</c> NULL forever. CoverageMonitor counts a gap as
    /// overlapping a window when it has no end, so ONE ungraceful shutdown makes every future
    /// coverage report carry a permanent unexplained gap, and coverage is the gate that admits a
    /// recorded day into a study. Closing at startup keeps that gate meaningful.
    /// <para>
    /// The close is marked <c>inferred</c>, never <c>observed</c>: the interval really was
    /// unrecorded, but nobody watched recording resume, so <c>ended_at</c> is an upper bound on the
    /// outage rather than a measurement of it. Study-time filters need that distinction.
    /// </para>
    /// <para>
    /// Retryable, and retried by <c>SubscriptionManager.ExecuteAsync</c> until it succeeds. This is
    /// the crash-recovery path, and the crashes it recovers from (host OOM, a container stop, an
    /// Aspire cold start the gateway wins the race to) are exactly the ones that also leave Postgres
    /// down or still starting — a one-shot whose failure is merely logged therefore fails precisely
    /// in the scenario it exists for, and every orphaned row then stays open forever anyway.
    /// </para>
    /// <para>
    /// Because a retry can land long after this process has opened gaps of its own, the statement
    /// excludes the gap ids this recorder still holds. Earlier this was justified by "this process
    /// holds no subscriptions yet", which is only true of the very first attempt — the assumption
    /// that a retry would have quietly violated.
    /// </para>
    /// <para>
    /// It is NOT safe to run with another gateway live, and the scope shape does nothing to make it
    /// so — an earlier version of this remark claimed the opposite ("gap scope is per-LEASE and
    /// lease ids are fresh per process"), which is worth stating plainly because that kind of
    /// confident note is what stops the next reader looking. The statement below filters on
    /// <c>ended_at IS NULL</c> and on gap ids THIS process owns; it does not look at <c>scope</c> at
    /// all. So every open row belonging to any other live recorder gets bounded as <c>inferred</c>,
    /// whatever its scope: a concurrent process's ongoing lease gap, and equally its
    /// <c>recorder:write:option</c>, <c>recorder:write:underlying</c>, <c>recorder:option-buffer</c>
    /// or <c>recorder:underlying-buffer</c> gap — four scopes that are constant strings, identical in
    /// every process, and never were per-lease.
    /// </para>
    /// <para>
    /// The real precondition is one live recorder at a time, which the single-TWS-socket design
    /// already implies and which only a redeploy overlap (or a manual gateway run alongside Aspire)
    /// violates. In that window the misreport flatters: a genuinely ongoing outage is stamped as
    /// having ended at the other process's startup. It is worse than it first looks, because the
    /// bounded-away row is still in the older process's <c>_openGapIdByScope</c>, so its
    /// <see cref="OpenGapAsync"/> keeps short-circuiting and no replacement row is written for the
    /// remainder of the outage — every batch dropped after that instant shows up only in the
    /// <c>write_failures</c> counter. It self-heals when that process next closes the scope, which
    /// rewrites <c>ended_at</c>/<c>closed_by</c> to the observed values.
    /// </para>
    /// <para>
    /// Deliberately not fixed with an owner token. Doing it properly needs an owner column plus
    /// recorder liveness (a heartbeat), because "owned by someone else" is only actionable if you
    /// can tell whether that someone is still alive — a token alone would just stop the reconciliation
    /// bounding rows from processes that really are dead, which is the failure this method exists to
    /// prevent and the more damaging of the two. Encoding an owner in <c>scope</c> would not even
    /// help, since the statement does not filter on it. The buffer-overflow scopes are also a much
    /// smaller target than they were: they now converge within one flush interval rather than
    /// lingering until the next tick (see <see cref="OverflowGap"/>). The write-failure scopes stay
    /// exposed for as long as Postgres is down, and that residue is accepted, not overlooked.
    /// </para>
    /// </remarks>
    public async Task<bool> ReconcileOrphanedGapsAsync(CancellationToken cancellationToken)
    {
        if (_dataSource is null)
        {
            return true;
        }

        // Snapshot of what this process owns. A gap opened between this read and the UPDATE below
        // would be bounded as though it were an orphan — a window of one round trip, and one that
        // heals itself: the scope stays in _openGapIdByScope, so this recorder's own CloseGapAsync
        // still runs when recording resumes and rewrites ended_at/closed_by to the observed values.
        var owned = _openGapIdByScope.Values.Select(gap => gap.GapId).ToArray();

        try
        {
            await using var command = _dataSource.CreateCommand(
                "UPDATE gateway.recorder_gaps SET ended_at = now(), closed_by = 'inferred' " +
                "WHERE ended_at IS NULL AND NOT (gap_id = ANY($1)) RETURNING gap_id");
            // An empty array is the common (startup) case and behaves correctly: `x = ANY('{}')` is
            // false rather than NULL, so nothing is excluded. NOT IN would not have been safe here.
            command.Parameters.AddWithValue(owned);

            var reconciled = new List<long>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    reconciled.Add(reader.GetInt64(0));
                }
            }

            if (reconciled.Count > 0)
            {
                _logger.LogWarning(
                    "Bounded {Count} recording gap(s) left open by a previous process: {GapIds}. " +
                    "Their end times are inferred from this startup, not observed — the data really " +
                    "is missing, but the outage may have ended earlier than the row now says.",
                    reconciled.Count,
                    string.Join(", ", reconciled));
            }

            var failures = Volatile.Read(ref _orphanReconcileFailures);

            if (failures > 0)
            {
                _logger.LogInformation(
                    "Recording-gap reconciliation succeeded after {Failures} failed attempt(s).", failures);
            }

            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            // Not fatal: recording works without it. Coverage reporting is what degrades — until a
            // retry lands, which is why this reports failure instead of swallowing it.
            var failures = Interlocked.Increment(ref _orphanReconcileFailures);

            if (failures == 1)
            {
                _logger.LogError(ex, "Could not reconcile recording gaps left open by a previous process; retrying.");
            }
            else
            {
                _logger.LogDebug(
                    ex, "Recording-gap reconciliation still failing (attempt {Attempt}).", failures);
            }

            return false;
        }
    }

    /// <summary>
    /// Opens a gap for <paramref name="scope"/> if one is not already open. Safe to call
    /// repeatedly while a condition persists — only the first call in a run actually inserts a row.
    /// </summary>
    public async Task OpenGapAsync(string scope, string reason, CancellationToken cancellationToken)
    {
        if (_dataSource is null || _openGapIdByScope.ContainsKey(scope))
        {
            return;
        }

        try
        {
            await using var command = _dataSource.CreateCommand(
                "INSERT INTO gateway.recorder_gaps (scope, started_at, reason) VALUES ($1, now(), $2) RETURNING gap_id");
            // closed_by stays NULL while the gap is open; the schema CHECK ties the two together.
            command.Parameters.AddWithValue(scope);
            command.Parameters.AddWithValue(reason);

            var gapId = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

            if (!_openGapIdByScope.TryAdd(scope, new OpenGap(gapId, reason)))
            {
                // Another caller opened one concurrently; close the redundant row rather than leak it.
                await using var close = _dataSource.CreateCommand(
                    "UPDATE gateway.recorder_gaps SET ended_at = now(), closed_by = 'observed' WHERE gap_id = $1");
                close.Parameters.AddWithValue(gapId);
                await close.ExecuteNonQueryAsync(cancellationToken);
                return;
            }

            _logger.LogWarning("Recording gap opened: scope={Scope} reason={Reason} gapId={GapId}.", scope, reason, gapId);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            _logger.LogError(ex, "Could not record a gap for scope {Scope} ({Reason}).", scope, reason);
        }
    }

    /// <summary>Closes the open gap for <paramref name="scope"/>, if there is one.</summary>
    /// <param name="observed">
    /// True when recording was actually seen to resume (a tick arrived). False when the scope is
    /// merely known to be finished — an evicted lease, say — where the end is a bound rather than a
    /// measurement, and is recorded as <c>inferred</c>.
    /// </param>
    /// <remarks>
    /// Every gap MUST eventually be closed by someone. CoverageMonitor reads an unended gap as an
    /// outage that is still in progress, so a row left open on purpose does not read as "this
    /// subscription is over" — it reads as "coverage is broken, and will be for all future windows."
    /// </remarks>
    public Task CloseGapAsync(string scope, bool observed = true) =>
        CloseGapAsync(scope, observed, onlyIfReason: null);

    /// <param name="onlyIfReason">
    /// When non-null, the close applies ONLY if the scope's open row was opened for that reason,
    /// and does nothing otherwise. A gap scope holds one open row whatever opened it, so a caller
    /// that can only speak for its own condition — "TWS now reports live" says nothing about
    /// whether a disconnect has ended — must not be able to close somebody else's.
    /// </param>
    private async Task CloseGapAsync(string scope, bool observed, string? onlyIfReason)
    {
        if (_dataSource is null || !_openGapIdByScope.TryGetValue(scope, out var open))
        {
            return;
        }

        if (onlyIfReason is not null && !string.Equals(open.Reason, onlyIfReason, StringComparison.Ordinal))
        {
            return;
        }

        // Compare-and-remove, not a bare remove: between the read above and here another caller can
        // have closed this row and opened a different one for the same scope, and taking that one
        // would close a condition still in force.
        if (!_openGapIdByScope.TryRemove(new KeyValuePair<string, OpenGap>(scope, open)))
        {
            return;
        }

        try
        {
            await using var command = _dataSource.CreateCommand(
                "UPDATE gateway.recorder_gaps SET ended_at = now(), closed_by = $2 WHERE gap_id = $1");
            command.Parameters.AddWithValue(open.GapId);
            command.Parameters.AddWithValue(observed ? "observed" : "inferred");
            await command.ExecuteNonQueryAsync();

            _logger.LogInformation("Recording gap closed: scope={Scope} gapId={GapId}.", scope, open.GapId);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            _logger.LogError(ex, "Could not close the gap for scope {Scope} (gapId {GapId}).", scope, open.GapId);

            // Put it back so a later close attempt (or the next OpenGapAsync no-op check) can retry;
            // otherwise the in-memory guard forgets a gap the database still has open.
            _openGapIdByScope.TryAdd(scope, open);
        }
    }

    /// <summary>Convenience for <see cref="RecordingTickSink"/>: closes a lease's own gap scope.</summary>
    /// <param name="effectiveMarketDataType">
    /// What TWS has reported for the re-issued ticker so far, or null if it has not reported yet.
    /// </param>
    /// <remarks>
    /// The close and the non-live re-open are sequenced here, in one task, rather than being fired
    /// as two independent calls from the sink. They target the same single-row scope, so the order
    /// decides the outcome: closed-then-reopened leaves the alarm standing, reopened-then-closed
    /// silently retires it — and since TWS reports a ticker's type once per <c>reqMktData</c>,
    /// nothing would ever raise it again for the rest of that subscription's life.
    /// </remarks>
    public void NotifyGapClosed(Guid leaseId, short? effectiveMarketDataType) =>
        _ = ResumeRecordingAsync(leaseId, effectiveMarketDataType);

    private async Task ResumeRecordingAsync(Guid leaseId, short? effectiveMarketDataType)
    {
        await CloseGapAsync(LeaseScope(leaseId));

        if (effectiveMarketDataType is { } type && type != LiveMarketDataType)
        {
            NotifyNonLiveMarketData(leaseId, type);
        }
    }

    /// <summary>The one <c>marketDataType</c> value that is not an alarm.</summary>
    private const short LiveMarketDataType = 1;

    /// <summary>
    /// The <c>recorder_gaps.reason</c> written when TWS is serving a lease something other than
    /// live data. Public so a test — and a coverage query — names the same string this does.
    /// </summary>
    public const string NonLiveMarketDataReason = "non_live_market_data";

    /// <summary>
    /// Records that TWS is serving this lease a non-live regime. Loud, and NOT a refusal: the
    /// subscription keeps running and the ticks keep landing, because delayed data is the AppHost's
    /// documented first-run default (<c>IBKR__MarketDataType=3</c>) and a gateway that refused to
    /// boot on it would be unusable. The gap row IS the alarm.
    /// </summary>
    /// <remarks>
    /// Both halves are needed and neither substitutes for the other. The log line is what a person
    /// watching a session sees; the gap row is what a coverage query sees months later, when the
    /// only remaining question is whether a recorded surface can be trusted — and by then the log
    /// is gone. Per-lease scope, like every other subscription-level gap, so it is bounded by the
    /// same <c>TerminateAsync</c> that bounds the rest and cannot outlive its lease.
    /// </remarks>
    public void NotifyNonLiveMarketData(Guid leaseId, int marketDataType)
    {
        _logger.LogCritical(
            "TWS is serving lease {LeaseId} market data type {MarketDataType} ({Regime}), NOT live. " +
            "Recording continues and every tick captured under it is stamped with that type, but the " +
            "quotes are not live and a {Reason} gap is open for this lease until TWS reports live. " +
            "IBKR:MarketDataType is what was REQUESTED; this is what was SERVED, and TWS downgrades " +
            "silently when the entitlement is missing.",
            leaseId, marketDataType, DescribeMarketDataType(marketDataType), NonLiveMarketDataReason);

        _ = OpenGapAsync(LeaseScope(leaseId), NonLiveMarketDataReason, CancellationToken.None);
    }

    /// <summary>
    /// Retires the non-live alarm when TWS reports live for this lease's ticker — and only that
    /// alarm: see <see cref="CloseGapAsync(string, bool, string?)"/>'s <c>onlyIfReason</c>.
    /// </summary>
    public void NotifyLiveMarketData(Guid leaseId) =>
        _ = CloseGapAsync(LeaseScope(leaseId), observed: true, onlyIfReason: NonLiveMarketDataReason);

    private static string DescribeMarketDataType(int marketDataType) => marketDataType switch
    {
        1 => "live",
        2 => "frozen",
        3 => "delayed",
        4 => "delayed-frozen",
        _ => "unrecognised",
    };

    public static string LeaseScope(Guid leaseId) => $"lease:{leaseId:N}";

    // ---- draining --------------------------------------------------------------------------

    /// <summary>
    /// One channel's drain loop: fill a batch, write it, keep this channel's buffer-overflow gap
    /// reconciled, and exit when the channel is finished or shutdown has run out of patience.
    /// </summary>
    /// <remarks>
    /// Shared by both pipelines on purpose. They differ only in which COPY they run, and the parts
    /// that must NOT drift between them are exactly the ones here: when a batch is written, when it
    /// is abandoned, and who is accountable for observations that never reach Postgres.
    /// </remarks>
    private async Task DrainLoopAsync<T>(
        ChannelReader<T> reader,
        Func<List<T>, CancellationToken, Task> writeBatchAsync,
        string writeScope,
        OverflowGap overflow,
        CancellationToken cancellationToken)
    {
        var batch = new List<T>(BatchSize);

        while (true)
        {
            var completed = await FillBatchAsync(reader, batch, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                // Past the shutdown drain deadline. Writing with a cancelled token cannot succeed,
                // and writing with an uncancellable one is precisely what the deadline exists to
                // prevent, so everything still held here is genuinely lost — which means it gets
                // recorded, because unrecoverable data must never leave with a task instead of a
                // row. reader.Count is a snapshot, but by this point the writers are completed.
                await RecordUnflushedAsync(writeScope, batch.Count + reader.Count);
                return;
            }

            if (batch.Count > 0)
            {
                await writeBatchAsync(batch, cancellationToken);
                batch.Clear();
            }

            // Every iteration, including the idle ones: this periodic desired-vs-actual check — not
            // any single event — is what guarantees a buffer-overflow gap converges. When neither
            // saturation nor a gap is present it costs one dictionary lookup and no round trip.
            if (overflow.Saturated && reader.Count < OverflowClearedDepth)
            {
                overflow.ClearSaturated();
            }

            if (overflow.Saturated != _openGapIdByScope.ContainsKey(overflow.Scope))
            {
                await ReconcileOverflowGapAsync(overflow);
            }

            if (completed)
            {
                return; // writers completed and the channel is empty: everything buffered is durable
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="batch"/> up to <see cref="BatchSize"/>, until <see cref="FlushInterval"/>
    /// elapses, or until the channel is completed and drained. Returns true only in that last case.
    /// </summary>
    private static async Task<bool> FillBatchAsync<T>(ChannelReader<T> reader, List<T> batch, CancellationToken cancellationToken)
    {
        using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        flushCts.CancelAfter(FlushInterval);

        try
        {
            while (batch.Count < BatchSize)
            {
                if (!await reader.WaitToReadAsync(flushCts.Token))
                {
                    return true; // channel completed and empty
                }

                while (batch.Count < BatchSize && reader.TryRead(out var item))
                {
                    batch.Add(item);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Either the flush interval elapsed with a partial (possibly empty) batch — that is the
            // point — or shutdown blew its drain deadline. The caller keeps what was already
            // dequeued either way. This used to rethrow in the second case (the catch filter was
            // `when (!cancellationToken.IsCancellationRequested)`), faulting the drain task with up
            // to BatchSize observations still inside it, unwritten and unrecorded.
        }

        return false;
    }

    /// <summary>
    /// The record of observations that were dequeued or queued but never persisted. Increments
    /// <c>write_failures</c> FIRST and opens the gap second: the metric is the part that must
    /// survive a Postgres that is itself the reason the flush failed.
    /// </summary>
    private async Task RecordUnflushedAsync(string scope, int count)
    {
        if (count <= 0)
        {
            return;
        }

        _writeFailures.Add(count);
        _logger.LogError(
            "Recorder shutdown could not flush {Count} observation(s) for {Scope} within the drain " +
            "deadline; they are lost and the window is being recorded as a gap.",
            count,
            scope);

        await OpenGapAsync(scope, "shutdown_drain_timeout", CancellationToken.None);
    }

    private async Task WriteOptionBatchAsync(List<OptionQuoteObservation> batch, CancellationToken cancellationToken)
    {
        // A distinct scope per table: WriteOptionBatchAsync and WriteUnderlyingBatchAsync are two
        // independent, concurrently-running loops. A shared "recorder:write" scope meant one
        // pipeline's success (CloseGapAsync) could close a gap that should still reflect the OTHER
        // pipeline still failing.
        const string scope = OptionWriteScope;
        const string columns =
            "con_id, lease_id, observed_at, changed_fields, bid, ask, bid_size, ask_size, last, last_size, " +
            "volume, open_interest, greeks_variant, iv, delta, gamma, vega, theta, und_price, locked, crossed, " +
            "origin, normalization_version, market_data_type";

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
                await using var writer = await connection.BeginBinaryImportAsync(
                    $"COPY gateway.option_quote_events ({columns}) FROM STDIN (FORMAT BINARY)", cancellationToken);

                foreach (var observation in batch)
                {
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(observation.Envelope.ConId, NpgsqlDbType.Integer, cancellationToken);
                    await writer.WriteAsync(observation.Envelope.LeaseId, NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync(observation.Envelope.ObservedAt, NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync((int)observation.Changed, NpgsqlDbType.Integer, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.Bid, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.Ask, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.BidSize, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.AskSize, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.Last, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.LastSize, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.Volume, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.OpenInterest, cancellationToken);
                    await writer.WriteAsync((short)observation.GreeksVariant, NpgsqlDbType.Smallint, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.Iv, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.Delta, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.Gamma, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.Vega, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.Theta, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.UnderlyingPrice, cancellationToken);
                    await writer.WriteAsync(observation.Locked, NpgsqlDbType.Boolean, cancellationToken);
                    await writer.WriteAsync(observation.Crossed, NpgsqlDbType.Boolean, cancellationToken);
                    await writer.WriteAsync((short)observation.Envelope.Origin, NpgsqlDbType.Smallint, cancellationToken);
                    await writer.WriteAsync(observation.Envelope.NormalizationVersion, NpgsqlDbType.Smallint, cancellationToken);
                    await WriteNullableSmallintAsync(writer, observation.Envelope.MarketDataType, cancellationToken);
                }

                await writer.CompleteAsync(cancellationToken);
                _eventsPersisted.Add(batch.Count);
                await CloseGapAsync(scope);
                return;
            }
            catch (OperationCanceledException)
            {
                // Shutdown's drain deadline expired mid-COPY. Retrying is pointless (the token stays
                // cancelled) and this batch is already out of the channel, so it is accounted for
                // here or it disappears with the faulting task — which is exactly what used to
                // happen: OperationCanceledException is not in the filter below, so the batch was
                // dropped with no retry, no write_failures, and no gap.
                await RecordUnflushedAsync(scope, batch.Count);
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                // One bounded retry: this runs entirely on the background drain task, never the
                // pump thread, so the delay is safe. A single transient blip (one bad connection
                // attempt during a brief pool hiccup) recovers instead of permanently discarding up
                // to 5,000 already-dequeued, otherwise-unrecoverable observations.
                if (attempt == 0)
                {
                    _logger.LogWarning(
                        ex, "Transient failure persisting {Count} option observation(s); retrying once.", batch.Count);
                    await Task.Delay(TimeSpan.FromMilliseconds(RetryDelayMilliseconds), CancellationToken.None);
                    continue;
                }

                _writeFailures.Add(batch.Count);
                _logger.LogError(ex, "Failed to persist {Count} option observation(s) after retry; batch dropped.", batch.Count);
                await OpenGapAsync(scope, "write_failure", CancellationToken.None);
                return;
            }
        }
    }

    private async Task WriteUnderlyingBatchAsync(List<UnderlyingTickObservation> batch, CancellationToken cancellationToken)
    {
        const string scope = UnderlyingWriteScope;
        const string columns =
            "con_id, lease_id, observed_at, changed_fields, bid, ask, bid_size, ask_size, last, last_size, " +
            "volume, locked, crossed, origin, normalization_version, market_data_type";

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
                await using var writer = await connection.BeginBinaryImportAsync(
                    $"COPY gateway.underlying_tick_events ({columns}) FROM STDIN (FORMAT BINARY)", cancellationToken);

                foreach (var observation in batch)
                {
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(observation.Envelope.ConId, NpgsqlDbType.Integer, cancellationToken);
                    await writer.WriteAsync(observation.Envelope.LeaseId, NpgsqlDbType.Uuid, cancellationToken);
                    await writer.WriteAsync(observation.Envelope.ObservedAt, NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync((int)observation.Changed, NpgsqlDbType.Integer, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.Bid, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.Ask, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.BidSize, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.AskSize, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.Last, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.LastSize, cancellationToken);
                    await WriteNullableDecimalAsync(writer, observation.Volume, cancellationToken);
                    await writer.WriteAsync(observation.Locked, NpgsqlDbType.Boolean, cancellationToken);
                    await writer.WriteAsync(observation.Crossed, NpgsqlDbType.Boolean, cancellationToken);
                    await writer.WriteAsync((short)observation.Envelope.Origin, NpgsqlDbType.Smallint, cancellationToken);
                    await writer.WriteAsync(observation.Envelope.NormalizationVersion, NpgsqlDbType.Smallint, cancellationToken);
                    await WriteNullableSmallintAsync(writer, observation.Envelope.MarketDataType, cancellationToken);
                }

                await writer.CompleteAsync(cancellationToken);
                _eventsPersisted.Add(batch.Count);
                await CloseGapAsync(scope);
                return;
            }
            catch (OperationCanceledException)
            {
                // See WriteOptionBatchAsync: past the shutdown deadline this batch is lost, and
                // being lost is a thing that gets written down.
                await RecordUnflushedAsync(scope, batch.Count);
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                if (attempt == 0)
                {
                    _logger.LogWarning(
                        ex, "Transient failure persisting {Count} underlying tick observation(s); retrying once.", batch.Count);
                    await Task.Delay(TimeSpan.FromMilliseconds(RetryDelayMilliseconds), CancellationToken.None);
                    continue;
                }

                _writeFailures.Add(batch.Count);
                _logger.LogError(ex, "Failed to persist {Count} underlying tick observation(s) after retry; batch dropped.", batch.Count);
                await OpenGapAsync(scope, "write_failure", CancellationToken.None);
                return;
            }
        }
    }

    private static Task WriteNullableDecimalAsync(NpgsqlBinaryImporter writer, decimal? value, CancellationToken cancellationToken) =>
        value is { } present
            ? writer.WriteAsync(present, NpgsqlDbType.Numeric, cancellationToken)
            : writer.WriteNullAsync(cancellationToken);

    /// <summary>
    /// Writes a nullable smallint, with NULL meaning UNMEASURED rather than any particular value —
    /// which for <c>market_data_type</c> is the whole point (migration 016).
    /// </summary>
    private static Task WriteNullableSmallintAsync(NpgsqlBinaryImporter writer, short? value, CancellationToken cancellationToken) =>
        value is { } present
            ? writer.WriteAsync(present, NpgsqlDbType.Smallint, cancellationToken)
            : writer.WriteNullAsync(cancellationToken);

    /// <summary>
    /// Flushes what is buffered, then stops. A graceful shutdown must not be a data-loss event.
    /// </summary>
    /// <remarks>
    /// The order of the first three statements is the whole point, and it used to be the other way
    /// round: cancel, complete, await. Completing a channel means "let the reader drain to the end",
    /// which the drain loops cannot do on a token that is already cancelled — the fill threw, the
    /// loop faulted with up to <see cref="BatchSize"/> observations in hand, an in-flight COPY threw
    /// past its own catch filter, and up to <see cref="ChannelCapacity"/> more sat in the channel.
    /// Up to 55,000 unrecoverable observations discarded on EVERY clean restart, silently: no
    /// <c>write_failures</c>, no gap row, so the next startup's orphan reconciliation had nothing to
    /// bound and <c>CoverageMonitor</c> saw missing minutes with no gap explaining them — the
    /// unexplained-gap state Phase 1's "all gaps explained" acceptance criterion is written against,
    /// manufactured by shutting down normally.
    /// <para>
    /// The drain is bounded because the reason a drain does not progress is usually Postgres, and
    /// "flush everything" must not become "never exit". Past the deadline the loops abandon what is
    /// left and say so in <c>write_failures</c> and a gap row (<see cref="RecordUnflushedAsync"/>):
    /// data lost loudly is recoverable as knowledge; data lost silently is not.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        _optionChannel?.Writer.TryComplete();
        _underlyingChannel?.Writer.TryComplete();

        var drained = Task.WhenAll(_drainTasks);

        try
        {
            await drained.WaitAsync(_shutdownDrainTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogError(
                "Recorder drain did not finish within {Timeout}; cancelling. Whatever is still " +
                "buffered is about to be lost and will be recorded as such.",
                _shutdownDrainTimeout);

            await _lifetime.CancelAsync();

            try
            {
                await drained.WaitAsync(AbandonTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "Recorder drain tasks did not stop after cancellation; some unflushed observations may be unrecorded.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recorder drain task failed during shutdown.");
        }

        // Backstop only by this point: either the loops have returned, or they have been told to.
        await _lifetime.CancelAsync();
        _lifetime.Dispose();

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }
}
