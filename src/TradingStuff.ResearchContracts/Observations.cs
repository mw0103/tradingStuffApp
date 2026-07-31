namespace TradingStuff.ResearchContracts;

/// <summary>Which fields changed on this tick, relative to the sink's previous accumulated state.</summary>
[Flags]
public enum QuoteFieldChanges
{
    None = 0,
    Bid = 1,
    Ask = 2,
    BidSize = 4,
    AskSize = 8,
    Last = 16,
    LastSize = 32,
    Volume = 64,
    OpenInterest = 128,
    Greeks = 256,
    UnderlyingPrice = 512,
}

/// <summary>
/// Which option-computation tick variant a Greeks/IV reading came from. Only <see cref="Model"/> is
/// populated in Phase 1 — bid/ask/last computations are derived from one side of the book and
/// disagree with each other, the same reasoning <c>QuoteRequest</c> already applies to execution
/// quotes. The other variants are reserved for a later phase, not implemented here.
/// </summary>
public enum GreeksVariant
{
    None = 0,
    Bid = 1,
    Ask = 2,
    Last = 3,
    Model = 4,
}

/// <summary>Where an observation came from.</summary>
public enum ObservationOrigin
{
    /// <summary>A normal tick on a standing subscription.</summary>
    Stream = 1,

    /// <summary>A one-shot snapshot request (not used by the recorder in Phase 1; reserved).</summary>
    Snapshot = 2,

    /// <summary>The first tick after a subscription was re-issued following a reconnect.</summary>
    ReplayResubscribe = 3,
}

/// <summary>Fields every raw observation carries, regardless of instrument kind.</summary>
public sealed record ObservationEnvelope(
    int ConId,
    Guid LeaseId,
    DateTimeOffset ObservedAt,
    short NormalizationVersion,
    ObservationOrigin Origin);

/// <summary>
/// One full-state row for an option leg: every field the sink currently holds, not only what
/// changed this tick — <see cref="Changed"/> says what changed, but downstream readers should never
/// have to join across rows to know the state "as of" this observation.
/// </summary>
public sealed record OptionQuoteObservation(
    ObservationEnvelope Envelope,
    QuoteFieldChanges Changed,
    decimal? Bid,
    decimal? Ask,
    decimal? BidSize,
    decimal? AskSize,
    decimal? Last,
    decimal? LastSize,
    decimal? Volume,
    decimal? OpenInterest,
    GreeksVariant GreeksVariant,
    decimal? Iv,
    decimal? Delta,
    decimal? Gamma,
    decimal? Vega,
    decimal? Theta,
    decimal? UnderlyingPrice,
    bool Locked,
    bool Crossed);

/// <summary>The underlying-tick equivalent: same shape, no option Greeks/IV/OI.</summary>
public sealed record UnderlyingTickObservation(
    ObservationEnvelope Envelope,
    QuoteFieldChanges Changed,
    decimal? Bid,
    decimal? Ask,
    decimal? BidSize,
    decimal? AskSize,
    decimal? Last,
    decimal? LastSize,
    decimal? Volume,
    bool Locked,
    bool Crossed);
