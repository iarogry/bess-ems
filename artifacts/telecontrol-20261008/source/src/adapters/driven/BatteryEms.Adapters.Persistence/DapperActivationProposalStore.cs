using System.Data;
using BatteryEms.Application.Orchestration;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

public sealed class DapperActivationProposalStore : IActivationProposalStore
{
    private const string InsertProposalSql = """
        INSERT INTO activation_proposals (
            proposal_id, comparison_id, run_id, site_id, delivery_date,
            payload_hash, payload_json, proposed_by, proposal_reason,
            created_at_utc, expires_at_utc, status)
        VALUES (
            @ProposalId, @ComparisonId, @RunId, @SiteId, @DeliveryDate,
            @PayloadHash, CAST(@PayloadJson AS jsonb), @ProposedBy, @ProposalReason,
            @CreatedAtUtc, @ExpiresAtUtc, @Status)
        ON CONFLICT (comparison_id, payload_hash) DO NOTHING
        RETURNING *, payload_json::text AS PayloadJson;
        """;

    private const string SelectProposalByKeySql = """
        SELECT *, payload_json::text AS PayloadJson
        FROM activation_proposals
        WHERE comparison_id = @ComparisonId AND payload_hash = @PayloadHash;
        """;

    private const string SelectProposalSql = """
        SELECT *, payload_json::text AS PayloadJson
        FROM activation_proposals
        WHERE proposal_id = @ProposalId;
        """;

    private const string SelectProposalForUpdateSql = """
        SELECT *, payload_json::text AS PayloadJson
        FROM activation_proposals
        WHERE proposal_id = @ProposalId
        FOR UPDATE;
        """;

    private const string MarkExpiredSql = """
        UPDATE activation_proposals
        SET status = 'Expired'
        WHERE proposal_id = @ProposalId;
        """;

    private const string InsertOutboxSql = """
        INSERT INTO activation_outbox (
            outbox_item_id, proposal_id, site_id, delivery_date,
            payload_hash, payload_json, idempotency_key, status,
            created_at_utc, updated_at_utc)
        VALUES (
            @OutboxItemId, @ProposalId, @SiteId, @DeliveryDate,
            @PayloadHash, CAST(@PayloadJson AS jsonb), @IdempotencyKey, @Status,
            @CreatedAtUtc, @UpdatedAtUtc);
        """;

    private const string ApproveProposalSql = """
        UPDATE activation_proposals
        SET status = 'Approved',
            reviewed_by = @ReviewedBy,
            review_reason = @ReviewReason,
            reviewed_at_utc = @ReviewedAtUtc,
            outbox_item_id = @OutboxItemId
        WHERE proposal_id = @ProposalId;
        """;

    private const string SelectOutboxSql = """
        SELECT *, payload_json::text AS PayloadJson
        FROM activation_outbox
        WHERE outbox_item_id = @OutboxItemId;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public DapperActivationProposalStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        DapperConfig.EnsureConfigured();
    }

    public async Task<ActivationProposalCreateResult> CreateAsync(
        ActivationProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        proposal = proposal.EnsureValid();
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var parameters = ToParameters(proposal);
            var inserted = await connection.QuerySingleOrDefaultAsync<ProposalRow>(new CommandDefinition(
                InsertProposalSql,
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (inserted is not null)
            {
                return new ActivationProposalCreateResult(FromRow(inserted), Created: true);
            }

            var existing = await connection.QuerySingleAsync<ProposalRow>(new CommandDefinition(
                SelectProposalByKeySql,
                new { proposal.ComparisonId, proposal.PayloadHash },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return new ActivationProposalCreateResult(FromRow(existing), Created: false);
        }
    }

    public async Task<ActivationProposal?> FindAsync(
        Guid proposalId,
        CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var row = await connection.QuerySingleOrDefaultAsync<ProposalRow>(new CommandDefinition(
                SelectProposalSql,
                new { ProposalId = proposalId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return row is null ? null : FromRow(row);
        }
    }

    public async Task<ActivationProposalApprovalResult> ApproveAndHoldAsync(
        Guid proposalId,
        string approvedBy,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
            var row = await connection.QuerySingleOrDefaultAsync<ProposalRow>(new CommandDefinition(
                SelectProposalForUpdateSql,
                new { ProposalId = proposalId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (row is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ActivationProposalApprovalResult(
                    null, null, Approved: false, "activation-proposal-not-found");
            }

            var proposal = FromRow(row);
            if (proposal.Status == ActivationProposalStatus.Approved
                && proposal.OutboxItemId is { } existingOutboxId)
            {
                var existingOutbox = await LoadOutboxAsync(
                    connection,
                    transaction,
                    existingOutboxId,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ActivationProposalApprovalResult(
                    proposal, existingOutbox, Approved: true);
            }

            if (proposal.Status != ActivationProposalStatus.Pending)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ActivationProposalApprovalResult(
                    proposal, null, Approved: false, "activation-proposal-not-pending");
            }

            if (nowUtc >= proposal.ExpiresAtUtc)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    MarkExpiredSql,
                    new { ProposalId = proposalId },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                proposal = proposal with { Status = ActivationProposalStatus.Expired };
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ActivationProposalApprovalResult(
                    proposal, null, Approved: false, "activation-proposal-expired");
            }

            if (string.Equals(proposal.ProposedBy, approvedBy, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ActivationProposalApprovalResult(
                    proposal, null, Approved: false, "activation-four-eyes-required");
            }

            var outbox = new ActivationOutboxItem(
                Guid.NewGuid(),
                proposal.ProposalId,
                proposal.SiteId,
                proposal.DeliveryDate,
                proposal.PayloadHash,
                proposal.PayloadJson,
                $"activation:{proposal.ProposalId:D}:{proposal.PayloadHash}",
                ActivationOutboxStatus.Held,
                nowUtc.ToUniversalTime()).EnsureValid();
            await connection.ExecuteAsync(new CommandDefinition(
                InsertOutboxSql,
                ToParameters(outbox),
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                ApproveProposalSql,
                new
                {
                    ProposalId = proposalId,
                    ReviewedBy = approvedBy,
                    ReviewReason = reason,
                    ReviewedAtUtc = nowUtc.ToUniversalTime(),
                    outbox.OutboxItemId,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            proposal = proposal with
            {
                Status = ActivationProposalStatus.Approved,
                ReviewedBy = approvedBy,
                ReviewReason = reason,
                ReviewedAtUtc = nowUtc.ToUniversalTime(),
                OutboxItemId = outbox.OutboxItemId,
            };
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ActivationProposalApprovalResult(proposal, outbox, Approved: true);
            }
        }
    }

    private static async Task<ActivationOutboxItem> LoadOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid outboxItemId,
        CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleAsync<OutboxRow>(new CommandDefinition(
            SelectOutboxSql,
            new { OutboxItemId = outboxItemId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return FromRow(row);
    }

    private static object ToParameters(ActivationProposal proposal) => new
    {
        proposal.ProposalId,
        proposal.ComparisonId,
        proposal.RunId,
        proposal.SiteId,
        proposal.DeliveryDate,
        proposal.PayloadHash,
        proposal.PayloadJson,
        proposal.ProposedBy,
        proposal.ProposalReason,
        CreatedAtUtc = proposal.CreatedAtUtc.ToUniversalTime(),
        ExpiresAtUtc = proposal.ExpiresAtUtc.ToUniversalTime(),
        Status = proposal.Status.ToString(),
    };

    private static object ToParameters(ActivationOutboxItem outbox) => new
    {
        outbox.OutboxItemId,
        outbox.ProposalId,
        outbox.SiteId,
        outbox.DeliveryDate,
        outbox.PayloadHash,
        outbox.PayloadJson,
        outbox.IdempotencyKey,
        Status = outbox.Status.ToString(),
        CreatedAtUtc = outbox.CreatedAtUtc.ToUniversalTime(),
        UpdatedAtUtc = outbox.UpdatedAtUtc?.ToUniversalTime(),
    };

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
}
