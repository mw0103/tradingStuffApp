using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Timing;

/// <summary>
/// One EDGAR acceptance time, resolved. <see cref="InstantUtc"/> is null exactly when the wall
/// clock EDGAR stamped does not name a real instant — see <see cref="EdgarAcceptance"/>.
/// </summary>
/// <param name="DateEt">The Eastern calendar date of the acceptance. Taken from the wall clock itself, never from the UTC instant.</param>
/// <param name="TimeEt">The Eastern wall-clock time of day of the acceptance.</param>
/// <param name="InstantUtc">The UTC instant, or null when the wall time does not exist in America/New_York.</param>
/// <param name="Anomaly">Null on the ordinary path; otherwise what was wrong and what was done about it.</param>
public sealed record EdgarAcceptanceInstant(
    DateOnly DateEt,
    TimeOnly TimeEt,
    DateTimeOffset? InstantUtc,
    string? Anomaly);

/// <summary>
/// <b>THE EDGAR BOUNDARY.</b> The single place in this study where a wall-clock time becomes an
/// instant, and the only permitted use of <see cref="TimeZoneInfo"/> anywhere in it.
/// </summary>
/// <remarks>
/// <para>
/// This is the study's analogue of the <c>IBApi</c> <c>double</c> boundary in the platform proper:
/// a vendor hands us a value in its own representation, it is converted once at the edge, and
/// everything inside works in the canonical one. EDGAR's <c>acceptanceDateTime</c> is a UTC instant
/// (see <c>EdgarParsing.ParseAcceptanceEt</c>); <see cref="FromUtcInstant"/> turns it into the
/// America/New_York wall clock that <see cref="EventRow.AcceptanceEt"/> carries as a
/// <see cref="DateTimeKind.Unspecified"/> <see cref="DateTime"/>, and <see cref="Resolve"/> turns
/// that wall clock back into an instant. Downstream of this class every timing decision is a
/// comparison of UTC instants against
/// <see cref="TradingStuff.ResearchContracts.ISessionClock"/> session boundaries, which are
/// themselves UTC.
/// </para>
/// <para>
/// This does not contend with the platform doctrine that <c>SessionClock</c> is the only type
/// permitted to convert a timezone (docs/DECISIONS.md, entry 5). That doctrine is about the
/// <i>calendar</i>: what session an instant falls in, and what trading date it belongs to. Neither
/// question is asked here. This class answers only "what instant did EDGAR mean", and it asks the
/// clock nothing. A second conversion anywhere in the study — a hand-rolled "subtract five hours", a
/// <c>DateTime.SpecifyKind(..., Utc)</c> on a value that is not already a UTC instant, a second
/// <see cref="TimeZoneInfo"/> lookup — is the defect this class exists to make unnecessary. The one
/// <c>SpecifyKind(..., Utc)</c> in the study is in <c>EdgarParsing.ParseAcceptanceEt</c>, on digits
/// the stamp's "Z" has already declared UTC, feeding <see cref="FromUtcInstant"/> directly: that is
/// labelling, not converting.
/// </para>
/// <para>
/// <b>DST.</b> Neither DST case can arise from real EDGAR data: US transitions happen at 02:00 local
/// on a Sunday, and EDGAR accepts filings on business days between 06:00 and 22:00 ET. Nor can
/// <see cref="FromUtcInstant"/> manufacture an invalid one. Neither fact makes the two branches
/// below dead, because a wall clock reaches <see cref="Resolve"/> from <c>events.csv</c> as readily
/// as from the parser, and by then the offset is gone. Both are therefore decided here rather than
/// left to the framework's defaults, because those defaults
/// are wrong in opposite directions — <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/>
/// throws on an invalid time (a throw that would kill a whole run over one row) and silently assumes
/// standard time on an ambiguous one.
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Ambiguous</b> (the hour repeated at the autumn fall-back) resolves to the FIRST occurrence —
/// the larger, still-DST offset, hence the earlier UTC instant. Earlier is the leak-safe direction:
/// entry is the last close at or before the print, so believing the print EARLIER than it was can
/// only select an entry close that is also strictly before the true print, whereas believing it
/// later could select a close the real print had already moved. Note this is a different rule from
/// <c>SessionGenerator</c>'s, which widens a session in both directions; a session boundary is a
/// containment predicate where wider is safe, an acceptance instant is a leakage boundary where
/// earlier is safe.
/// </item>
/// <item>
/// <b>Invalid</b> (the hour skipped at the spring-forward) resolves to NO instant, and the event is
/// quarantined UNRESOLVED by <see cref="TimingResolver"/>. A wall time that never occurred is not a
/// timing case, it is a corrupt input — mis-parsed, or stamped in some other zone — and inventing an
/// instant for it would convert a data defect into a confident measurement date. An honest gap in
/// the record beats a plausible fabrication (docs/LESSONS.md, 8). This costs nothing real: the
/// skipped hour is 02:00-03:00 ET on a March Sunday.
/// </item>
/// </list>
/// </remarks>
public static class EdgarAcceptance
{
    /// <summary>EDGAR's <c>acceptanceDateTime</c> is stamped in this zone. The SEC publishes no other.</summary>
    public const string EasternTimeZoneId = "America/New_York";

    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById(EasternTimeZoneId);

    /// <summary>Noon Eastern: the cut used to label an acceptance on a day the exchange never opened. See <see cref="TimingResolver"/>.</summary>
    public static readonly TimeOnly NoonEt = new(12, 0);

    /// <summary>
    /// The inbound half of the boundary: EDGAR's UTC <c>acceptanceDateTime</c> instant to the
    /// America/New_York wall clock every row downstream carries. Lives here, beside the single
    /// <see cref="TimeZoneInfo"/> instance in the study, so the "one conversion site" claim above
    /// stays true — <c>EdgarParsing.ParseAcceptanceEt</c> calls this instead of resolving the zone a
    /// second time.
    /// </summary>
    /// <param name="utcInstant">
    /// The instant EDGAR stamped. Must carry <see cref="DateTimeKind.Utc"/>, the mirror of
    /// <see cref="Resolve"/>'s Unspecified contract: a value of some other kind has already been
    /// interpreted by somebody else, and converting it again here would shift it silently.
    /// </param>
    /// <remarks>
    /// Unlike <see cref="Resolve"/>, this direction decides nothing: every UTC instant names exactly
    /// one Eastern wall clock. It cannot return a time inside the spring-forward gap, and where it
    /// lands in the fall-back hour the offset that told the two occurrences apart is gone from the
    /// returned value by construction. That is why <see cref="Resolve"/> still has to rule on both
    /// cases — the wall clocks reaching it come from <c>events.csv</c> as often as from this method,
    /// and the CSV carries no offset.
    /// </remarks>
    public static DateTime FromUtcInstant(DateTime utcInstant)
    {
        if (utcInstant.Kind is not DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "the EDGAR acceptance instant must carry DateTimeKind.Utc before it is converted to " +
                $"an Eastern wall clock; got {utcInstant.Kind} for {utcInstant:O}.",
                nameof(utcInstant));
        }

        return DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(utcInstant, Eastern), DateTimeKind.Unspecified);
    }

    /// <summary>Resolves an EDGAR acceptance wall clock to its UTC instant.</summary>
    /// <param name="acceptanceEt">
    /// The acceptance time as an America/New_York wall clock. Must be
    /// <see cref="DateTimeKind.Unspecified"/>: a value already carrying <see cref="DateTimeKind.Utc"/>
    /// or <see cref="DateTimeKind.Local"/> has been converted by somebody else, and reinterpreting it
    /// as Eastern here would shift every date it produces by hours without a trace. That is a caller
    /// contract, not row data, so it refuses loudly rather than quarantining.
    /// </param>
    public static EdgarAcceptanceInstant Resolve(DateTime acceptanceEt)
    {
        if (acceptanceEt.Kind is not DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "EDGAR acceptance times are America/New_York wall clocks and must carry " +
                $"DateTimeKind.Unspecified; got {acceptanceEt.Kind} for {acceptanceEt:O}.",
                nameof(acceptanceEt));
        }

        var dateEt = DateOnly.FromDateTime(acceptanceEt);
        var timeEt = TimeOnly.FromDateTime(acceptanceEt);

        if (Eastern.IsInvalidTime(acceptanceEt))
        {
            return new EdgarAcceptanceInstant(
                dateEt,
                timeEt,
                null,
                $"acceptance wall clock {acceptanceEt:yyyy-MM-dd HH:mm:ss} does not exist in " +
                $"{EasternTimeZoneId} (spring-forward gap)");
        }

        if (Eastern.IsAmbiguousTime(acceptanceEt))
        {
            // UTC = local - offset, so the LARGER offset (-04:00 EDT) is the earlier instant, which
            // is the first of the two occurrences and the leak-safe one.
            var offsets = Eastern.GetAmbiguousTimeOffsets(acceptanceEt);

            return new EdgarAcceptanceInstant(
                dateEt,
                timeEt,
                new DateTimeOffset(acceptanceEt, offsets.Max()).ToUniversalTime(),
                $"acceptance wall clock {acceptanceEt:yyyy-MM-dd HH:mm:ss} is ambiguous in " +
                $"{EasternTimeZoneId} (fall-back hour); resolved to the first occurrence");
        }

        return new EdgarAcceptanceInstant(
            dateEt,
            timeEt,
            new DateTimeOffset(acceptanceEt, Eastern.GetUtcOffset(acceptanceEt)).ToUniversalTime(),
            null);
    }
}
