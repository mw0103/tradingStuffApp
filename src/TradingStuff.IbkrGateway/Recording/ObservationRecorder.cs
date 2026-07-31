using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
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

    void NotifyGapClosed(Guid leaseId);
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
    private const int BatchSize = 5_000;
    private const int RetryDelayMilliseconds = 250;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

    private readonly NpgsqlDataSource? _dataSource;
    private readonly ILogger<ObservationRecorder> _logger;
    private readonly Channel<OptionQuoteObservation>? _optionChannel;
    private readonly Channel<UnderlyingTickObservation>? _underlyingChannel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task[] _drainTasks;

    // Process-local guard against opening a duplicate gap row for a scope that already has one
    // open. Gap rows themselves are the durable record; this dictionary only avoids spamming the
    // table while a condition (e.g. sustained buffer saturation) persists across many ticks.
    private readonly ConcurrentDictionary<string, long> _openGapIdByScope = new();

    private readonly Counter<long> _eventsPersisted;
    private readonly Counter<long> _writeFailures;
    private readonly Counter<long> _bufferOverflows;

    // Only for logging: the orphan reconciliation is retried until it succeeds, and one error per
    // retry every sweep interval would bury the rest of the log.
    private int _orphanReconcileFailures;

    public ObservationRecorder(IConfiguration configuration, IMeterFactory meterFactory, ILogger<ObservationRecorder> logger)
    {
        _logger = logger;

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
            Task.Run(() => DrainOptionLoopAsync(_lifetime.Token)),
            Task.Run(() => DrainUnderlyingLoopAsync(_lifetime.Token)),
        ];
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

        CheckOverflow(channel.Reader.Count, "recorder:option-buffer");
        channel.Writer.TryWrite(observation);
    }

    public void EnqueueUnderlying(UnderlyingTickObservation observation)
    {
        if (_underlyingChannel is not { } channel)
        {
            return;
        }

        CheckOverflow(channel.Reader.Count, "recorder:underlying-buffer");
        channel.Writer.TryWrite(observation);
    }

    /// <summary>
    /// Opens (or, once the backlog has drained, closes) the buffer-overflow gap for one channel.
    /// Unlike the per-lease "disconnect"/"line_evicted" scopes, this one is process-wide and
    /// routinely recoverable, so it is the only gap scope closed from the enqueue path itself
    /// rather than from a tick resuming on a specific lease.
    /// </summary>
    private void CheckOverflow(int currentDepth, string scope)
    {
        if (currentDepth < ChannelCapacity)
        {
            if (_openGapIdByScope.ContainsKey(scope))
            {
                _ = CloseGapAsync(scope);
            }

            return;
        }

        _bufferOverflows.Add(1);

        // Fire-and-forget: this is called from the pump thread and must not await. Errors are
        // logged inside OpenGapAsync itself.
        _ = OpenGapAsync(scope, "buffer_overflow", CancellationToken.None);
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
    /// Safe to run with other gateways live only because gap scope is per-LEASE and lease ids are
    /// fresh per process: no row this reconciliation can see belongs to a subscription anyone still
    /// holds. If gap scope ever becomes per-conId, this becomes wrong and needs an owner column.
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
        var owned = _openGapIdByScope.Values.ToArray();

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

            if (!_openGapIdByScope.TryAdd(scope, gapId))
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
    public async Task CloseGapAsync(string scope, bool observed = true)
    {
        if (_dataSource is null || !_openGapIdByScope.TryRemove(scope, out var gapId))
        {
            return;
        }

        try
        {
            await using var command = _dataSource.CreateCommand(
                "UPDATE gateway.recorder_gaps SET ended_at = now(), closed_by = $2 WHERE gap_id = $1");
            command.Parameters.AddWithValue(gapId);
            command.Parameters.AddWithValue(observed ? "observed" : "inferred");
            await command.ExecuteNonQueryAsync();

            _logger.LogInformation("Recording gap closed: scope={Scope} gapId={GapId}.", scope, gapId);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            _logger.LogError(ex, "Could not close the gap for scope {Scope} (gapId {GapId}).", scope, gapId);

            // Put it back so a later close attempt (or the next OpenGapAsync no-op check) can retry;
            // otherwise the in-memory guard forgets a gap the database still has open.
            _openGapIdByScope.TryAdd(scope, gapId);
        }
    }

    /// <summary>Convenience for <see cref="RecordingTickSink"/>: closes a lease's own gap scope.</summary>
    public void NotifyGapClosed(Guid leaseId) => _ = CloseGapAsync(LeaseScope(leaseId));

    public static string LeaseScope(Guid leaseId) => $"lease:{leaseId:N}";

    // ---- draining --------------------------------------------------------------------------

    private async Task DrainOptionLoopAsync(CancellationToken cancellationToken)
    {
        var batch = new List<OptionQuoteObservation>(BatchSize);

        while (!cancellationToken.IsCancellationRequested)
        {
            await FillBatchAsync(_optionChannel!.Reader, batch, cancellationToken);

            if (batch.Count == 0)
            {
                continue;
            }

            await WriteOptionBatchAsync(batch, cancellationToken);
            batch.Clear();
        }
    }

    private async Task DrainUnderlyingLoopAsync(CancellationToken cancellationToken)
    {
        var batch = new List<UnderlyingTickObservation>(BatchSize);

        while (!cancellationToken.IsCancellationRequested)
        {
            await FillBatchAsync(_underlyingChannel!.Reader, batch, cancellationToken);

            if (batch.Count == 0)
            {
                continue;
            }

            await WriteUnderlyingBatchAsync(batch, cancellationToken);
            batch.Clear();
        }
    }

    /// <summary>Fills <paramref name="batch"/> up to <see cref="BatchSize"/> or until <see cref="FlushInterval"/> elapses.</summary>
    private static async Task FillBatchAsync<T>(ChannelReader<T> reader, List<T> batch, CancellationToken cancellationToken)
    {
        using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        flushCts.CancelAfter(FlushInterval);

        try
        {
            while (batch.Count < BatchSize)
            {
                if (!await reader.WaitToReadAsync(flushCts.Token))
                {
                    return; // channel completed
                }

                while (batch.Count < BatchSize && reader.TryRead(out var item))
                {
                    batch.Add(item);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Flush interval elapsed with a partial (possibly empty) batch — that is the point.
        }
    }

    private async Task WriteOptionBatchAsync(List<OptionQuoteObservation> batch, CancellationToken cancellationToken)
    {
        // A distinct scope per table: WriteOptionBatchAsync and WriteUnderlyingBatchAsync are two
        // independent, concurrently-running loops. A shared "recorder:write" scope meant one
        // pipeline's success (CloseGapAsync) could close a gap that should still reflect the OTHER
        // pipeline still failing.
        const string scope = "recorder:write:option";
        const string columns =
            "con_id, lease_id, observed_at, changed_fields, bid, ask, bid_size, ask_size, last, last_size, " +
            "volume, open_interest, greeks_variant, iv, delta, gamma, vega, theta, und_price, locked, crossed, " +
            "origin, normalization_version";

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
                }

                await writer.CompleteAsync(cancellationToken);
                _eventsPersisted.Add(batch.Count);
                await CloseGapAsync(scope);
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
        const string scope = "recorder:write:underlying";
        const string columns =
            "con_id, lease_id, observed_at, changed_fields, bid, ask, bid_size, ask_size, last, last_size, " +
            "volume, locked, crossed, origin, normalization_version";

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
                }

                await writer.CompleteAsync(cancellationToken);
                _eventsPersisted.Add(batch.Count);
                await CloseGapAsync(scope);
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

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _optionChannel?.Writer.TryComplete();
        _underlyingChannel?.Writer.TryComplete();

        try
        {
            await Task.WhenAll(_drainTasks);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }
}
