using System.Collections.Concurrent;
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
/// A TWS stream that is opened once per connection and read many times.
/// </summary>
/// <remarks>
/// <para>
/// Account summary, positions, and P&amp;L are <em>subscriptions</em>, not queries: the initial
/// delivery is followed by pushed updates for as long as the request lives. The obvious shape —
/// subscribe, read, cancel, repeat — does not survive contact with TWS. Verified against TWS 223 on
/// 2026-07-31: the third consecutive portfolio read fails with
/// <c>error 322: Maximum number of account summary requests exceeded; desubscribe to previous
/// request first</c>, even though <c>cancelAccountSummary</c> is issued with the correct request id
/// after every read and neither throws nor reports an error. TWS keeps counting the subscription.
/// </para>
/// <para>
/// Opening each stream once and letting TWS push updates avoids the cap entirely, removes three
/// round trips from every order submission, and keeps the data fresher than polling would — TWS
/// pushes account summary changes on its own schedule and position changes as they happen.
/// </para>
/// <para>
/// The trade-off is lifetime: a subscription belongs to the connection that opened it. When the
/// socket drops, <see cref="IbkrRequestRegistry.FailAll"/> faults every one of these, and the reader
/// must rebuild rather than serve values frozen at the moment of disconnect.
/// </para>
/// </remarks>
internal abstract class AccountSubscription : IPendingRequest
{
    private readonly TaskCompletionSource _initialDelivery =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the first full delivery has arrived.</summary>
    public Task InitialDelivery => _initialDelivery.Task;

    /// <summary>Set once the stream is dead; a faulted subscription must be rebuilt, not read.</summary>
    public Exception? Failure { get; private set; }

    protected void CompleteInitialDelivery() => _initialDelivery.TrySetResult();

    public void Fail(Exception error)
    {
        Failure = error;
        _initialDelivery.TrySetException(error);
    }
}

/// <summary>Live account summary values, keyed so a later push replaces the value it supersedes.</summary>
internal sealed class AccountSummarySubscription : AccountSubscription
{
    private readonly ConcurrentDictionary<(string Account, string Tag, string Currency), AccountSummaryValue> _values =
        new();

    public void Apply(AccountSummaryValue value) =>
        _values[(value.Account, value.Tag, value.Currency)] = value;

    /// <summary>Marks the end of the initial delivery. Updates keep arriving afterwards.</summary>
    public void CompleteSnapshot() => CompleteInitialDelivery();

    public IReadOnlyList<AccountSummaryValue> Values => [.. _values.Values];
}

/// <summary>
/// Live positions, keyed on account + contract.
/// </summary>
/// <remarks>
/// TWS reports a closed position as a row with a quantity of zero rather than withdrawing it, so
/// overwriting by contract is what keeps the set correct as positions change.
/// </remarks>
internal sealed class PositionsSubscription : AccountSubscription
{
    private readonly ConcurrentDictionary<(string Account, int ConId), AccountPositionRow> _rows = new();

    public void Apply(AccountPositionRow row) => _rows[(row.Account, row.Contract.ConId)] = row;

    public void CompleteSnapshot() => CompleteInitialDelivery();

    public IReadOnlyList<AccountPositionRow> Rows => [.. _rows.Values];
}

/// <summary>
/// Live account P&amp;L.
/// </summary>
/// <remarks>
/// Unlike the other two this has no <c>...End</c> callback — the first push carrying a real daily
/// P&amp;L is the initial delivery.
/// </remarks>
internal sealed class PnLSubscription : AccountSubscription
{
    private AccountPnL? _latest;

    public AccountPnL? Latest => Volatile.Read(ref _latest);

    public void Apply(double dailyPnL, double unrealizedPnL, double realizedPnL)
    {
        // TWS sends double.MaxValue for anything it has not computed yet. Settling on a sentinel
        // would report a fabricated flat day and silently disable the MAX_DAILY_LOSS risk check, so
        // wait for a real value and let the caller's timeout decide when to give up.
        if (!QuoteRequest.TryConvertSigned(dailyPnL, out var daily))
        {
            return;
        }

        QuoteRequest.TryConvertSigned(unrealizedPnL, out var unrealized);
        QuoteRequest.TryConvertSigned(realizedPnL, out var realized);

        Volatile.Write(ref _latest, new AccountPnL(daily, unrealized, realized));
        CompleteInitialDelivery();
    }
}
