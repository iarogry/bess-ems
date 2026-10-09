using System.Data;
using System.Text.Json;
using BatteryEms.Application.Orchestration;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

public sealed class DapperActivationCutoverStore : IActivationCutoverStore
{
    private const string SelectProposalForUpdateSql = """
        SELECT *, payload_json::text AS PayloadJson
        FROM activation_proposals
        WHERE proposal_id = @ProposalId
        FOR UPDATE;
        """;

    private const string SelectOutboxForUpdateSql = """
        SELECT *, payload_json::text AS PayloadJson
        FROM activation_outbox
        WHERE outbox_item_id = @OutboxItemId
        FOR UPDATE;
        """;

    private const string SelectSafetyForUpdateSql = """
        SELECT *
        FROM activation_writer_safety
        WHERE site_id = @SiteId
        FOR UPDATE;
        """;

    private const string SelectLeaseForUpdateSql = """
        SELECT *
        FROM activation_writer_leases
        WHERE site_id = @SiteId
        FOR UPDATE;
        """;

    private const string SelectPilotSessionForUpdateSql = """
        SELECT *
        FROM activation_pilot_sessions
        WHERE session_id = @PilotSessionId
        FOR UPDATE;
        """;

    private const string MarkReadySql = """
        UPDATE activation_outbox
        SET status = 'Ready',
            updated_at_utc = @ReleasedAtUtc,
            release_writer_owner_id = @WriterOwnerId,
            release_safety_revision = @ExpectedSafetyRevision,
            release_fencing_token = @ExpectedFencingToken,
            release_pilot_session_id = @PilotSessionId,
            release_window_id = @ReleaseWindowId,
            released_by = @ReleasedBy,
            release_reason = @Reason,
            released_at_utc = @ReleasedAtUtc
        WHERE outbox_item_id = @OutboxItemId
          AND status = 'Held';
        """;

    private const string RollbackSafetySql = """
        UPDATE activation_writer_safety
        SET kill_switch_engaged = TRUE,
            writer_authority = 'None',
            revision = revision + 1,
            updated_at_utc = @NowUtc,
            updated_by = @RolledBackBy,
            reason = @Reason,
            last_rollback_operation_id = @OperationId,
            last_rollback_cancelled_count = @CancelledReadyItems
        WHERE site_id = @SiteId
          AND revision = @ExpectedSafetyRevision;
        """;

    private const string CancelReadySql = """
        UPDATE activation_outbox
        SET status = 'Cancelled',
            updated_at_utc = @NowUtc,
            claim_id = NULL,
            claim_executor_id = NULL,
            claim_safety_revision = NULL,
            claim_fencing_token = NULL,
            claim_pilot_session_id = NULL,
            claim_window_id = NULL,
            claim_window_payload_hash = NULL,
            claimed_at_utc = NULL,
            claim_expires_at_utc = NULL,
            claim_outcome_code = NULL,
            claim_completed_at_utc = NULL
        WHERE site_id = @SiteId
          AND status IN ('Ready', 'Claimed');
        """;

    private const string DeleteLeaseSql = """
        DELETE FROM activation_writer_leases
        WHERE site_id = @SiteId;
        """;

    private const string AbortArmedPilotSessionsSql = """
        UPDATE activation_pilot_sessions
        SET status = 'Aborted',
            aborted_by = @RolledBackBy,
            abort_reason = @Reason,
            aborted_at_utc = @NowUtc
        WHERE site_id = @SiteId
          AND status = 'Armed';
        """;

    private const string SelectPilotEvidenceSql = """
        SELECT DISTINCT ON (delivery_date) *
        FROM shadow_plan_comparisons
        WHERE site_id = @SiteId
          AND delivery_date >= @WindowStart
          AND delivery_date <= @WindowEnd
        ORDER BY delivery_date ASC, compared_at_utc DESC, comparison_id DESC;
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PilotReadinessOptions _readinessOptions;
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public DapperActivationCutoverStore(
        NpgsqlDataSource dataSource,
        PilotReadinessOptions? readinessOptions = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        DapperConfig.EnsureConfigured();
        _dataSource = dataSource;
        _readinessOptions = (readinessOptions ?? new PilotReadinessOptions()).EnsureValid();
    }

    public async Task<ActivationOutboxReleaseResult> ReleaseHeldAsync(
        ActivationOutboxReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.EnsureValid();
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var proposalRow = await connection.QuerySingleOrDefaultAsync<ProposalRow>(new CommandDefinition(
                    SelectProposalForUpdateSql,
                    new { request.ProposalId },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                if (proposalRow is null)
                {
                    return await CommitReleaseFailureAsync(
                        transaction,
                        "activation-proposal-not-found",
                        cancellationToken).ConfigureAwait(false);
                }

                var proposal = FromRow(proposalRow);
                if (proposal.Status != ActivationProposalStatus.Approved
                    || proposal.OutboxItemId is null)
                {
                    return await CommitReleaseFailureAsync(
                        transaction,
                        "activation-proposal-not-approved",
                        cancellationToken,
                        proposal).ConfigureAwait(false);
                }

                if (string.Equals(proposal.ProposedBy, request.ReleasedBy, StringComparison.Ordinal))
                {
                    return await CommitReleaseFailureAsync(
                        transaction,
                        "activation-release-four-eyes-required",
                        cancellationToken,
                        proposal).ConfigureAwait(false);
                }

                var outboxRow = await connection.QuerySingleOrDefaultAsync<OutboxRow>(new CommandDefinition(
                    SelectOutboxForUpdateSql,
                    new { OutboxItemId = proposal.OutboxItemId.Value },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                if (outboxRow is null)
                {
                    return await CommitReleaseFailureAsync(
                        transaction,
                        "activation-outbox-not-found",
                        cancellationToken,
                        proposal).ConfigureAwait(false);
                }

                var outbox = FromRow(outboxRow);
                if (outbox.Status == ActivationOutboxStatus.Ready)
                {
                    var isReplay = string.Equals(
                            outbox.ReleaseWriterOwnerId,
                            request.WriterOwnerId,
                            StringComparison.Ordinal)
                        && outbox.ReleaseSafetyRevision == request.ExpectedSafetyRevision
                        && outbox.ReleaseFencingToken == request.ExpectedFencingToken
                        && outbox.ReleasePilotSessionId == request.PilotSessionId;
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return isReplay
                        ? new ActivationOutboxReleaseResult(proposal, outbox, Released: true)
                        : new ActivationOutboxReleaseResult(
                            proposal,
                            outbox,
                            Released: false,
                            "activation-outbox-already-released");
                }

                if (outbox.Status != ActivationOutboxStatus.Held)
                {
                    return await CommitReleaseFailureAsync(
                        transaction,
                        "activation-outbox-not-held",
                        cancellationToken,
                        proposal,
                        outbox).ConfigureAwait(false);
                }

                var pilotReady = await ValidatePilotReadinessAsync(
                    connection,
                    transaction,
                    proposal.SiteId,
                    proposal.DeliveryDate,
                    cancellationToken).ConfigureAwait(false);
                if (!pilotReady)
                {
                    return await CommitReleaseFailureAsync(
                        transaction,
                        "activation-pilot-readiness-not-met",
                        cancellationToken,
                        proposal,
                        outbox).ConfigureAwait(false);
                }

                var pilotValidation = await ValidatePilotSessionAsync(
                    connection,
                    transaction,
                    proposal,
                    outbox,
                    request,
                    cancellationToken).ConfigureAwait(false);
                if (pilotValidation.ErrorCode is not null)
                {
                    return await CommitReleaseFailureAsync(
                        transaction,
                        pilotValidation.ErrorCode,
                        cancellationToken,
                        proposal,
                        outbox).ConfigureAwait(false);
                }

                var gateFailure = await ValidateWriterGateAsync(
                    connection,
                    transaction,
                    proposal.SiteId,
                    request,
                    cancellationToken).ConfigureAwait(false);
                if (gateFailure is not null)
                {
                    return await CommitReleaseFailureAsync(
                        transaction,
                        gateFailure,
                        cancellationToken,
                        proposal,
                        outbox).ConfigureAwait(false);
                }

                var releasedAt = request.NowUtc.ToUniversalTime();
                var affected = await connection.ExecuteAsync(new CommandDefinition(
                    MarkReadySql,
                    new
                    {
                        outbox.OutboxItemId,
                        request.WriterOwnerId,
                        request.ExpectedSafetyRevision,
                        request.ExpectedFencingToken,
                        request.PilotSessionId,
                        ReleaseWindowId = pilotValidation.Session!.WindowId,
                        request.ReleasedBy,
                        request.Reason,
                        ReleasedAtUtc = releasedAt,
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                if (affected != 1)
                {
                    throw new InvalidOperationException("Held activation outbox transition lost its row lock.");
                }

                outbox = outbox with
                {
                    Status = ActivationOutboxStatus.Ready,
                    UpdatedAtUtc = releasedAt,
                    ReleaseWriterOwnerId = request.WriterOwnerId,
                    ReleaseSafetyRevision = request.ExpectedSafetyRevision,
                    ReleaseFencingToken = request.ExpectedFencingToken,
                    ReleasePilotSessionId = request.PilotSessionId,
                    ReleaseWindowId = pilotValidation.Session!.WindowId,
                    ReleasedBy = request.ReleasedBy,
                    ReleaseReason = request.Reason,
                    ReleasedAtUtc = releasedAt,
                };
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ActivationOutboxReleaseResult(
                    proposal,
                    outbox.EnsureValid(),
                    Released: true);
            }
        }
    }

    public async Task<ActivationRollbackResult> RollbackAsync(
        ActivationRollbackRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.EnsureValid();
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var row = await connection.QuerySingleOrDefaultAsync<SafetyStateRow>(new CommandDefinition(
                    SelectSafetyForUpdateSql,
                    new { request.SiteId },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                if (row is null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new ActivationRollbackResult(
                        null,
                        CancelledReadyItems: 0,
                        RolledBack: false,
                        "activation-safety-state-missing");
                }

                var current = FromRow(row);
                if (row.LastRollbackOperationId == request.OperationId)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new ActivationRollbackResult(
                        current,
                        row.LastRollbackCancelledCount ?? 0,
                        RolledBack: true);
                }

                if (current.Revision != request.ExpectedSafetyRevision)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new ActivationRollbackResult(
                        current,
                        CancelledReadyItems: 0,
                        RolledBack: false,
                        "activation-safety-revision-mismatch");
                }

                var now = request.NowUtc.ToUniversalTime();
                var cancelled = await connection.ExecuteAsync(new CommandDefinition(
                    CancelReadySql,
                    new { request.SiteId, NowUtc = now },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                var updated = await connection.ExecuteAsync(new CommandDefinition(
                    RollbackSafetySql,
                    new
                    {
                        request.OperationId,
                        request.SiteId,
                        request.ExpectedSafetyRevision,
                        request.RolledBackBy,
                        request.Reason,
                        NowUtc = now,
                        CancelledReadyItems = cancelled,
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                if (updated != 1)
                {
                    throw new InvalidOperationException("Activation rollback lost its safety-state row lock.");
                }

                await connection.ExecuteAsync(new CommandDefinition(
                    DeleteLeaseSql,
                    new { request.SiteId },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                await connection.ExecuteAsync(new CommandDefinition(
                    AbortArmedPilotSessionsSql,
                    new
                    {
                        request.SiteId,
                        request.RolledBackBy,
                        request.Reason,
                        NowUtc = now,
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                var rolledBackState = current with
                {
                    KillSwitchEngaged = true,
                    WriterAuthority = ActivationWriterAuthority.None,
                    Revision = current.Revision + 1,
                    UpdatedAtUtc = now,
                    UpdatedBy = request.RolledBackBy,
                    Reason = request.Reason,
                };
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ActivationRollbackResult(
                    rolledBackState.EnsureValid(),
                    cancelled,
                    RolledBack: true);
            }
        }
    }

    private static async Task<string?> ValidateWriterGateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string siteId,
        ActivationOutboxReleaseRequest request,
        CancellationToken cancellationToken)
    {
        var safetyRow = await connection.QuerySingleOrDefaultAsync<SafetyStateRow>(new CommandDefinition(
            SelectSafetyForUpdateSql,
            new { SiteId = siteId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var leaseRow = await connection.QuerySingleOrDefaultAsync<WriterLeaseRow>(new CommandDefinition(
            SelectLeaseForUpdateSql,
            new { SiteId = siteId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var safety = safetyRow is null ? null : FromRow(safetyRow);
        var lease = leaseRow is null ? null : FromRow(leaseRow);
        if (safety is not null && safety.Revision != request.ExpectedSafetyRevision)
        {
            return "activation-safety-revision-mismatch";
        }

        var gate = ActivationWriterSafetyGate.Evaluate(
            siteId,
            request.WriterOwnerId,
            safety,
            lease,
            request.NowUtc);
        if (!gate.CanWrite)
        {
            return gate.BlockingCode;
        }

        return gate.FencingToken != request.ExpectedFencingToken
            ? "activation-fencing-token-mismatch"
            : null;
    }

    private static async Task<(ActivationPilotSession? Session, string? ErrorCode)> ValidatePilotSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActivationProposal proposal,
        ActivationOutboxItem outbox,
        ActivationOutboxReleaseRequest request,
        CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<PilotSessionRow>(new CommandDefinition(
            SelectPilotSessionForUpdateSql,
            new { request.PilotSessionId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return (null, "activation-pilot-session-not-found");
        }

        var session = PilotFromRow(row);
        if (session.ProposalId != proposal.ProposalId
            || session.OutboxItemId != outbox.OutboxItemId
            || !string.Equals(session.SiteId, proposal.SiteId, StringComparison.Ordinal))
        {
            return (session, "activation-pilot-session-scope-mismatch");
        }

        if (!session.IsActiveAt(request.NowUtc))
        {
            return (session, session.Status == ActivationPilotSessionStatus.Armed
                ? "activation-pilot-session-expired"
                : "activation-pilot-session-not-armed");
        }

        if (!string.Equals(session.WriterOwnerId, request.WriterOwnerId, StringComparison.Ordinal)
            || session.SafetyRevision != request.ExpectedSafetyRevision
            || session.FencingToken != request.ExpectedFencingToken)
        {
            return (session, "activation-pilot-session-safety-mismatch");
        }

        return string.Equals(session.ArmedBy, request.ReleasedBy, StringComparison.Ordinal)
            ? (session, "activation-pilot-release-four-eyes-required")
            : (session, null);
    }

    private async Task<bool> ValidatePilotReadinessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string siteId,
        DateOnly windowEnd,
        CancellationToken cancellationToken)
    {
        var windowStart = windowEnd.AddDays(-(_readinessOptions.RequiredConsecutiveDays - 1));
        var rows = await connection.QueryAsync<ComparisonRow>(new CommandDefinition(
            SelectPilotEvidenceSql,
            new { SiteId = siteId, WindowStart = windowStart, WindowEnd = windowEnd },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var evidence = rows.Select(FromRow).ToArray();
        return PilotReadinessPolicy.Evaluate(
            siteId,
            windowEnd,
            _readinessOptions,
            evidence).IsReady;
    }

    private static async Task<ActivationOutboxReleaseResult> CommitReleaseFailureAsync(
        NpgsqlTransaction transaction,
        string errorCode,
        CancellationToken cancellationToken,
        ActivationProposal? proposal = null,
        ActivationOutboxItem? outbox = null)
    {
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ActivationOutboxReleaseResult(proposal, outbox, Released: false, errorCode);
    }

    private static ActivationProposal FromRow(ProposalRow row) => new ActivationProposal(
        row.ProposalId,
        row.ComparisonId,
        row.RunId,
        row.SiteId,
        row.DeliveryDate,
        row.PayloadHash,
        row.PayloadJson,
        row.ProposedBy,
        row.ProposalReason,
        TimestampConverter.ToOffset(row.CreatedAtUtc),
        TimestampConverter.ToOffset(row.ExpiresAtUtc),
        Enum.Parse<ActivationProposalStatus>(row.Status),
        row.ReviewedBy,
        row.ReviewReason,
        row.ReviewedAtUtc is null ? null : TimestampConverter.ToOffset(row.ReviewedAtUtc.Value),
        row.OutboxItemId).EnsureValid();

    private static ActivationOutboxItem FromRow(OutboxRow row) => new ActivationOutboxItem(
        row.OutboxItemId,
        row.ProposalId,
        row.SiteId,
        row.DeliveryDate,
        row.PayloadHash,
        row.PayloadJson,
        row.IdempotencyKey,
        Enum.Parse<ActivationOutboxStatus>(row.Status),
        TimestampConverter.ToOffset(row.CreatedAtUtc),
        row.UpdatedAtUtc is null ? null : TimestampConverter.ToOffset(row.UpdatedAtUtc.Value),
        row.ReleaseWriterOwnerId,
        row.ReleaseSafetyRevision,
        row.ReleaseFencingToken,
        row.ReleasePilotSessionId,
        row.ReleaseWindowId,
        row.ReleasedBy,
        row.ReleaseReason,
        row.ReleasedAtUtc is null ? null : TimestampConverter.ToOffset(row.ReleasedAtUtc.Value)).EnsureValid();

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

    private static ActivationPilotSession PilotFromRow(PilotSessionRow row) => new ActivationPilotSession(
        row.SessionId,
        row.ProposalId,
        row.OutboxItemId,
        row.SiteId,
        row.WindowId,
        row.WriterOwnerId,
        row.SafetyRevision,
        row.FencingToken,
        Enum.Parse<ActivationPilotSessionStatus>(row.Status),
        row.ArmedBy,
        row.ArmReason,
        TimestampConverter.ToOffset(row.ArmedAtUtc),
        TimestampConverter.ToOffset(row.ExpiresAtUtc),
        row.AbortedBy,
        row.AbortReason,
        row.AbortedAtUtc is null ? null : TimestampConverter.ToOffset(row.AbortedAtUtc.Value)).EnsureValid();

    private static ShadowPlanComparisonRecord FromRow(ComparisonRow row) => new(
        row.ComparisonId,
        row.RunId,
        row.SiteId,
        row.DeliveryDate,
        TimestampConverter.ToOffset(row.ComparedAtUtc),
        row.IsEquivalent,
        row.LegacyPayloadReady,
        row.ShadowPayloadReady,
        JsonSerializer.Deserialize<ShadowPlanMismatch[]>(
            row.MismatchesJson,
            SerializerOptions) ?? []);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class ProposalRow
    {
        public Guid ProposalId { get; init; }
        public Guid ComparisonId { get; init; }
        public Guid RunId { get; init; }
        public string SiteId { get; init; } = string.Empty;
        public DateOnly DeliveryDate { get; init; }
        public string PayloadHash { get; init; } = string.Empty;
        public string PayloadJson { get; init; } = string.Empty;
        public string ProposedBy { get; init; } = string.Empty;
        public string ProposalReason { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
        public string Status { get; init; } = string.Empty;
        public string? ReviewedBy { get; init; }
        public string? ReviewReason { get; init; }
        public DateTime? ReviewedAtUtc { get; init; }
        public Guid? OutboxItemId { get; init; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class OutboxRow
    {
        public Guid OutboxItemId { get; init; }
        public Guid ProposalId { get; init; }
        public string SiteId { get; init; } = string.Empty;
        public DateOnly DeliveryDate { get; init; }
        public string PayloadHash { get; init; } = string.Empty;
        public string PayloadJson { get; init; } = string.Empty;
        public string IdempotencyKey { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; }
        public DateTime? UpdatedAtUtc { get; init; }
        public string? ReleaseWriterOwnerId { get; init; }
        public long? ReleaseSafetyRevision { get; init; }
        public long? ReleaseFencingToken { get; init; }
        public Guid? ReleasePilotSessionId { get; init; }
        public string? ReleaseWindowId { get; init; }
        public string? ReleasedBy { get; init; }
        public string? ReleaseReason { get; init; }
        public DateTime? ReleasedAtUtc { get; init; }
    }

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
        public Guid? LastRollbackOperationId { get; init; }
        public int? LastRollbackCancelledCount { get; init; }
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class ComparisonRow
    {
        public Guid ComparisonId { get; init; }
        public Guid RunId { get; init; }
        public string SiteId { get; init; } = string.Empty;
        public DateOnly DeliveryDate { get; init; }
        public DateTime ComparedAtUtc { get; init; }
        public bool IsEquivalent { get; init; }
        public bool LegacyPayloadReady { get; init; }
        public bool ShadowPayloadReady { get; init; }
        public string MismatchesJson { get; init; } = "[]";
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class PilotSessionRow
    {
        public Guid SessionId { get; init; }
        public Guid ProposalId { get; init; }
        public Guid OutboxItemId { get; init; }
        public string SiteId { get; init; } = string.Empty;
        public string WindowId { get; init; } = string.Empty;
        public string WriterOwnerId { get; init; } = string.Empty;
        public long SafetyRevision { get; init; }
        public long FencingToken { get; init; }
        public string Status { get; init; } = string.Empty;
        public string ArmedBy { get; init; } = string.Empty;
        public string ArmReason { get; init; } = string.Empty;
        public DateTime ArmedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
        public string? AbortedBy { get; init; }
        public string? AbortReason { get; init; }
        public DateTime? AbortedAtUtc { get; init; }
    }
}
