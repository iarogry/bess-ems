using System.Data;
using BatteryEms.Application.Orchestration;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

public sealed class DapperActivationPilotSessionStore : IActivationPilotSessionStore
{
    private const string SelectProposalSql = """
        SELECT proposal_id AS ProposalId,
               outbox_item_id AS OutboxItemId,
               site_id AS SiteId,
               proposed_by AS ProposedBy,
               reviewed_by AS ReviewedBy,
               status AS Status
        FROM activation_proposals
        WHERE proposal_id = @ProposalId
        FOR UPDATE;
        """;

    private const string SelectOutboxSql = """
        SELECT outbox_item_id AS OutboxItemId,
               status AS Status,
               payload_hash AS PayloadHash,
               payload_json::text AS PayloadJson
        FROM activation_outbox
        WHERE outbox_item_id = @OutboxItemId
        FOR UPDATE;
        """;

    private const string SelectSafetySql = """
        SELECT site_id AS SiteId,
               kill_switch_engaged AS KillSwitchEngaged,
               writer_authority AS WriterAuthority,
               legacy_writer_stopped_at_utc AS LegacyWriterStoppedAtUtc,
               legacy_stop_evidence AS LegacyStopEvidence,
               revision AS Revision,
               updated_at_utc AS UpdatedAtUtc,
               updated_by AS UpdatedBy,
               reason AS Reason
        FROM activation_writer_safety
        WHERE site_id = @SiteId
        FOR UPDATE;
        """;

    private const string SelectLeaseSql = """
        SELECT site_id AS SiteId,
               owner_id AS OwnerId,
               fencing_token AS FencingToken,
               acquired_at_utc AS AcquiredAtUtc,
               expires_at_utc AS ExpiresAtUtc
        FROM activation_writer_leases
        WHERE site_id = @SiteId
        FOR UPDATE;
        """;

    private const string ExpireStaleSql = """
        UPDATE activation_pilot_sessions
        SET status = 'Expired'
        WHERE status = 'Armed'
          AND expires_at_utc <= @NowUtc;
        """;

    private const string ExpireSessionSql = """
        UPDATE activation_pilot_sessions
        SET status = 'Expired'
        WHERE session_id = @SessionId
          AND status = 'Armed'
          AND expires_at_utc <= @NowUtc;
        """;

    private const string SelectSessionSql = """
        SELECT *
        FROM activation_pilot_sessions
        WHERE session_id = @SessionId;
        """;

    private const string SelectSessionForUpdateSql = """
        SELECT *
        FROM activation_pilot_sessions
        WHERE session_id = @SessionId
        FOR UPDATE;
        """;

    private const string InsertSessionSql = """
        INSERT INTO activation_pilot_sessions (
            session_id, proposal_id, outbox_item_id, site_id,
            window_id,
            writer_owner_id, safety_revision, fencing_token, status,
            armed_by, arm_reason, armed_at_utc, expires_at_utc)
        VALUES (
            @SessionId, @ProposalId, @OutboxItemId, @SiteId,
            @WindowId,
            @WriterOwnerId, @SafetyRevision, @FencingToken, 'Armed',
            @ArmedBy, @ArmReason, @ArmedAtUtc, @ExpiresAtUtc)
        ON CONFLICT DO NOTHING;
        """;

    private const string AbortSql = """
        UPDATE activation_pilot_sessions
        SET status = 'Aborted',
            aborted_by = @AbortedBy,
            abort_reason = @Reason,
            aborted_at_utc = @NowUtc
        WHERE session_id = @SessionId
          AND status = 'Armed';
        """;

    private readonly NpgsqlDataSource _dataSource;

    public DapperActivationPilotSessionStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        DapperConfig.EnsureConfigured();
        _dataSource = dataSource;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Maintainability",
        "CA1506",
        Justification = "The serializable arming transaction intentionally validates proposal, outbox, safety and lease rows as one fail-closed unit.")]
    public async Task<ActivationPilotArmResult> ArmAsync(
        ActivationPilotArmRequest request,
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
        var now = request.NowUtc.ToUniversalTime();
        await connection.ExecuteAsync(new CommandDefinition(
            ExpireStaleSql,
            new { NowUtc = now },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var existing = await connection.QuerySingleOrDefaultAsync<PilotSessionRow>(new CommandDefinition(
            SelectSessionForUpdateSql,
            new { request.SessionId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (existing is not null)
        {
            var existingSession = FromRow(existing);
            var replay = existingSession.Status == ActivationPilotSessionStatus.Armed
                && existingSession.ProposalId == request.ProposalId
                && string.Equals(existingSession.WindowId, request.WindowId, StringComparison.Ordinal)
                && string.Equals(existingSession.WriterOwnerId, request.WriterOwnerId, StringComparison.Ordinal)
                && existingSession.SafetyRevision == request.ExpectedSafetyRevision
                && existingSession.FencingToken == request.ExpectedFencingToken
                && string.Equals(existingSession.ArmedBy, request.ArmedBy, StringComparison.Ordinal)
                && existingSession.IsActiveAt(now);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay
                ? new ActivationPilotArmResult(existingSession, Armed: true)
                : new ActivationPilotArmResult(existingSession, Armed: false, "activation-pilot-session-conflict");
        }

        var proposal = await connection.QuerySingleOrDefaultAsync<ProposalRow>(new CommandDefinition(
            SelectProposalSql,
            new { request.ProposalId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (proposal is null)
        {
            return await FailArmAsync(transaction, "activation-proposal-not-found", cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(proposal.Status, "Approved", StringComparison.Ordinal)
            || proposal.OutboxItemId is null)
        {
            return await FailArmAsync(transaction, "activation-proposal-not-approved", cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(proposal.ProposedBy, request.ArmedBy, StringComparison.Ordinal)
            || string.Equals(proposal.ReviewedBy, request.ArmedBy, StringComparison.Ordinal))
        {
            return await FailArmAsync(transaction, "activation-pilot-four-eyes-required", cancellationToken).ConfigureAwait(false);
        }

        var outbox = await connection.QuerySingleOrDefaultAsync<OutboxRow>(new CommandDefinition(
            SelectOutboxSql,
            new { OutboxItemId = proposal.OutboxItemId.Value },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (outbox is null || !string.Equals(outbox.Status, "Held", StringComparison.Ordinal))
        {
            return await FailArmAsync(transaction, "activation-outbox-not-held", cancellationToken).ConfigureAwait(false);
        }

        if (!ActivationPayloadIntegrity.TryReadWindow(
                outbox.PayloadJson,
                outbox.PayloadHash,
                request.WindowId,
                out _,
                out _))
        {
            return await FailArmAsync(
                transaction,
                "activation-pilot-window-not-in-approved-payload",
                cancellationToken).ConfigureAwait(false);
        }

        var safetyRow = await connection.QuerySingleOrDefaultAsync<SafetyRow>(new CommandDefinition(
            SelectSafetySql,
            new { proposal.SiteId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var leaseRow = await connection.QuerySingleOrDefaultAsync<LeaseRow>(new CommandDefinition(
            SelectLeaseSql,
            new { proposal.SiteId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var safety = safetyRow is null ? null : SafetyFromRow(safetyRow);
        var lease = leaseRow is null ? null : LeaseFromRow(leaseRow);
        if (safety is not null && safety.Revision != request.ExpectedSafetyRevision)
        {
            return await FailArmAsync(transaction, "activation-safety-revision-mismatch", cancellationToken).ConfigureAwait(false);
        }

        var gate = ActivationWriterSafetyGate.Evaluate(
            proposal.SiteId,
            request.WriterOwnerId,
            safety,
            lease,
            now);
        if (!gate.CanWrite)
        {
            return await FailArmAsync(transaction, gate.BlockingCode!, cancellationToken).ConfigureAwait(false);
        }

        if (gate.FencingToken != request.ExpectedFencingToken)
        {
            return await FailArmAsync(transaction, "activation-fencing-token-mismatch", cancellationToken).ConfigureAwait(false);
        }

        var session = new ActivationPilotSession(
            request.SessionId,
            proposal.ProposalId,
            proposal.OutboxItemId.Value,
            proposal.SiteId,
            request.WindowId,
            request.WriterOwnerId,
            request.ExpectedSafetyRevision,
            request.ExpectedFencingToken,
            ActivationPilotSessionStatus.Armed,
            request.ArmedBy,
            request.Reason,
            now,
            now.Add(ActivationPilotSession.MaximumLifetime)).EnsureValid();
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            InsertSessionSql,
            session,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (inserted != 1)
        {
            return await FailArmAsync(
                transaction,
                "activation-pilot-session-already-active",
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ActivationPilotArmResult(session, Armed: true);
    }

    public async Task<ActivationPilotAbortResult> AbortAsync(
        ActivationPilotAbortRequest request,
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
        await connection.ExecuteAsync(new CommandDefinition(
            ExpireSessionSql,
            new { request.SessionId, NowUtc = request.NowUtc.ToUniversalTime() },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<PilotSessionRow>(new CommandDefinition(
            SelectSessionForUpdateSql,
            new { request.SessionId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ActivationPilotAbortResult(null, Aborted: false, "activation-pilot-session-not-found");
        }

        var current = FromRow(row);
        if (current.Status == ActivationPilotSessionStatus.Aborted)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ActivationPilotAbortResult(current, Aborted: true);
        }

        if (current.Status != ActivationPilotSessionStatus.Armed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ActivationPilotAbortResult(current, Aborted: false, "activation-pilot-session-not-armed");
        }

        var now = request.NowUtc.ToUniversalTime();
        await connection.ExecuteAsync(new CommandDefinition(
            AbortSql,
            new { request.SessionId, request.AbortedBy, request.Reason, NowUtc = now },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var aborted = current with
        {
            Status = ActivationPilotSessionStatus.Aborted,
            AbortedBy = request.AbortedBy,
            AbortReason = request.Reason,
            AbortedAtUtc = now,
        };
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ActivationPilotAbortResult(aborted.EnsureValid(), Aborted: true);
    }

    public async Task<ActivationPilotSession?> FindAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Pilot session identifier must not be empty.", nameof(sessionId));
        }

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            ExpireSessionSql,
            new { SessionId = sessionId, NowUtc = DateTimeOffset.UtcNow },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<PilotSessionRow>(new CommandDefinition(
            SelectSessionSql,
            new { SessionId = sessionId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : FromRow(row);
    }

    private static async Task<ActivationPilotArmResult> FailArmAsync(
        NpgsqlTransaction transaction,
        string errorCode,
        CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ActivationPilotArmResult(null, Armed: false, errorCode);
    }

    private static ActivationPilotSession FromRow(PilotSessionRow row) => new ActivationPilotSession(
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

    private sealed class ProposalRow
    {
        public Guid ProposalId { get; init; }
        public Guid? OutboxItemId { get; init; }
        public string SiteId { get; init; } = string.Empty;
        public string ProposedBy { get; init; } = string.Empty;
        public string? ReviewedBy { get; init; }
        public string Status { get; init; } = string.Empty;
    }

    private sealed class OutboxRow
    {
        public Guid OutboxItemId { get; init; }
        public string Status { get; init; } = string.Empty;
        public string PayloadHash { get; init; } = string.Empty;
        public string PayloadJson { get; init; } = string.Empty;
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
