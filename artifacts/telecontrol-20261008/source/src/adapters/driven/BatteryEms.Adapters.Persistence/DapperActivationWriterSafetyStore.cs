using System.Data;
using BatteryEms.Application.Orchestration;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

public sealed class DapperActivationWriterSafetyStore : IActivationWriterSafetyStore
{
    private const string SelectStateSql = """
        SELECT *
        FROM activation_writer_safety
        WHERE site_id = @SiteId;
        """;

    private const string InsertStateSql = """
        INSERT INTO activation_writer_safety (
            site_id, kill_switch_engaged, writer_authority,
            legacy_writer_stopped_at_utc, legacy_stop_evidence,
            revision, updated_at_utc, updated_by, reason)
        VALUES (
            @SiteId, @KillSwitchEngaged, @WriterAuthority,
            @LegacyWriterStoppedAtUtc, @LegacyStopEvidence,
            @Revision, @UpdatedAtUtc, @UpdatedBy, @Reason)
        ON CONFLICT (site_id) DO NOTHING;
        """;

    private const string UpdateStateSql = """
        UPDATE activation_writer_safety
        SET kill_switch_engaged = @KillSwitchEngaged,
            writer_authority = @WriterAuthority,
            legacy_writer_stopped_at_utc = @LegacyWriterStoppedAtUtc,
            legacy_stop_evidence = @LegacyStopEvidence,
            revision = @Revision,
            updated_at_utc = @UpdatedAtUtc,
            updated_by = @UpdatedBy,
            reason = @Reason
        WHERE site_id = @SiteId
          AND revision = @ExpectedRevision;
        """;

    private const string EnsureFenceSequenceSql = """
        INSERT INTO activation_writer_fence_sequences (site_id, last_fencing_token)
        VALUES (@SiteId, 0)
        ON CONFLICT (site_id) DO NOTHING;
        """;

    private const string LockFenceSequenceSql = """
        SELECT last_fencing_token
        FROM activation_writer_fence_sequences
        WHERE site_id = @SiteId
        FOR UPDATE;
        """;

    private const string IncrementFenceSequenceSql = """
        UPDATE activation_writer_fence_sequences
        SET last_fencing_token = last_fencing_token + 1
        WHERE site_id = @SiteId
        RETURNING last_fencing_token;
        """;

    private const string SelectLeaseSql = """
        SELECT *
        FROM activation_writer_leases
        WHERE site_id = @SiteId;
        """;

    private const string SelectLeaseForUpdateSql = """
        SELECT *
        FROM activation_writer_leases
        WHERE site_id = @SiteId
        FOR UPDATE;
        """;

    private const string UpsertLeaseSql = """
        INSERT INTO activation_writer_leases (
            site_id, owner_id, fencing_token, acquired_at_utc, expires_at_utc)
        VALUES (
            @SiteId, @OwnerId, @FencingToken, @AcquiredAtUtc, @ExpiresAtUtc)
        ON CONFLICT (site_id)
        DO UPDATE SET
            owner_id = EXCLUDED.owner_id,
            fencing_token = EXCLUDED.fencing_token,
            acquired_at_utc = EXCLUDED.acquired_at_utc,
            expires_at_utc = EXCLUDED.expires_at_utc;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public DapperActivationWriterSafetyStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        DapperConfig.EnsureConfigured();
        _dataSource = dataSource;
    }

    public async Task<ActivationSafetyState?> FindStateAsync(
        string siteId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var row = await connection.QuerySingleOrDefaultAsync<SafetyStateRow>(new CommandDefinition(
                SelectStateSql,
                new { SiteId = siteId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return row is null ? null : FromRow(row);
        }
    }

    public async Task<bool> CompareExchangeStateAsync(
        ActivationSafetyState nextState,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nextState);
        nextState = nextState.EnsureValid();
        if (expectedRevision is null && nextState.Revision != 1)
        {
            return false;
        }

        if (expectedRevision is not null && nextState.Revision != expectedRevision + 1)
        {
            return false;
        }

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = expectedRevision is null
                ? new CommandDefinition(
                    InsertStateSql,
                    ToParameters(nextState),
                    cancellationToken: cancellationToken)
                : new CommandDefinition(
                    UpdateStateSql,
                    MergeExpectedRevision(nextState, expectedRevision.Value),
                    cancellationToken: cancellationToken);
            return await connection.ExecuteAsync(command).ConfigureAwait(false) == 1;
        }
    }

    public async Task<ActivationLeaseAcquireResult> TryAcquireLeaseAsync(
        string siteId,
        string ownerId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (leaseDuration <= TimeSpan.Zero
            || leaseDuration > InMemoryActivationWriterSafetyStore.MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                leaseDuration,
                $"Writer lease must be positive and at most {InMemoryActivationWriterSafetyStore.MaximumLeaseDuration}.");
        }

        nowUtc = nowUtc.ToUniversalTime();
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    EnsureFenceSequenceSql,
                    new { SiteId = siteId },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                await connection.QuerySingleAsync<long>(new CommandDefinition(
                    LockFenceSequenceSql,
                    new { SiteId = siteId },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                var row = await connection.QuerySingleOrDefaultAsync<WriterLeaseRow>(new CommandDefinition(
                    SelectLeaseForUpdateSql,
                    new { SiteId = siteId },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                var existing = row is null ? null : FromRow(row);
                if (existing is not null
                    && existing.ExpiresAtUtc > nowUtc
                    && !string.Equals(existing.OwnerId, ownerId, StringComparison.Ordinal))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new ActivationLeaseAcquireResult(
                        existing,
                        Acquired: false,
                        "activation-writer-lease-owned-by-other");
                }

                var isRenewal = existing is not null
                    && existing.ExpiresAtUtc > nowUtc
                    && string.Equals(existing.OwnerId, ownerId, StringComparison.Ordinal);
                var token = isRenewal
                    ? existing!.FencingToken
                    : await connection.QuerySingleAsync<long>(new CommandDefinition(
                        IncrementFenceSequenceSql,
                        new { SiteId = siteId },
                        transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                var lease = new ActivationWriterLease(
                    siteId,
                    ownerId,
                    token,
                    nowUtc,
                    nowUtc.Add(leaseDuration)).EnsureValid();
                await connection.ExecuteAsync(new CommandDefinition(
                    UpsertLeaseSql,
                    ToParameters(lease),
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ActivationLeaseAcquireResult(lease, Acquired: true);
            }
        }
    }

    public async Task<ActivationWriterLease?> FindLeaseAsync(
        string siteId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var row = await connection.QuerySingleOrDefaultAsync<WriterLeaseRow>(new CommandDefinition(
                SelectLeaseSql,
                new { SiteId = siteId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return row is null ? null : FromRow(row);
        }
    }

    private static object ToParameters(ActivationSafetyState state) => new
    {
        state.SiteId,
        state.KillSwitchEngaged,
        WriterAuthority = state.WriterAuthority.ToString(),
        LegacyWriterStoppedAtUtc = state.LegacyWriterStoppedAtUtc?.ToUniversalTime(),
        state.LegacyStopEvidence,
        state.Revision,
        UpdatedAtUtc = state.UpdatedAtUtc.ToUniversalTime(),
        state.UpdatedBy,
        state.Reason,
    };

    private static object MergeExpectedRevision(ActivationSafetyState state, long expectedRevision) => new
    {
        state.SiteId,
        state.KillSwitchEngaged,
        WriterAuthority = state.WriterAuthority.ToString(),
        LegacyWriterStoppedAtUtc = state.LegacyWriterStoppedAtUtc?.ToUniversalTime(),
        state.LegacyStopEvidence,
        state.Revision,
        UpdatedAtUtc = state.UpdatedAtUtc.ToUniversalTime(),
        state.UpdatedBy,
        state.Reason,
        ExpectedRevision = expectedRevision,
    };

    private static object ToParameters(ActivationWriterLease lease) => new
    {
        lease.SiteId,
        lease.OwnerId,
        lease.FencingToken,
        AcquiredAtUtc = lease.AcquiredAtUtc.ToUniversalTime(),
        ExpiresAtUtc = lease.ExpiresAtUtc.ToUniversalTime(),
    };

    private static ActivationSafetyState FromRow(SafetyStateRow row) => new ActivationSafetyState(
        row.SiteId,
        row.KillSwitchEngaged,
        Enum.Parse<ActivationWriterAuthority>(row.WriterAuthority),
        row.LegacyWriterStoppedAtUtc is null
            ? null
            : TimestampConverter.ToOffset(row.LegacyWriterStoppedAtUtc.Value),
        row.LegacyStopEvidence,
        row.Revision,
        TimestampConverter.ToOffset(row.UpdatedAtUtc),
        row.UpdatedBy,
        row.Reason).EnsureValid();

    private static ActivationWriterLease FromRow(WriterLeaseRow row) => new ActivationWriterLease(
        row.SiteId,
        row.OwnerId,
        row.FencingToken,
        TimestampConverter.ToOffset(row.AcquiredAtUtc),
        TimestampConverter.ToOffset(row.ExpiresAtUtc)).EnsureValid();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class SafetyStateRow
    {
        public string SiteId { get; init; } = string.Empty;
        public bool KillSwitchEngaged { get; init; }
        public string WriterAuthority { get; init; } = string.Empty;
        public DateTime? LegacyWriterStoppedAtUtc { get; init; }
        public string? LegacyStopEvidence { get; init; }
        public long Revision { get; init; }
        public DateTime UpdatedAtUtc { get; init; }
        public string UpdatedBy { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class WriterLeaseRow
    {
        public string SiteId { get; init; } = string.Empty;
        public string OwnerId { get; init; } = string.Empty;
        public long FencingToken { get; init; }
        public DateTime AcquiredAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
    }
}
