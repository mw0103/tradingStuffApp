using Microsoft.Extensions.Options;
using Npgsql;

namespace TradingStuff.IbkrGateway.Persistence;

/// <summary>Outcome of recording an internal-order → broker-order mapping before transmission.</summary>
public abstract record OrderMappingResult
{
    /// <summary>The mapping is new; the order may be transmitted.</summary>
    public sealed record Recorded : OrderMappingResult;

    /// <summary>
    /// This internal order was already transmitted — possibly by a previous gateway process — as
    /// the given broker order. Placing again would put a second live order on the book.
    /// </summary>
    public sealed record AlreadyMapped(int ExistingIbkrOrderId, string LastStatus) : OrderMappingResult;

    /// <summary>The store could not be reached; persistence provides no protection right now.</summary>
    public sealed record Unavailable(string Reason) : OrderMappingResult;

    /// <summary>
    /// The broker order id is already mapped to a DIFFERENT internal order. An integrity violation,
    /// not an availability problem — placement must refuse unconditionally.
    /// </summary>
    public sealed record IntegrityViolation(string Reason) : OrderMappingResult;
}

/// <summary>
/// Durable internal-order → IBKR-order mapping (<c>gateway.ibkr_order_map</c>).
/// </summary>
/// <remarks>
/// The in-memory claim in <see cref="IbkrOrderTracker"/> dies with the process, so before this
/// store existed a gateway restart orphaned every live broker order: a caller retry after the
/// restart would transmit a second order for the same internal id. The mapping is written BEFORE
/// <c>placeOrder</c> and consulted on every placement, making the database the cross-process
/// authority. The schema is owned by ResearchService migrations; until they have run, writes fail
/// and are reported through <see cref="OrderMappingResult.Unavailable"/>.
/// </remarks>
public sealed class OrderIdStore : IDisposable
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly ILogger<OrderIdStore> _logger;

    public OrderIdStore(IConfiguration configuration, ILogger<OrderIdStore> logger)
    {
        _logger = logger;

        var connectionString = configuration.GetConnectionString("trading");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning(
                "No 'trading' connection string is configured; broker order-id persistence is OFF. " +
                "A gateway restart will forget which internal orders already reached the broker.");
            return;
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public bool Enabled => _dataSource is not null;

    /// <summary>Records the mapping, refusing a duplicate for an already-transmitted internal order.</summary>
    public async Task<OrderMappingResult> TryRecordPlacementAsync(
        Guid internalOrderId,
        int ibkrOrderId,
        string? account,
        string status,
        CancellationToken cancellationToken)
    {
        if (_dataSource is null)
        {
            return new OrderMappingResult.Unavailable("No 'trading' connection string is configured.");
        }

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

            // Conflict target is the internal id specifically: a clash on UNIQUE(ibkr_order_id)
            // must THROW (unique violation, handled below) rather than be silently swallowed —
            // a reused broker order id is an integrity violation, not a duplicate retry.
            await using (var insert = new NpgsqlCommand(
                "INSERT INTO gateway.ibkr_order_map (internal_order_id, ibkr_order_id, account, last_status) " +
                "VALUES ($1, $2, $3, $4) ON CONFLICT (internal_order_id) DO NOTHING",
                connection))
            {
                insert.Parameters.AddWithValue(internalOrderId);
                insert.Parameters.AddWithValue(ibkrOrderId);
                insert.Parameters.AddWithValue((object?)account ?? DBNull.Value);
                insert.Parameters.AddWithValue(status);

                if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
                {
                    return new OrderMappingResult.Recorded();
                }
            }

            await using (var existing = new NpgsqlCommand(
                "SELECT ibkr_order_id, last_status FROM gateway.ibkr_order_map WHERE internal_order_id = $1",
                connection))
            {
                existing.Parameters.AddWithValue(internalOrderId);

                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);

                if (await reader.ReadAsync(cancellationToken))
                {
                    return new OrderMappingResult.AlreadyMapped(reader.GetInt32(0), reader.GetString(1));
                }
            }

            return new OrderMappingResult.Unavailable(
                "The mapping insert conflicted but no row exists for the internal order.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            var diagnosis = await DescribeBrokerIdCollisionAsync(ibkrOrderId, cancellationToken);

            _logger.LogCritical(ex, "Refusing to place an order: {Diagnosis}", diagnosis);

            return new OrderMappingResult.IntegrityViolation(diagnosis);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            _logger.LogCritical(
                ex,
                "Could not persist the order mapping {InternalOrderId} -> {IbkrOrderId}. " +
                "If the gateway restarts, this order cannot be recognised as already transmitted.",
                internalOrderId,
                ibkrOrderId);

            return new OrderMappingResult.Unavailable(ex.Message);
        }
    }

    /// <summary>
    /// Explains a broker-order-id collision in terms an operator can act on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not "impossible". TWS's <b>Global Configuration → API → Settings → Reset API order ID
    /// sequence</b> sends the client a low <c>nextValidId</c>, and while
    /// <see cref="IbkrRequestRegistry.SeedFrom"/> only ever raises the in-process sequence, a gateway
    /// restart after such a reset starts from that low id — straight back into the range the
    /// <c>ibkr_order_map</c> rows of earlier sessions already occupy. Every placement then fails
    /// here, with no self-healing path, until the shared request/order sequence has climbed past the
    /// highest id on record.
    /// </para>
    /// <para>
    /// The remedy is deliberately NOT to delete the colliding rows. They are the audit trail that
    /// links an internal order to a real broker order, and silently removing one to unblock a
    /// placement would destroy exactly the record that exists to prove what was transmitted. The
    /// honest output is a message naming the cause and the number the sequence has to clear.
    /// </para>
    /// </remarks>
    private async Task<string> DescribeBrokerIdCollisionAsync(int ibkrOrderId, CancellationToken cancellationToken)
    {
        var baseline =
            $"Broker order id {ibkrOrderId} is already mapped to a different internal order in " +
            "gateway.ibkr_order_map.";

        try
        {
            await using var command = _dataSource!.CreateCommand(
                "SELECT m.internal_order_id, m.last_status, m.placed_at, " +
                "       (SELECT max(ibkr_order_id) FROM gateway.ibkr_order_map) " +
                "FROM gateway.ibkr_order_map m WHERE m.ibkr_order_id = $1");
            command.Parameters.AddWithValue(ibkrOrderId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                return baseline;
            }

            var highest = reader.IsDBNull(3) ? ibkrOrderId : reader.GetInt32(3);

            return baseline +
                   $" It belongs to internal order {reader.GetGuid(0)} ({reader.GetString(1)}), recorded " +
                   $"{reader.GetFieldValue<DateTimeOffset>(2):u}. The usual cause is TWS's " +
                   "'Reset API order ID sequence' followed by a gateway restart, which reseeds this " +
                   $"process below ids already used. Placement stays blocked until the id sequence is " +
                   $"above {highest}: restart the gateway with TWS's order id sequence advanced past " +
                   "that value (Global Configuration -> API -> Settings), or reconcile and retire the " +
                   "old mappings deliberately. Do NOT delete order-map rows to clear this — they are " +
                   "the audit trail.";
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not read the colliding order-map row for broker order {IbkrOrderId}.", ibkrOrderId);
            return baseline;
        }
    }

    /// <summary>
    /// Removes a mapping this call recorded when the transmission provably never reached the wire,
    /// so a later retry of the same internal order may legitimately place. Guarded by both ids and
    /// the untouched status, it can never delete a row belonging to a transmitted order.
    /// </summary>
    public async Task<bool> TryDeleteNeverTransmittedAsync(
        Guid internalOrderId,
        int ibkrOrderId,
        string recordedStatus,
        CancellationToken cancellationToken)
    {
        if (_dataSource is null)
        {
            return false;
        }

        try
        {
            await using var command = _dataSource.CreateCommand(
                "DELETE FROM gateway.ibkr_order_map " +
                "WHERE internal_order_id = $1 AND ibkr_order_id = $2 AND last_status = $3");
            command.Parameters.AddWithValue(internalOrderId);
            command.Parameters.AddWithValue(ibkrOrderId);
            command.Parameters.AddWithValue(recordedStatus);

            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            _logger.LogWarning(
                ex,
                "Could not remove the never-transmitted mapping {InternalOrderId} -> {IbkrOrderId}; " +
                "a retry of this internal order will be refused until the row is removed.",
                internalOrderId,
                ibkrOrderId);

            return false;
        }
    }

    /// <summary>Best-effort status refresh; failures are logged, never thrown.</summary>
    public async Task TryUpdateStatusAsync(int ibkrOrderId, string status, long permId, CancellationToken cancellationToken)
    {
        if (_dataSource is null)
        {
            return;
        }

        try
        {
            await using var command = _dataSource.CreateCommand(
                "UPDATE gateway.ibkr_order_map " +
                "SET last_status = $2, perm_id = NULLIF($3, 0::bigint), updated_at = now() " +
                "WHERE ibkr_order_id = $1");
            command.Parameters.AddWithValue(ibkrOrderId);
            command.Parameters.AddWithValue(status);
            command.Parameters.AddWithValue(permId);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not update the persisted status of order {IbkrOrderId}.", ibkrOrderId);
        }
    }

    public void Dispose() => _dataSource?.Dispose();
}
