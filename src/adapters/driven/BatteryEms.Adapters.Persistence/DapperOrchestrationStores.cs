using BatteryEms.Application.Orchestration;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

public sealed class DapperOrchestrationRunStore : IOrchestrationRunStore
{
    private const string InsertRunSql = """
        INSERT INTO orchestration_runs (
            run_id, site_id, run_type, horizon_start, horizon_end,
            trigger_type, trigger_ref, idempotency_key, status, started_at,
            completed_at, input_hash, output_ref, error_code, error_message,
            metadata_json)
        VALUES (
            @RunId, @SiteId, @RunType, @HorizonStart, @HorizonEnd,
            @TriggerType, @TriggerRef, @IdempotencyKey, @Status, @StartedAt,
            @CompletedAt, @InputHash, @OutputRef, @ErrorCode, @ErrorMessage,
            CAST(@MetadataJson AS jsonb))
        ON CONFLICT (idempotency_key) DO NOTHING;
        """;

    private const string SelectRunByIdempotencySql = """
        SELECT *
        FROM orchestration_runs
        WHERE idempotency_key = @IdempotencyKey;
        """;

    private const string SelectCurrentRunsSql = """
        SELECT *
        FROM orchestration_runs
        WHERE site_id = @SiteId
        ORDER BY started_at DESC;
        """;

    private const string UpdateRunSql = """
        UPDATE orchestration_runs
        SET status = @Status,
            completed_at = @CompletedAt,
            output_ref = @OutputRef,
            error_code = @ErrorCode,
            error_message = @ErrorMessage,
            metadata_json = CAST(@MetadataJson AS jsonb)
        WHERE run_id = @RunId;
        """;

    private const string UpsertStepSql = """
        INSERT INTO orchestration_steps (
            step_id, run_id, site_id, module, step_order, idempotency_key,
            status, started_at, completed_at, attempt_count, next_attempt_at,
            input_hash, output_ref, data_role, data_status, error_code,
            error_message, metadata_json)
        VALUES (
            @StepId, @RunId, @SiteId, @Module, @StepOrder, @IdempotencyKey,
            @Status, @StartedAt, @CompletedAt, @AttemptCount, @NextAttemptAt,
            @InputHash, @OutputRef, @DataRole, @DataStatus, @ErrorCode,
            @ErrorMessage, CAST(@MetadataJson AS jsonb))
        ON CONFLICT (idempotency_key)
        DO UPDATE SET
            status = EXCLUDED.status,
            completed_at = EXCLUDED.completed_at,
            attempt_count = EXCLUDED.attempt_count,
            next_attempt_at = EXCLUDED.next_attempt_at,
            output_ref = EXCLUDED.output_ref,
            data_role = EXCLUDED.data_role,
            data_status = EXCLUDED.data_status,
            error_code = EXCLUDED.error_code,
            error_message = EXCLUDED.error_message,
            metadata_json = EXCLUDED.metadata_json;
        """;

    private const string SelectStepsSql = """
        SELECT *
        FROM orchestration_steps
        WHERE run_id = @RunId
        ORDER BY step_order ASC, started_at ASC;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public DapperOrchestrationRunStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        DapperConfig.EnsureConfigured();
        _dataSource = dataSource;
    }

    public async Task<OrchestrationRunStartResult> TryStartAsync(
        OrchestrationRun run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        run = run.EnsureValid();

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                InsertRunSql,
                ToRunRow(run),
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            var row = await connection.QuerySingleAsync<RunRow>(new CommandDefinition(
                SelectRunByIdempotencySql,
                new { run.IdempotencyKey },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return new OrchestrationRunStartResult(FromRunRow(row), affected == 1);
        }
    }

    public async Task CompleteAsync(
        Guid runId,
        OrchestrationRunCompletion completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                UpdateRunSql,
                new
                {
                    RunId = runId,
                    Status = completion.Status.ToString(),
                    CompletedAt = completion.CompletedAt.ToUniversalTime(),
                    completion.OutputRef,
                    completion.ErrorCode,
                    completion.ErrorMessage,
                    MetadataJson = completion.MetadataJson ?? "{}",
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (affected != 1)
            {
                throw new InvalidOperationException($"Orchestration run '{runId}' was not found.");
            }
        }
    }

    public async Task AppendStepAsync(
        OrchestrationStep orchestrationStep,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orchestrationStep);
        orchestrationStep = orchestrationStep.EnsureValid();

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                UpsertStepSql,
                ToStepRow(orchestrationStep),
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<OrchestrationRun>> QueryCurrentAsync(
        string siteId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<RunRow>(new CommandDefinition(
                SelectCurrentRunsSql,
                new { SiteId = siteId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.Select(FromRunRow).ToArray();
        }
    }

    public async Task<IReadOnlyList<OrchestrationStep>> QueryStepsAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<StepRow>(new CommandDefinition(
                SelectStepsSql,
                new { RunId = runId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.Select(FromStepRow).ToArray();
        }
    }

    private static object ToRunRow(OrchestrationRun run) => new
    {
        run.RunId,
        run.SiteId,
        run.RunType,
        HorizonStart = run.HorizonStart?.ToUniversalTime(),
        HorizonEnd = run.HorizonEnd?.ToUniversalTime(),
        TriggerType = run.TriggerType.ToString(),
        run.TriggerRef,
        run.IdempotencyKey,
        Status = run.Status.ToString(),
        StartedAt = run.StartedAt.ToUniversalTime(),
        CompletedAt = run.CompletedAt?.ToUniversalTime(),
        run.InputHash,
        run.OutputRef,
        run.ErrorCode,
        run.ErrorMessage,
        run.MetadataJson,
    };

    private static object ToStepRow(OrchestrationStep step) => new
    {
        step.StepId,
        step.RunId,
        step.SiteId,
        step.Module,
        step.StepOrder,
        step.IdempotencyKey,
        Status = step.Status.ToString(),
        StartedAt = step.StartedAt?.ToUniversalTime(),
        CompletedAt = step.CompletedAt?.ToUniversalTime(),
        step.AttemptCount,
        NextAttemptAt = step.NextAttemptAt?.ToUniversalTime(),
        step.InputHash,
        step.OutputRef,
        DataRole = step.DataRole?.ToString(),
        DataStatus = step.DataStatus?.ToString(),
        step.ErrorCode,
        step.ErrorMessage,
        step.MetadataJson,
    };

    private static OrchestrationRun FromRunRow(RunRow row) => new(
        row.RunId,
        row.SiteId,
        row.RunType,
        ToNullableOffset(row.HorizonStart),
        ToNullableOffset(row.HorizonEnd),
        Enum.Parse<OrchestrationTriggerType>(row.TriggerType),
        row.TriggerRef,
        row.IdempotencyKey,
        Enum.Parse<OrchestrationRunStatus>(row.Status),
        TimestampConverter.ToOffset(row.StartedAt),
        ToNullableOffset(row.CompletedAt),
        row.InputHash,
        row.OutputRef,
        row.ErrorCode,
        row.ErrorMessage,
        row.MetadataJson ?? "{}");

    private static OrchestrationStep FromStepRow(StepRow row) => new(
        row.StepId,
        row.RunId,
        row.SiteId,
        row.Module,
        row.StepOrder,
        row.IdempotencyKey,
        Enum.Parse<OrchestrationStepStatus>(row.Status),
        ToNullableOffset(row.StartedAt),
        ToNullableOffset(row.CompletedAt),
        row.AttemptCount,
        ToNullableOffset(row.NextAttemptAt),
        row.InputHash,
        row.OutputRef,
        row.DataRole is null ? null : Enum.Parse<DataRole>(row.DataRole),
        row.DataStatus is null ? null : Enum.Parse<DataBalanceState>(row.DataStatus),
        row.ErrorCode,
        row.ErrorMessage,
        row.MetadataJson ?? "{}");

    private static DateTimeOffset? ToNullableOffset(DateTime? utcDateTime) =>
        utcDateTime is null ? null : TimestampConverter.ToOffset(utcDateTime.Value);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class RunRow
    {
        public Guid RunId { get; init; }
        public string SiteId { get; init; } = string.Empty;
        public string RunType { get; init; } = string.Empty;
        public DateTime? HorizonStart { get; init; }
        public DateTime? HorizonEnd { get; init; }
        public string TriggerType { get; init; } = string.Empty;
        public string? TriggerRef { get; init; }
        public string IdempotencyKey { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public string? InputHash { get; init; }
        public string? OutputRef { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
        public string? MetadataJson { get; init; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class StepRow
    {
        public Guid StepId { get; init; }
        public Guid RunId { get; init; }
        public string SiteId { get; init; } = string.Empty;
        public string Module { get; init; } = string.Empty;
        public int StepOrder { get; init; }
        public string IdempotencyKey { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public DateTime? StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public int AttemptCount { get; init; }
        public DateTime? NextAttemptAt { get; init; }
        public string? InputHash { get; init; }
        public string? OutputRef { get; init; }
        public string? DataRole { get; init; }
        public string? DataStatus { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
        public string? MetadataJson { get; init; }
    }
}

public sealed class DapperOrchestrationLockStore : IOrchestrationLockStore
{
    private const string InsertSql = """
        INSERT INTO orchestration_locks (lock_key, owner_id, acquired_at, expires_at, metadata_json)
        VALUES (@LockKey, @OwnerId, @AcquiredAt, @ExpiresAt, CAST(@MetadataJson AS jsonb))
        ON CONFLICT (lock_key) DO NOTHING;
        """;

    private const string ReplaceExpiredSql = """
        UPDATE orchestration_locks
        SET owner_id = @OwnerId,
            acquired_at = @AcquiredAt,
            expires_at = @ExpiresAt,
            metadata_json = CAST(@MetadataJson AS jsonb)
        WHERE lock_key = @LockKey
          AND expires_at <= @Now;
        """;

    private const string ReleaseSql = """
        DELETE FROM orchestration_locks
        WHERE lock_key = @LockKey
          AND owner_id = @OwnerId;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public DapperOrchestrationLockStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        DapperConfig.EnsureConfigured();
        _dataSource = dataSource;
    }

    public async Task<bool> TryAcquireAsync(
        OrchestrationLock orchestrationLock,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orchestrationLock);
        orchestrationLock = orchestrationLock.EnsureValid();

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                InsertSql,
                ToLockRow(orchestrationLock),
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (affected == 1)
            {
                return true;
            }

            affected = await connection.ExecuteAsync(new CommandDefinition(
                ReplaceExpiredSql,
                new
                {
                    orchestrationLock.LockKey,
                    orchestrationLock.OwnerId,
                    AcquiredAt = orchestrationLock.AcquiredAt.ToUniversalTime(),
                    ExpiresAt = orchestrationLock.ExpiresAt.ToUniversalTime(),
                    orchestrationLock.MetadataJson,
                    Now = now.ToUniversalTime(),
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return affected == 1;
        }
    }

    public async Task ReleaseAsync(
        string lockKey,
        string ownerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                ReleaseSql,
                new { LockKey = lockKey, OwnerId = ownerId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    private static object ToLockRow(OrchestrationLock orchestrationLock) => new
    {
        orchestrationLock.LockKey,
        orchestrationLock.OwnerId,
        AcquiredAt = orchestrationLock.AcquiredAt.ToUniversalTime(),
        ExpiresAt = orchestrationLock.ExpiresAt.ToUniversalTime(),
        orchestrationLock.MetadataJson,
    };
}

public sealed class DapperDataBalanceStore : IDataBalanceStore
{
    private const string UpsertSql = """
        INSERT INTO orchestration_data_balances (
            site_id, data_group, source, instrument_id, role, status,
            freshness_deadline_utc, last_success_at_utc, last_attempt_at_utc,
            next_attempt_at_utc, attempt_count, last_error_code,
            last_error_message, quality_summary, metadata_json)
        VALUES (
            @SiteId, @DataGroup, @Source, @InstrumentId, @Role, @Status,
            @FreshnessDeadlineUtc, @LastSuccessAtUtc, @LastAttemptAtUtc,
            @NextAttemptAtUtc, @AttemptCount, @LastErrorCode,
            @LastErrorMessage, @QualitySummary, CAST(@MetadataJson AS jsonb))
        ON CONFLICT (site_id, data_group, source, instrument_id)
        DO UPDATE SET
            role = EXCLUDED.role,
            status = EXCLUDED.status,
            freshness_deadline_utc = EXCLUDED.freshness_deadline_utc,
            last_success_at_utc = EXCLUDED.last_success_at_utc,
            last_attempt_at_utc = EXCLUDED.last_attempt_at_utc,
            next_attempt_at_utc = EXCLUDED.next_attempt_at_utc,
            attempt_count = EXCLUDED.attempt_count,
            last_error_code = EXCLUDED.last_error_code,
            last_error_message = EXCLUDED.last_error_message,
            quality_summary = EXCLUDED.quality_summary,
            metadata_json = EXCLUDED.metadata_json;
        """;

    private const string SelectSql = """
        SELECT *
        FROM orchestration_data_balances
        WHERE site_id = @SiteId
        ORDER BY data_group ASC, source ASC, instrument_id ASC;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public DapperDataBalanceStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        DapperConfig.EnsureConfigured();
        _dataSource = dataSource;
    }

    public async Task UpsertAsync(
        DataBalanceStatus status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        status = status.EnsureValid();

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                UpsertSql,
                ToBalanceRow(status),
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DataBalanceStatus>> QueryAsync(
        string siteId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<BalanceRow>(new CommandDefinition(
                SelectSql,
                new { SiteId = siteId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.Select(FromBalanceRow).ToArray();
        }
    }

    private static object ToBalanceRow(DataBalanceStatus status) => new
    {
        status.SiteId,
        status.DataGroup,
        status.Source,
        status.InstrumentId,
        Role = status.Role.ToString(),
        Status = status.Status.ToString(),
        FreshnessDeadlineUtc = status.FreshnessDeadlineUtc?.ToUniversalTime(),
        LastSuccessAtUtc = status.LastSuccessAtUtc?.ToUniversalTime(),
        LastAttemptAtUtc = status.LastAttemptAtUtc?.ToUniversalTime(),
        NextAttemptAtUtc = status.NextAttemptAtUtc?.ToUniversalTime(),
        status.AttemptCount,
        status.LastErrorCode,
        status.LastErrorMessage,
        status.QualitySummary,
        status.MetadataJson,
    };

    private static DataBalanceStatus FromBalanceRow(BalanceRow row) => new(
        row.SiteId,
        row.DataGroup,
        row.Source,
        row.InstrumentId,
        Enum.Parse<DataRole>(row.Role),
        Enum.Parse<DataBalanceState>(row.Status),
        ToNullableOffset(row.FreshnessDeadlineUtc),
        ToNullableOffset(row.LastSuccessAtUtc),
        ToNullableOffset(row.LastAttemptAtUtc),
        ToNullableOffset(row.NextAttemptAtUtc),
        row.AttemptCount,
        row.LastErrorCode,
        row.LastErrorMessage,
        row.QualitySummary,
        row.MetadataJson ?? "{}");

    private static DateTimeOffset? ToNullableOffset(DateTime? utcDateTime) =>
        utcDateTime is null ? null : TimestampConverter.ToOffset(utcDateTime.Value);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class BalanceRow
    {
        public string SiteId { get; init; } = string.Empty;
        public string DataGroup { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public string InstrumentId { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public DateTime? FreshnessDeadlineUtc { get; init; }
        public DateTime? LastSuccessAtUtc { get; init; }
        public DateTime? LastAttemptAtUtc { get; init; }
        public DateTime? NextAttemptAtUtc { get; init; }
        public int AttemptCount { get; init; }
        public string? LastErrorCode { get; init; }
        public string? LastErrorMessage { get; init; }
        public string? QualitySummary { get; init; }
        public string? MetadataJson { get; init; }
    }
}
