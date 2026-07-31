namespace TradingStuff.ResearchService.Gateway;

/// <summary>
/// An IBKR contract descriptor for historical requests, matched by property name against the
/// gateway's own <c>HistoricalContractSpec</c>.
/// </summary>
/// <remarks>
/// Restated here rather than shared through a project reference for the same reason every other DTO
/// in this file is: ResearchService and IbkrGateway are separate processes and the gateway is the
/// sole TWS socket owner, so the contract between them is the HTTP body, not a CLR type.
/// <para>
/// <see cref="IncludeExpired"/> is carried even though this package never sets it: the ES
/// per-expired-contract walk (package 2e) requests history for contracts that have already expired,
/// and a spec that could not express that would force a second, parallel client.
/// </para>
/// </remarks>
public sealed record HistoricalContractSpecDto(
    string Symbol,
    string SecType,
    string Exchange = "SMART",
    string Currency = "USD",
    string? LastTradeDateOrContractMonth = null,
    decimal? Strike = null,
    string? Right = null,
    string? Multiplier = null,
    string? TradingClass = null,
    string? PrimaryExchange = null,
    int? ConId = null,
    bool IncludeExpired = false);

/// <summary>Request body for the gateway's <c>POST /ibkr/history/bars</c>.</summary>
public sealed record HistoricalBarsRequestDto(
    HistoricalContractSpecDto Contract,
    DateTimeOffset? EndDateTime,
    string Duration,
    string BarSize,
    string WhatToShow,
    bool UseRth = true);

/// <summary>
/// One historical bar. Exactly one of <see cref="Timestamp"/> (intraday) or
/// <see cref="TradingDate"/> (daily and coarser) is populated — TWS returns a bare date for daily
/// bars even under <c>formatDate=2</c>, and reading one as the other silently misplaces the bar.
/// </summary>
public sealed record HistoricalBarDto(
    DateTimeOffset? Timestamp,
    DateOnly? TradingDate,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    int Count,
    decimal Wap);

/// <summary>Response body for the gateway's <c>POST /ibkr/history/bars</c>.</summary>
public sealed record HistoricalBarsResponseDto(IReadOnlyList<HistoricalBarDto> Bars, bool HasData);

/// <summary>Response body for the gateway's <c>POST /ibkr/history/head-timestamp</c>.</summary>
public sealed record HeadTimestampResponseDto(DateTimeOffset HeadTimestamp);

/// <summary>
/// How a gateway historical call ended, in the terms the coordinator's state machine acts on.
/// </summary>
/// <remarks>
/// The mapping matters more than the names. The gateway answers a confirmed-empty slice with
/// <b>200 OK and <c>HasData: false</c></b>, not an error, so "no data here" and "the request failed"
/// are different HTTP outcomes and must stay different states — an empty slice is retired, a failed
/// one is retried. Pacing rejection arrives as <b>429 with <c>Retry-After</c></b> and is neither: the
/// slice never reached TWS, so it goes back on the queue without consuming a retry.
/// </remarks>
public enum GatewayOutcome
{
    /// <summary>Bars returned.</summary>
    Ok,

    /// <summary>TWS confirmed this slice has no data (error 162, surfaced as <c>HasData: false</c>).</summary>
    Empty,

    /// <summary>The pacing governor refused the request. Back off by <c>Retry-After</c> and re-issue the same slice.</summary>
    Paced,

    /// <summary>Worth retrying: a transient gateway/TWS failure, or a timeout.</summary>
    Transient,

    /// <summary>Retrying this exact request cannot help (bad contract, rejected parameters, CONTFUT error 10339).</summary>
    Permanent,

    /// <summary>The gateway is not connected to TWS. Like <see cref="Paced"/>, the slice never left the building.</summary>
    NotConnected,
}

/// <summary>The outcome of one <c>POST /ibkr/history/bars</c>, classified for the coordinator.</summary>
public sealed record HistoricalBarsResult(
    GatewayOutcome Outcome,
    IReadOnlyList<HistoricalBarDto> Bars,
    TimeSpan? RetryAfter,
    int? IbkrErrorCode,
    string? Detail);

/// <summary>The outcome of one <c>POST /ibkr/history/head-timestamp</c>, classified for the coordinator.</summary>
public sealed record HeadTimestampResult(
    GatewayOutcome Outcome,
    DateTimeOffset? HeadTimestampUtc,
    TimeSpan? RetryAfter,
    int? IbkrErrorCode,
    string? Detail);
