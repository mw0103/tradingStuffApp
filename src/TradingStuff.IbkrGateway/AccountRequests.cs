using IbContract = IBApi.Contract;

namespace TradingStuff.IbkrGateway;

/// <summary>One tag/value row from <c>reqAccountSummary</c>.</summary>
/// <remarks>
/// TWS reports the summary as a stream of loose key/value strings rather than a typed record, one
/// callback per tag, so values arrive unparsed and tagged with the currency they are denominated in.
/// </remarks>
internal sealed record AccountSummaryValue(string Account, string Tag, string Value, string Currency);

/// <summary>One position row from <c>reqPositionsMulti</c>.</summary>
internal sealed record AccountPositionRow(string Account, IbContract Contract, decimal Position, double AverageCost);

/// <summary>Account-level profit and loss for the current session.</summary>
public sealed record AccountPnL(decimal DailyPnL, decimal UnrealizedPnL, decimal RealizedPnL);

/// <summary>
/// A <c>reqPnL</c> subscription read once.
/// </summary>
/// <remarks>
/// Unlike contract details or chains, P&amp;L has no <c>...End</c> callback — it is a subscription
/// that streams updates until cancelled. The first usable callback is the snapshot, so this settles
/// on it and the caller cancels immediately afterwards.
/// </remarks>
internal sealed class PnLRequest : IPendingRequest
{
    private readonly TaskCompletionSource<AccountPnL> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<AccountPnL> Task => _completion.Task;

    public void Apply(double dailyPnL, double unrealizedPnL, double realizedPnL)
    {
        // Daily P&L is the only field that matters to the risk engine, and TWS sends
        // double.MaxValue for anything it has not computed yet. Settling on a sentinel would report
        // a fabricated flat day, which is exactly what this stage exists to stop — so wait for a
        // real value and let the caller's timeout decide when to give up.
        if (!QuoteRequest.TryConvertSigned(dailyPnL, out var daily))
        {
            return;
        }

        QuoteRequest.TryConvertSigned(unrealizedPnL, out var unrealized);
        QuoteRequest.TryConvertSigned(realizedPnL, out var realized);

        _completion.TrySetResult(new AccountPnL(daily, unrealized, realized));
    }

    public void Fail(Exception error) => _completion.TrySetException(error);
}
