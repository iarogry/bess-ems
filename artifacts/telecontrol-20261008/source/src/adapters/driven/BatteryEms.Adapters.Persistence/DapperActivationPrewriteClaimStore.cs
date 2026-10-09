using System.Data;
using BatteryEms.Application.Orchestration;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

public sealed class DapperActivationPrewriteClaimStore : IActivationPrewriteClaimStore
{
    private const string SelectOutboxSql = """
        SELECT *, payload_json::text AS PayloadJson
        FROM activation_outbox
        WHERE outbox_item_id = @OutboxItemId
        FOR UPDATE;
        """;

    private const string SelectPilotSql = """
        SELECT *
        FROM activation_pilot_sessions
        WHERE session_id = @PilotSessionId
        FOR UPDATE;
        """;

    private const string SelectSafetySql = """
        SELECT *
        FROM activation_writer_safety
        WHERE site_id = @SiteId
        FOR UPDATE;
        """;

    private const string SelectLeaseSql = """
        SELECT *
        FROM activation_writer_leases
        WHERE site_id = @SiteId
        FOR UPDATE;
        """;

    private const string ClaimSql = """
        UPDATE activation_outbox
        SET status = 'Claimed',
            updated_at_utc = @ClaimedAtUtc,
            claim_id = @ClaimId,
            claim_executor_id = @ExecutorId,
            claim_safety_revision = @ExpectedSafetyRevision,
            claim_fencing_token = @ExpectedFencingToken,
            claim_pilot_session_id = @PilotSessionId,
            claim_window_id = @WindowId,
            claim_window_payload_hash = @WindowPayloadHash,
            claimed_at_utc = @ClaimedAtUtc,
            claim_expires_at_utc = @ExpiresAtUtc
        WHERE outbox_item_id = @OutboxItemId
          AND status = 'Ready';
        """;

    private const string CompleteSql = """
        UPDATE activation_outbox
        SET status = @Status,
            updated_at_utc = @CompletedAtUtc,
            claim_outcome_code = @OutcomeCode,
            claim_completed_at_utc = @CompletedAtUtc
        WHERE outbox_item_id = @OutboxItemId
          AND status = 'Claimed'
          AND claim_id = @ClaimId
          AND claim_executor_id = @ExecutorId;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public DapperActivationPrewriteClaimStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        DapperConfig.EnsureConfigured();
        _dataSource = dataSource;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Maintainability",
        "CA1506",
        Justification = "The serializable pre-write transaction intentionally locks every safety authority as one fail-closed unit.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Maintainability",
        "CA1502",
        Justification = "Each branch maps one explicit fail-closed invariant inside the single serializable claim transaction.")]
    public async Task<ActivationPrewriteClaimResult> TryClaimAsync(
        ActivationPrewriteClaimRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.EnsureValid();
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<OutboxRow>(new CommandDefinition(
            SelectOutboxSql,
            new { request.OutboxItemId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return await FailClaimAsync(transaction, "activation-outbox-not-found", cancellationToken).ConfigureAwait(false);
        }

        var now = request.NowUtc.ToUniversalTime();
        if (string.Equals(row.Status, "Claimed", StringComparison.Ordinal))
        {
            var replay = row.ClaimId == request.ClaimId
                && string.Equals(row.ClaimExecutorId, request.ExecutorId, StringComparison.Ordinal)
                && string.Equals(row.ReleaseWriterOwnerId, request.WriterOwnerId, StringComparison.Ordinal)
                && row.ClaimSafetyRevision == request.ExpectedSafetyRevision
                && row.ClaimFencingToken == request.ExpectedFencingToken;
            if (!replay)
            {
                return await FailClaimAsync(
                    transaction,
                    row.ClaimExpiresAtUtc <= now.UtcDateTime
                        ? "activation-prewrite-claim-expired-rollback-required"
                        : "activation-outbox-already-claimed",
                    cancellationToken).ConfigureAwait(false);
            }

            if (row.ClaimExpiresAtUtc is null || row.ClaimExpiresAtUtc <= now.UtcDateTime)
            {
                return await FailClaimAsync(
                    transaction,
                    "activation-prewrite-claim-expired-rollback-required",
                    cancellationToken).ConfigureAwait(false);
            }

            var replayClaim = ToClaim(row);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ActivationPrewriteClaimResult(replayClaim, Claimed: true, IsReplay: true);
        }

        if (!string.Equals(row.Status, "Ready", StringComparison.Ordinal))
        {
            return await FailClaimAsync(transaction, "activation-outbox-not-ready", cancellationToken).ConfigureAwait(false);
        }

        if (row.ReleasePilotSessionId is null
            || !ActivationPayloadIntegrity.IsWindowIdValid(row.ReleaseWindowId)
            || row.ReleaseSafetyRevision != request.ExpectedSafetyRevision
            || row.ReleaseFencingToken != request.ExpectedFencingToken
            || !string.Equals(row.ReleaseWriterOwnerId, request.WriterOwnerId, StringComparison.Ordinal))
        {
            return await FailClaimAsync(transaction, "activation-release-authority-mismatch", cancellationToken).ConfigureAwait(false);
        }

        if (!ActivationPayloadIntegrity.TryReadWindow(
                row.PayloadJson,
                row.PayloadHash,
                row.ReleaseWindowId!,
                out var plan,
                out var releaseWindow)
            || releaseWindow is null
            || plan is null
            || !string.Equals(plan.SiteId, row.SiteId, StringComparison.Ordinal)
            || plan.DeliveryDate != row.DeliveryDate)
        {
            return await FailClaimAsync(transaction, "activation-payload-integrity-failed", cancellationToken).ConfigureAwait(false);
        }

        var windowPayloadHash = ActivationPayloadIntegrity.ComputeWindowHash(releaseWindow);

        var timing = ActivationWindowTimingPolicy.Evaluate(row.DeliveryDate, row.ReleaseWindowId!, now);
        if (!timing.CanStart)
        {
            return await FailClaimAsync(transaction, timing.BlockingCode!, cancellationToken).ConfigureAwait(false);
        }

        var claimExpires = now.Add(ActivationPrewriteClaim.DefaultDuration);
        if (claimExpires > timing.StartDeadlineUtc)
        {
            return await FailClaimAsync(transaction, "activation-window-claim-duration-insufficient", cancellationToken).ConfigureAwait(false);
        }
        var pilotRow = await connection.QuerySingleOrDefaultAsync<PilotRow>(new CommandDefinition(
            SelectPilotSql,
            new { PilotSessionId = row.ReleasePilotSessionId.Value },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (pilotRow is null
            || !string.Equals(pilotRow.Status, "Armed", StringComparison.Ordinal)
            || pilotRow.ExpiresAtUtc < claimExpires.UtcDateTime
            || pilotRow.OutboxItemId != row.OutboxItemId
            || !string.Equals(pilotRow.WindowId, row.ReleaseWindowId, StringComparison.Ordinal)
            || pilotRow.SafetyRevision != request.ExpectedSafetyRevision
            || pilotRow.FencingToken != request.ExpectedFencingToken
            || !string.Equals(pilotRow.WriterOwnerId, request.WriterOwnerId, StringComparison.Ordinal))
        {
            return await FailClaimAsync(transaction, "activation-pilot-window-insufficient", cancellationToken).ConfigureAwait(false);
        }

        var safetyRow = await connection.QuerySingleOrDefaultAsync<SafetyRow>(new CommandDefinition(
            SelectSafetySql,
            new { row.SiteId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var leaseRow = await connection.QuerySingleOrDefaultAsync<LeaseRow>(new CommandDefinition(
            SelectLeaseSql,
            new { row.SiteId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var safety = safetyRow is null ? null : SafetyFromRow(safetyRow);
        var lease = leaseRow is null ? null : LeaseFromRow(leaseRow);
        if (safety is not null && safety.Revision != request.ExpectedSafetyRevision)
        {
            return await FailClaimAsync(transaction, "activation-safety-revision-mismatch", cancellationToken).ConfigureAwait(false);
        }

        var gate = ActivationWriterSafetyGate.Evaluate(
            row.SiteId,
            request.WriterOwnerId,
            safety,
            lease,
            now);
        if (!gate.CanWrite)
        {
            return await FailClaimAsync(transaction, gate.BlockingCode!, cancellationToken).ConfigureAwait(false);
        }

        if (gate.FencingToken != request.ExpectedFencingToken)
        {
            return await FailClaimAsync(transaction, "activation-fencing-token-mismatch", cancellationToken).ConfigureAwait(false);
        }

        if (lease!.ExpiresAtUtc < claimExpires)
        {
            return await FailClaimAsync(transaction, "activation-writer-lease-window-insufficient", cancellationToken).ConfigureAwait(false);
        }

        var claim = new ActivationPrewriteClaim(
            request.ClaimId,
            row.OutboxItemId,
            row.ReleasePilotSessionId.Value,
            row.SiteId,
            row.ReleaseWindowId!,
            windowPayloadHash,
            request.ExecutorId,
            request.WriterOwnerId,
            request.ExpectedSafetyRevision,
            request.ExpectedFencingToken,
            row.PayloadHash,
            row.PayloadJson,
            now,
            claimExpires).EnsureValid();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            ClaimSql,
            new
            {
                claim.ClaimId,
                claim.OutboxItemId,
                claim.ExecutorId,
                ExpectedSafetyRevision = claim.SafetyRevision,
                ExpectedFencingToken = claim.FencingToken,
                PilotSessionId = claim.PilotSessionId,
                claim.WindowId,
                claim.WindowPayloadHash,
                ClaimedAtUtc = claim.ClaimedAtUtc,
                ExpiresAtUtc = claim.ExpiresAtUtc,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException("Activation pre-write claim lost its outbox row lock.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ActivationPrewriteClaimResult(claim, Claimed: true);
    }

    public async Task<ActivationPrewriteCompletionResult> CompleteAsync(
        ActivationPrewriteCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.EnsureValid();
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<OutboxRow>(new CommandDefinition(
            SelectOutboxSql,
            new { request.OutboxItemId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return await FailCompletionAsync(transaction, null, "activation-outbox-not-found", cancellationToken).ConfigureAwait(false);
        }

        var target = request.Succeeded ? ActivationOutboxStatus.Succeeded : ActivationOutboxStatus.Failed;
        var current = Enum.Parse<ActivationOutboxStatus>(row.Status);
        if (current is ActivationOutboxStatus.Succeeded or ActivationOutboxStatus.Failed)
        {
            var replay = current == target
                && row.ClaimId == request.ClaimId
                && string.Equals(row.ClaimExecutorId, request.ExecutorId, StringComparison.Ordinal)
                && string.Equals(row.ClaimOutcomeCode, request.OutcomeCode, StringComparison.Ordinal);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay
                ? new ActivationPrewriteCompletionResult(current, Completed: true)
                : new ActivationPrewriteCompletionResult(current, Completed: false, "activation-prewrite-completion-conflict");
        }

        if (current != ActivationOutboxStatus.Claimed
            || row.ClaimId != request.ClaimId
            || !string.Equals(row.ClaimExecutorId, request.ExecutorId, StringComparison.Ordinal))
        {
            return await FailCompletionAsync(
                transaction,
                current,
                "activation-prewrite-claim-mismatch",
                cancellationToken).ConfigureAwait(false);
        }

        if (row.ClaimedAtUtc is null || request.NowUtc.UtcDateTime < row.ClaimedAtUtc.Value)
        {
            return await FailCompletionAsync(
                transaction,
                current,
                "activation-prewrite-completion-before-claim",
                cancellationToken).ConfigureAwait(false);
        }

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            CompleteSql,
            new
            {
                Status = target.ToString(),
                request.OutcomeCode,
                CompletedAtUtc = request.NowUtc.ToUniversalTime(),
                request.OutboxItemId,
                request.ClaimId,
                request.ExecutorId,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException("Activation pre-write completion lost its outbox row lock.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ActivationPrewriteCompletionResult(target, Completed: true);
    }

    private static async Task<ActivationPrewriteClaimResult> FailClaimAsync(
        NpgsqlTransaction transaction,
        string code,
        CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ActivationPrewriteClaimResult(null, Claimed: false, code);
    }

    private static async Task<ActivationPrewriteCompletionResult> FailCompletionAsync(
        NpgsqlTransaction transaction,
        ActivationOutboxStatus? status,
        string code,
        CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ActivationPrewriteCompletionResult(status, Completed: false, code);
    }

    private static ActivationPrewriteClaim ToClaim(OutboxRow row) => new ActivationPrewriteClaim(
        row.ClaimId!.Value,
        row.OutboxItemId,
        row.ClaimPilotSessionId!.Value,
        row.SiteId,
        row.ClaimWindowId!,
        row.ClaimWindowPayloadHash!,
        row.ClaimExecutorId!,
        row.ReleaseWriterOwnerId!,
        row.ClaimSafetyRevision!.Value,
        row.ClaimFencingToken!.Value,
        row.PayloadHash,
        row.PayloadJson,
        TimestampConverter.ToOffset(row.ClaimedAtUtc!.Value),
        TimestampConverter.ToOffset(row.ClaimExpiresAtUtc!.Value)).EnsureValid();

    private static ActivationSafetyState SafetyFromRow(SafetyRow row) => new ActivationSafetyState(
        row.SiteId,
        row.KillSwitchEngaged,
        Enum.Parse<ActivationWriterAuthority>(row.WriterAuthority),
        row.LegacyWriterStoppedAtUtc is null ? null : TimestampConverter.ToOffset(row.LegacyWriterStoppedAtUtc.Value),
        row.LegacyStopEvidence,
        row.Revision,
        TimestampConverter.ToOffset(row.UpdatedAtUtc),
        row.UpdatedBy,
        row.Reason).EnsureValid();

    private static ActivationWriterLease LeaseFromRow(LeaseRow row) => new ActivationWriterLease(
        row.SiteId,
        row.OwnerId,
        row.FencingToken,
        TimestampConverter.ToOffset(row.AcquiredAtUtc),
        TimestampConverter.ToOffset(row.ExpiresAtUtc)).EnsureValid();

    private sealed class OutboxRow
    {
        public Guid OutboxItemId { get; init; }
        public string SiteId { get; init; } = string.Empty;
        public DateOnly DeliveryDate { get; init; }
        public string PayloadHash { get; init; } = string.Empty;
        public string PayloadJson { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? ReleaseWriterOwnerId { get; init; }
        public long? ReleaseSafetyRevision { get; init; }
        public long? ReleaseFencingToken { get; init; }
        public Guid? ReleasePilotSessionId { get; init; }
        public string? ReleaseWindowId { get; init; }
        public Guid? ClaimId { get; init; }
        public string? ClaimExecutorId { get; init; }
        public long? ClaimSafetyRevision { get; init; }
        public long? ClaimFencingToken { get; init; }
        public Guid? ClaimPilotSessionId { get; init; }
        public string? ClaimWindowId { get; init; }
        public string? ClaimWindowPayloadHash { get; init; }
        public DateTime? ClaimedAtUtc { get; init; }
        public DateTime? ClaimExpiresAtUtc { get; init; }
        public string? ClaimOutcomeCode { get; init; }
    }

    private sealed class PilotRow
    {
        public Guid OutboxItemId { get; init; }
        public string WindowId { get; init; } = string.Empty;
        public string WriterOwnerId { get; init; } = string.Empty;
        public long SafetyRevision { get; init; }
        public long FencingToken { get; init; }
        public string Status { get; init; } = string.Empty;
        public DateTime ExpiresAtUtc { get; init; }
    }

    private sealed class SafetyRow
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

    private sealed class LeaseRow
    {
        public string SiteId { get; init; } = string.Empty;
        public string OwnerId { get; init; } = string.Empty;
        public long FencingToken { get; init; }
        public DateTime AcquiredAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
    }
}
