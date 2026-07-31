using IBApi;

namespace TradingStuff.IbkrGateway;

/// <summary>
/// A price-only market data request for a non-option contract — the underlying, used to centre an
/// option chain's strike window on the money.
/// </summary>
/// <remarks>
/// Separate from <see cref="QuoteRequest"/> because a stock never produces an option computation
/// tick, so anything waiting on Greeks here would always time out. Completes on the first usable
/// last/close price, or on a mid once both sides of the book have arrived.
/// </remarks>
internal sealed class SpotPriceRequest : ITickSink
{
    private readonly TaskCompletionSource<decimal> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Lock _gate = new();

    private decimal? _bid;
    private decimal? _ask;
    private decimal? _last;

    public Task<decimal> Task => _completion.Task;

    public void ApplyPrice(int field, double price)
    {
        if (!QuoteRequest.TryConvertPrice(price, out var value) || value <= 0m)
        {
            return;
        }

        lock (_gate)
        {
            switch (field)
            {
                case TickType.LAST or TickType.DELAYED_LAST or TickType.CLOSE or TickType.DELAYED_CLOSE:
                    _last = value;
                    break;
                case TickType.BID or TickType.DELAYED_BID:
                    _bid = value;
                    break;
                case TickType.ASK or TickType.DELAYED_ASK:
                    _ask = value;
                    break;
                default:
                    return;
            }
        }

        TryComplete();
    }

    /// <summary>Not applicable to a stock; option computation ticks never arrive for one.</summary>
    public void ApplyOptionComputation(int field, double delta, double gamma, double vega, double theta)
    {
    }

    public void CompletePartial()
    {
        if (Resolve() is { } price)
        {
            _completion.TrySetResult(price);
            return;
        }

        _completion.TrySetException(
            new IbkrRequestException(0, "No usable price arrived for the underlying."));
    }

    public void Fail(Exception error) => _completion.TrySetException(error);

    private void TryComplete()
    {
        if (Resolve() is { } price)
        {
            _completion.TrySetResult(price);
        }
    }

    private decimal? Resolve()
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
