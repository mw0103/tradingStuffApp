using System.Data;
using Npgsql;
using TradingStuff.ResearchContracts;

namespace TradingStuff.ResearchService.Trials;

/// <summary>
/// Raised when the protocol refuses a registration. Distinct from a database failure: nothing
/// about retrying will help, and the caller must not proceed with the run.
/// </summary>
public sealed class TrialProtocolException(string message) : Exception(message);

/// <summary>
/// Reads and appends the pre-registered trial registry.
/// </summary>
/// <remarks>
/// <para>
/// Every write is a plain INSERT. There is no update path and no delete path — not because none
/// was needed yet, but because their absence is the guarantee. The tables carry triggers that
/// reject both, so a future caller reaching for one gets an error naming the reason rather than a
/// silently amended history.
/// </para>
/// <para>
/// <see cref="RegisterAsync"/> runs serializable. The ordinal it assigns is the study's variant
/// count plus one, and two concurrent registrations reading the same count would otherwise both
/// claim it — the unique constraint would catch the collision, but the loser would have to retry
/// with a count it had already misread. Serializable makes the read and the write one decision.
/// This is a handful of rows per study, so the isolation costs nothing that matters.
/// </para>
/// </remarks>
public sealed class TrialRegistry(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Registers a variant and returns it with its assigned ordinal.
    /// </summary>
    /// <exception cref="TrialProtocolException">
    /// The study has exhausted its variant cap. The registration is explicit that this is a
    /// negative result rather than a prompt to raise the cap, so it is refused here rather than
    /// warned about.
    /// </exception>
    public async Task<RegisteredTrial> RegisterAsync(TrialDeclaration declaration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (string.IsNullOrWhiteSpace(declaration.Study))
            throw new ArgumentException("A trial must name the study it runs under.", nameof(declaration));
        if (string.IsNullOrWhiteSpace(declaration.GitSha))
            throw new ArgumentException(
                "A trial must record the commit it runs from, or its configuration describes a run " +
                "that cannot be reproduced.", nameof(declaration));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var registered = await CountAsync(connection, transaction, declaration.Study, cancellationToken);

        if (!TrialProtocol.CanRegisterAnother(registered))
        {
            throw new TrialProtocolException(
                $"Study '{declaration.Study}' has registered its full cap of {TrialProtocol.VariantCap} " +
                "variants. The pre-registration treats an exhausted cap as a negative result; " +
                "continuing requires data accumulated after re-registration, not another variant.");
        }

        const string insert = """
            INSERT INTO research.registered_trials
                (study, variant_ordinal, feature_set_hash, model_family, hyperparameters,
                 fold_config, seed, git_sha, rationale)
            VALUES ($1, $2, $3, $4, $5::jsonb, $6::jsonb, $7, $8, $9)
            RETURNING trial_id, registered_at
            """;

        await using var command = new NpgsqlCommand(insert, connection, transaction);
        command.Parameters.AddWithValue(declaration.Study);
        command.Parameters.AddWithValue(registered + 1);
        command.Parameters.AddWithValue(declaration.FeatureSetHash);
        command.Parameters.AddWithValue(declaration.ModelFamily);
        command.Parameters.AddWithValue(declaration.Hyperparameters);
        command.Parameters.AddWithValue(declaration.FoldConfig);
        command.Parameters.AddWithValue(declaration.Seed);
        command.Parameters.AddWithValue(declaration.GitSha);
        command.Parameters.AddWithValue(declaration.Rationale);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        var trialId = reader.GetInt64(0);
        var registeredAt = reader.GetFieldValue<DateTimeOffset>(1);

        await reader.CloseAsync();
        await transaction.CommitAsync(cancellationToken);

        return new RegisteredTrial(
            trialId, declaration.Study, registered + 1, registeredAt,
            declaration.FeatureSetHash, declaration.ModelFamily, declaration.Hyperparameters,
            declaration.FoldConfig, declaration.Seed, declaration.GitSha, declaration.Rationale);
    }

    /// <summary>Variants registered for a study, whether or not they produced an outcome.</summary>
    /// <remarks>
    /// Declarations, not results. A variant that was declared and abandoned still consumed a look
    /// at the data, which is what the multiple-comparison correction exists to price.
    /// </remarks>
    public async Task<int> CountAsync(string study, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await CountAsync(connection, null, study, cancellationToken);
    }

    private static async Task<int> CountAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string study, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM research.registered_trials WHERE study = $1", connection, transaction);
        command.Parameters.AddWithValue(study);

        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Every variant registered for a study, in the order they were registered.</summary>
    public async Task<IReadOnlyList<RegisteredTrial>> ListAsync(string study, CancellationToken cancellationToken)
    {
        const string select = """
            SELECT trial_id, study, variant_ordinal, registered_at, feature_set_hash, model_family,
                   hyperparameters::text, fold_config::text, seed, git_sha, rationale
            FROM research.registered_trials
            WHERE study = $1
            ORDER BY variant_ordinal
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(select, connection);
        command.Parameters.AddWithValue(study);

        var trials = new List<RegisteredTrial>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            trials.Add(new RegisteredTrial(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetInt64(8), reader.GetString(9),
                reader.GetString(10)));
        }

        return trials;
    }

    /// <summary>
    /// Records what a registered variant produced.
    /// </summary>
    /// <remarks>
    /// Separate from registration by design, and one outcome per trial by constraint. A second
    /// result for the same declaration would mean the variant was run twice and the reader could
    /// not tell which run the registry describes.
    /// </remarks>
    public async Task RecordOutcomeAsync(TrialOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        const string insert = """
            INSERT INTO research.trial_outcomes
                (trial_id, pooled_qlike, pooled_qlike_gain, reported_log_mse,
                 diebold_mariano_statistic, diebold_mariano_p_value, p_threshold_applied,
                 folds_improved, folds_total, largest_year_share, verdict)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(insert, connection);
        command.Parameters.AddWithValue(outcome.TrialId);
        command.Parameters.AddWithValue(outcome.PooledQlike);
        command.Parameters.AddWithValue(outcome.PooledQlikeGain);
        command.Parameters.AddWithValue(outcome.ReportedLogMse);
        command.Parameters.AddWithValue(outcome.DieboldMarianoStatistic);
        command.Parameters.AddWithValue(outcome.DieboldMarianoPValue);
        command.Parameters.AddWithValue(outcome.PThresholdApplied);
        command.Parameters.AddWithValue(outcome.FoldsImproved);
        command.Parameters.AddWithValue(outcome.FoldsTotal);
        command.Parameters.AddWithValue(outcome.LargestYearShare);
        command.Parameters.AddWithValue(outcome.Verdict);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
