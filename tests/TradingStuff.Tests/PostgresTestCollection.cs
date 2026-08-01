namespace TradingStuff.Tests;

/// <summary>
/// Serialises every database-backed suite, and bounds what each one may hold open.
/// </summary>
/// <remarks>
/// <para>
/// Without this the suites pass individually and fail together. xunit runs test classes in
/// parallel up to the core count — 32 on the machine this was diagnosed on — and each
/// database-backed test creates its own database and therefore its own Npgsql pool. Against a
/// stock server that is 32 classes competing for <c>max_connections = 100</c>, and every failure
/// reads <c>"sorry, too many clients already"</c> rather than anything about the code under test.
/// </para>
/// <para>
/// Two separate causes, so two separate fixes, and neither is sufficient alone.
/// </para>
/// <para>
/// <b>Peak concurrency.</b> A shared collection makes xunit run these classes one at a time. Only
/// the database suites are serialised; the ~1300 tests that never open a connection still run
/// across every core, so the default run stays as fast as it was.
/// </para>
/// <para>
/// <b>Cumulative idle connections.</b> Serialising alone would still fail. Npgsql keys a pool per
/// connection string and keeps it for the process lifetime, and a full run creates roughly a
/// hundred distinct databases — so a hundred pools, each holding its idle connections for the
/// default five minutes, long after the test that made them finished.
/// <see cref="ConnectionString"/> caps each pool and prunes it in seconds, which is what stops
/// the total climbing across a run rather than within one test.
/// </para>
/// <para>
/// The alternative — raising <c>max_connections</c> — was rejected: it is a property of whoever's
/// server the suite happens to meet, and Aspire's own <c>AddContainer("postgres", "17")</c> hands
/// out the stock 100. A test suite that only passes against a tuned server is a test suite that
/// does not pass.
/// </para>
/// </remarks>
[CollectionDefinition(PostgresCollection.Name, DisableParallelization = true)]
public sealed class PostgresCollection
{
    public const string Name = "postgres";

    /// <summary>
    /// A connection string for a per-test database, with pooling bounded so a run's worth of
    /// them cannot exhaust the server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Maximum Pool Size</c> caps what any one test can hold. Eight is comfortably above what
    /// the busiest suite opens at once — the concurrent-claimer tests run several coordinators —
    /// and far below the point where one test could starve the next.
    /// </para>
    /// <para>
    /// <c>Connection Idle Lifetime</c> and <c>Connection Pruning Interval</c> are the ones that
    /// actually matter here. They are seconds rather than the default five minutes, so a pool
    /// belonging to a finished test releases its connections while the run is still going instead
    /// of holding them until the process exits.
    /// </para>
    /// </remarks>
    public static string ConnectionString(string server, string database) =>
        $"{server.TrimEnd(';')};Database={database};" +
        "Maximum Pool Size=8;Connection Idle Lifetime=2;Connection Pruning Interval=1";

    /// <summary>A connection string for a fresh, uniquely named database.</summary>
    public static string FreshDatabase(string server) =>
        ConnectionString(server, $"trading_test_{Guid.NewGuid():N}");
}
