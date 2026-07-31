using IBApi;
using TradingStuff.Contracts;

namespace TradingStuff.IbkrGateway;

/// <summary>A pending request fed by market-data tick callbacks.</summary>
internal interface ITickSink : IPendingRequest
{
    void ApplyPrice(int field, double price);

    /// <summary>
    /// The full <c>tickOptionComputation</c> payload. <paramref name="impliedVolatility"/> and
    /// <paramref name="undPrice"/> exist for sinks that record them (the recorder does; the
    /// one-shot execution-quote path does not and ignores both).
    /// </summary>
    void ApplyOptionComputation(
        int field, double impliedVolatility, double delta, double gamma, double vega, double theta, double undPrice);

    /// <summary>
    /// Bid/ask/last size, and — for a leased option subscription with generic ticks 100/101 — this
    /// contract's own volume (field 29 or 30) and open interest (field 27 or 28). Default no-op:
    /// only the recorder's standing-subscription sinks care about sizes; the one-shot
    /// <see cref="QuoteRequest"/>/<see cref="SpotPriceRequest"/> paths do not carry size fields at
    /// all and would otherwise need an empty override each.
    /// </summary>
    void ApplySize(int field, decimal size)
    {
    }

    /// <summary>Settle with whatever has arrived so far.</summary>
    void CompletePartial();
}

/// <summary>
/// Accumulates the several tick callbacks that together make up one option quote, and completes once
/// bid, ask, and model Greeks have all arrived (or the caller's timeout expires).
/// </summary>
/// <remarks>
/// Two things make this fiddlier than it looks:
/// <list type="bullet">
/// <item>A quote is assembled from many callbacks arriving at different times, not one message.</item>
/// <item>Under delayed market data (<c>reqMarketDataType(3)</c>) TWS sends entirely different tick
/// fields — 66/67/68 instead of 1/2/4, and 83 instead of 13. Handling only the live fields yields a
/// subscription that looks connected and never produces a quote.</item>
/// </list>
/// </remarks>
internal sealed class QuoteRequest : ITickSink
{
    private readonly TaskCompletionSource<QuoteSnapshot> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly OptionContract _contract;
    private readonly string _source;
    private readonly bool _requireGreeks;
    private readonly Lock _gate = new();

    private decimal? _bid;
    private decimal? _ask;
    private decimal? _last;
    private OptionGreeks? _greeks;

    /// <param name="requireGreeks">
    /// False when quoting a non-option (the underlying, for centring a chain window), where no
    /// option computation tick will ever arrive and waiting for one would always time out.
    /// </param>
    public QuoteRequest(OptionContract contract, string source, bool requireGreeks = true)
    {
        _contract = contract;
        _source = source;
        _requireGreeks = requireGreeks;
    }

    /// <summary>Best available price so far — used for spot lookups.</summary>
    public decimal? BestPrice
    {
        get
        {
            lock (_gate)
            {
                if (_last is > 0m)
                {
                    return _last;
                }

                return _bid is > 0m && _ask is > 0m ? decimal.Round((_bid.Value + _ask.Value) / 2m, 4) : null;
            }
        }
    }

    public Task<QuoteSnapshot> Task => _completion.Task;

    public void ApplyPrice(int field, double price)
    {
        if (!TryConvertPrice(price, out var value))
        {
            return;
        }

        lock (_gate)
        {
            switch (field)
            {
                case TickType.BID or TickType.DELAYED_BID:
                    _bid = value;
                    break;
                case TickType.ASK or TickType.DELAYED_ASK:
                    _ask = value;
                    break;
                case TickType.LAST or TickType.DELAYED_LAST:
                    _last = value;
                    break;
                default:
                    return;
            }
        }

        TryCompleteIfReady();
    }

    public void ApplyOptionComputation(
        int field, double impliedVolatility, double delta, double gamma, double vega, double theta, double undPrice)
    {
        // impliedVolatility/undPrice are not part of QuoteSnapshot; this path ignores both.
        // Only the model computation is a usable Greeks set; the bid/ask/last computations are
        // derived from one side of the book and disagree with each other.
        if (field is not (TickType.MODEL_OPTION or TickType.DELAYED_MODEL_OPTION))
        {
            return;
        }

        // TWS marks an entirely uncomputed option calculation with -2 in delta.
        if (delta is -2d)
        {
            return;
        }

        if (!TryConvertGreek(delta, out var d) || !TryConvertGreek(gamma, out var g) ||
            !TryConvertGreek(vega, out var v) || !TryConvertGreek(theta, out var t))
        {
            return;
        }

        lock (_gate)
        {
            _greeks = new OptionGreeks(d, g, t, v);
        }

        TryCompleteIfReady();
    }

    /// <summary>
    /// Completes with whatever has arrived so far. Illiquid series can legitimately never publish a
    /// full set, so a partial snapshot beats hanging — the missing fields surface as zeroes and the
    /// caller can see them on the snapshot.
    /// </summary>
    public void CompletePartial() => Complete();

    public void Fail(Exception error) => _completion.TrySetException(error);

    private void TryCompleteIfReady()
    {
        lock (_gate)
        {
            if (_bid is null || _ask is null)
            {
                return;
            }

            if (_requireGreeks && _greeks is null)
            {
                return;
            }
        }

        Complete();
    }

    private void Complete()
    {
        lock (_gate)
        {
            var bid = _bid ?? 0m;
            var ask = _ask ?? 0m;

            _completion.TrySetResult(new QuoteSnapshot(
                Guid.NewGuid(),
                _contract,
                bid,
                ask,
                _last ?? (bid > 0m && ask > 0m ? decimal.Round((bid + ask) / 2m, 4) : 0m),
                _greeks ?? new OptionGreeks(0m, 0m, 0m, 0m),
                DateTimeOffset.UtcNow,
                _source));
        }
    }

    /// <summary>
    /// Converts a TWS price tick, rejecting its "unavailable" markers. Unset numeric fields arrive as
    /// <see cref="double.MaxValue"/> and a missing quote arrives as -1; casting either straight to
    /// <see cref="decimal"/> overflows or poisons the snapshot.
    /// </summary>
    internal static bool TryConvertPrice(double value, out decimal result)
    {
        result = 0m;

        // An option never trades at a negative price, so any negative is TWS's "no quote" marker.
        if (!IsUsable(value) || value < 0d)
        {
            return false;
        }

        result = (decimal)value;
        return true;
    }

    /// <summary>
    /// Converts a Greek from an option computation tick. Unlike prices, Greeks are legitimately
    /// negative — a deep in-the-money put has delta -1 and theta is normally negative — so only the
    /// numeric sentinels are rejected here, never the sign.
    /// </summary>
    internal static bool TryConvertGreek(double value, out decimal result) =>
        TryConvertSigned(value, out result);

    /// <summary>
    /// Converts a value whose sign is meaningful, rejecting only TWS's numeric sentinels.
    /// </summary>
    /// <remarks>
    /// Use for anything that can legitimately be negative: Greeks, and a combo's net price or
    /// average fill price, where negative means a net credit. Running those through
    /// <see cref="TryConvertPrice"/> silently discards every credit as if it were a missing quote.
    /// </remarks>
    internal static bool TryConvertSigned(double value, out decimal result)
    {
        result = 0m;

        if (!IsUsable(value))
        {
            return false;
        }

        result = (decimal)value;
        return true;
    }

    private static bool IsUsable(double value) =>
        !double.IsNaN(value)
        && !double.IsInfinity(value)
        && value < (double)decimal.MaxValue
        && value > (double)decimal.MinValue;
}
