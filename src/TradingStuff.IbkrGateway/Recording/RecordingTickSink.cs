using IBApi;
using TradingStuff.ResearchContracts;

namespace TradingStuff.IbkrGateway.Recording;

/// <summary>
/// Accumulates the tick callbacks for one standing subscription into full-state observations and
/// forwards each changed tick to the <see cref="ObservationRecorder"/>.
/// </summary>
/// <remarks>
/// Unlike <c>QuoteRequest</c> (which completes once and stops), this sink lives for the whole
/// lease and never settles — every tick that changes accumulated state produces one row. Every
/// method here runs on the EReader pump thread via <c>IbkrClientWrapper</c>; nothing may block.
/// </remarks>
internal sealed class RecordingTickSink : ITickSink
{
    private const short NormalizationVersion = 1;

    private readonly int _conId;
    private readonly Guid _leaseId;
    private readonly bool _isOption;
    private readonly IObservationSink _recorder;
    private readonly Action<Exception> _onFailed;
    private readonly Lock _gate = new();

    private decimal? _bid;
    private decimal? _ask;
    private decimal? _bidSize;
    private decimal? _askSize;
    private decimal? _last;
    private decimal? _lastSize;
    private decimal? _volume;
    private decimal? _openInterest;
    private GreeksVariant _greeksVariant = GreeksVariant.None;
    private decimal? _iv;
    private decimal? _delta;
    private decimal? _gamma;
    private decimal? _vega;
    private decimal? _theta;
    private decimal? _undPrice;

    /// <summary>
    /// True until the next tick emits — that tick is tagged <see cref="ObservationOrigin.ReplayResubscribe"/>
    /// so a gap can be closed on it, then this flips permanently to <see cref="ObservationOrigin.Stream"/>.
    /// </summary>
    private bool _pendingReplayTag;

    public RecordingTickSink(
        int conId,
        Guid leaseId,
        bool isOption,
        bool markFirstTickAsReplay,
        IObservationSink recorder,
        Action<Exception> onFailed)
    {
        _conId = conId;
        _leaseId = leaseId;
        _isOption = isOption;
        _pendingReplayTag = markFirstTickAsReplay;
        _recorder = recorder;
        _onFailed = onFailed;
    }

    public void ApplyPrice(int field, double price)
    {
        if (!QuoteRequest.TryConvertPrice(price, out var value))
        {
            return;
        }

        QuoteFieldChanges changed;

        lock (_gate)
        {
            switch (field)
            {
                case TickType.BID or TickType.DELAYED_BID:
                    if (_bid == value)
                    {
                        return;
                    }

                    _bid = value;
                    changed = QuoteFieldChanges.Bid;
                    break;

                case TickType.ASK or TickType.DELAYED_ASK:
                    if (_ask == value)
                    {
                        return;
                    }

                    _ask = value;
                    changed = QuoteFieldChanges.Ask;
                    break;

                case TickType.LAST or TickType.DELAYED_LAST:
                    if (_last == value)
                    {
                        return;
                    }

                    _last = value;
                    changed = QuoteFieldChanges.Last;
                    break;

                default:
                    return;
            }
        }

        Emit(changed);
    }

    public void ApplySize(int field, decimal size)
    {
        if (!TryConvertSize(size, out var value))
        {
            return;
        }

        QuoteFieldChanges changed;

        lock (_gate)
        {
            switch (field)
            {
                case TickType.BID_SIZE or TickType.DELAYED_BID_SIZE:
                    if (_bidSize == value)
                    {
                        return;
                    }

                    _bidSize = value;
                    changed = QuoteFieldChanges.BidSize;
                    break;

                case TickType.ASK_SIZE or TickType.DELAYED_ASK_SIZE:
                    if (_askSize == value)
                    {
                        return;
                    }

                    _askSize = value;
                    changed = QuoteFieldChanges.AskSize;
                    break;

                case TickType.LAST_SIZE or TickType.DELAYED_LAST_SIZE:
                    if (_lastSize == value)
                    {
                        return;
                    }

                    _lastSize = value;
                    changed = QuoteFieldChanges.LastSize;
                    break;

                case TickType.VOLUME or TickType.DELAYED_VOLUME:
                    if (_volume == value)
                    {
                        return;
                    }

                    _volume = value;
                    changed = QuoteFieldChanges.Volume;
                    break;

                // Per-contract open interest arrives as call-OI (27) or put-OI (28) depending on
                // this contract's own right — only one of the two ever fires for a given
                // subscription, so both are treated as "the" OI reading rather than requiring the
                // sink to already know the contract's right.
                case TickType.OPTION_CALL_OPEN_INTEREST or TickType.OPTION_PUT_OPEN_INTEREST:
                    if (!_isOption || _openInterest == value)
                    {
                        return;
                    }

                    _openInterest = value;
                    changed = QuoteFieldChanges.OpenInterest;
                    break;

                default:
                    return;
            }
        }

        Emit(changed);
    }

    public void ApplyOptionComputation(
        int field, double impliedVolatility, double delta, double gamma, double vega, double theta, double undPrice)
    {
        // Only the model computation is recorded — see GreeksVariant's remarks.
        if (field is not (TickType.MODEL_OPTION or TickType.DELAYED_MODEL_OPTION))
        {
            return;
        }

        // TWS marks an entirely uncomputed option calculation with -2 in delta; when that happens
        // IV/undPrice on the same callback are not trustworthy either.
        if (delta is -2d)
        {
            return;
        }

        if (!QuoteRequest.TryConvertGreek(delta, out var d) ||
            !QuoteRequest.TryConvertGreek(gamma, out var g) ||
            !QuoteRequest.TryConvertGreek(vega, out var v) ||
            !QuoteRequest.TryConvertGreek(theta, out var t))
        {
            return;
        }

        // IV and underlying price use the price converter (never negative), and are optional: a
        // computation can be usable for Greeks while temporarily missing one of these two.
        var haveIv = QuoteRequest.TryConvertPrice(impliedVolatility, out var iv) && iv > 0m;
        var haveUnd = QuoteRequest.TryConvertPrice(undPrice, out var und) && und > 0m;

        QuoteFieldChanges changed;

        lock (_gate)
        {
            var greeksChanged = _greeksVariant != GreeksVariant.Model ||
                                 _delta != d || _gamma != g || _vega != v || _theta != t ||
                                 (haveIv && _iv != iv);
            var undChanged = haveUnd && _undPrice != und;

            if (!greeksChanged && !undChanged)
            {
                return;
            }

            _greeksVariant = GreeksVariant.Model;
            _delta = d;
            _gamma = g;
            _vega = v;
            _theta = t;

            if (haveIv)
            {
                _iv = iv;
            }

            if (haveUnd)
            {
                _undPrice = und;
            }

            changed = QuoteFieldChanges.None;

            if (greeksChanged)
            {
                changed |= QuoteFieldChanges.Greeks;
            }

            if (undChanged)
            {
                changed |= QuoteFieldChanges.UnderlyingPrice;
            }
        }

        Emit(changed);
    }

    public void CompletePartial()
    {
        // No concept of "complete" for a standing subscription — it lives for the lease's duration.
    }

    public void Fail(Exception error) => _onFailed(error);

    private void Emit(QuoteFieldChanges changed)
    {
        var observedAt = DateTimeOffset.UtcNow;
        bool locked;
        bool crossed;
        ObservationOrigin origin;

        lock (_gate)
        {
            locked = _bid.HasValue && _ask.HasValue && _bid == _ask;
            crossed = _bid.HasValue && _ask.HasValue && _bid > _ask;
            origin = _pendingReplayTag ? ObservationOrigin.ReplayResubscribe : ObservationOrigin.Stream;
            _pendingReplayTag = false;

            var envelope = new ObservationEnvelope(_conId, _leaseId, observedAt, NormalizationVersion, origin);

            if (_isOption)
            {
                _recorder.EnqueueOption(new OptionQuoteObservation(
                    envelope, changed, _bid, _ask, _bidSize, _askSize, _last, _lastSize, _volume, _openInterest,
                    _greeksVariant, _iv, _delta, _gamma, _vega, _theta, _undPrice, locked, crossed));
            }
            else
            {
                _recorder.EnqueueUnderlying(new UnderlyingTickObservation(
                    envelope, changed, _bid, _ask, _bidSize, _askSize, _last, _lastSize, _volume, locked, crossed));
            }
        }

        if (origin == ObservationOrigin.ReplayResubscribe)
        {
            _recorder.NotifyGapClosed(_leaseId);
        }
    }

    private static bool TryConvertSize(decimal value, out decimal result)
    {
        result = 0m;

        if (value == decimal.MaxValue || value < 0m)
        {
            return false;
        }

        result = value;
        return true;
    }
}
