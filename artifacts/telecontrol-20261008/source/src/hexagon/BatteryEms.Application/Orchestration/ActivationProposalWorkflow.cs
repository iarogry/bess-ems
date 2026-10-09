using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatteryEms.Application.Persistence;
using BatteryEms.Application.Time;
using BatteryEms.Domain;

namespace BatteryEms.Application.Orchestration;

public enum ActivationProposalStatus
{
    Pending,
    Approved,
    Rejected,
    Expired,
    Cancelled,
}

public enum ActivationOutboxStatus
{
    Held,
    Ready,
    Claimed,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record ActivationProposal(
    Guid ProposalId,
    Guid ComparisonId,
    Guid RunId,
    string SiteId,
    DateOnly DeliveryDate,
    string PayloadHash,
    string PayloadJson,
    string ProposedBy,
    string ProposalReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ActivationProposalStatus Status,
    string? ReviewedBy = null,
    string? ReviewReason = null,
    DateTimeOffset? ReviewedAtUtc = null,
    Guid? OutboxItemId = null)
{
    public ActivationProposal EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(PayloadHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(PayloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProposedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProposalReason);
        if (ExpiresAtUtc <= CreatedAtUtc)
        {
            throw new ArgumentException("Proposal expiration must be after creation.", nameof(ExpiresAtUtc));
        }

        return this;
    }
}

public sealed record ActivationOutboxItem(
    Guid OutboxItemId,
    Guid ProposalId,
    string SiteId,
    DateOnly DeliveryDate,
    string PayloadHash,
    string PayloadJson,
    string IdempotencyKey,
    ActivationOutboxStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc = null,
    string? ReleaseWriterOwnerId = null,
    long? ReleaseSafetyRevision = null,
    long? ReleaseFencingToken = null,
    Guid? ReleasePilotSessionId = null,
    string? ReleaseWindowId = null,
    string? ReleasedBy = null,
    string? ReleaseReason = null,
    DateTimeOffset? ReleasedAtUtc = null)
{
    public ActivationOutboxItem EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(PayloadHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(PayloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(IdempotencyKey);
        var hasAnyReleaseMetadata = ReleaseWriterOwnerId is not null
            || ReleaseSafetyRevision is not null
            || ReleaseFencingToken is not null
            || ReleasePilotSessionId is not null
            || ReleaseWindowId is not null
            || ReleasedBy is not null
            || ReleaseReason is not null
            || ReleasedAtUtc is not null;
        var hasAllReleaseMetadata = !string.IsNullOrWhiteSpace(ReleaseWriterOwnerId)
            && ReleaseSafetyRevision is > 0
            && ReleaseFencingToken is > 0
            && ReleasePilotSessionId is not null
            && ActivationPayloadIntegrity.IsWindowIdValid(ReleaseWindowId)
            && !string.IsNullOrWhiteSpace(ReleasedBy)
            && !string.IsNullOrWhiteSpace(ReleaseReason)
            && ReleasedAtUtc is not null;
        if (hasAnyReleaseMetadata && !hasAllReleaseMetadata)
        {
            throw new ArgumentException("Outbox release metadata must be complete.");
        }

        if (Status is ActivationOutboxStatus.Ready
            or ActivationOutboxStatus.Claimed
            or ActivationOutboxStatus.Succeeded
            or ActivationOutboxStatus.Failed
            && !hasAllReleaseMetadata)
        {
            throw new ArgumentException("A dispatchable outbox item requires release metadata.");
        }

        return this;
    }
}

public sealed record ActivationProposalCreateResult(
    ActivationProposal? Proposal,
    bool Created,
    string? ErrorCode = null);

public sealed record ActivationProposalApprovalResult(
    ActivationProposal? Proposal,
    ActivationOutboxItem? OutboxItem,
    bool Approved,
    string? ErrorCode = null);

public interface IActivationProposalStore
{
    Task<ActivationProposalCreateResult> CreateAsync(
        ActivationProposal proposal,
        CancellationToken cancellationToken);

    Task<ActivationProposal?> FindAsync(
        Guid proposalId,
        CancellationToken cancellationToken);

    Task<ActivationProposalApprovalResult> ApproveAndHoldAsync(
        Guid proposalId,
        string approvedBy,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
}

public interface IActivationProposalUseCase
{
    Task<ActivationProposalCreateResult> ProposeAsync(
        string siteId,
        DateOnly deliveryDate,
        string proposedBy,
        string reason,
        CancellationToken cancellationToken);

    Task<ActivationProposalApprovalResult> ApproveAsync(
        Guid proposalId,
        string approvedBy,
        string reason,
        CancellationToken cancellationToken);

    Task<ActivationProposal?> FindAsync(
        Guid proposalId,
        CancellationToken cancellationToken);
}

public sealed class InMemoryActivationProposalStore : IActivationProposalStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, ActivationProposal> _proposals = [];
    private readonly Dictionary<string, Guid> _proposalIdsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ActivationOutboxItem> _outbox = [];

    public Task<ActivationProposalCreateResult> CreateAsync(
        ActivationProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(proposal);
        proposal = proposal.EnsureValid();
        var key = ProposalKey(proposal);
        lock (_gate)
        {
            if (_proposalIdsByKey.TryGetValue(key, out var existingId))
            {
                return Task.FromResult(new ActivationProposalCreateResult(
                    _proposals[existingId],
                    Created: false));
            }

            _proposals.Add(proposal.ProposalId, proposal);
            _proposalIdsByKey.Add(key, proposal.ProposalId);
            return Task.FromResult(new ActivationProposalCreateResult(proposal, Created: true));
        }
    }

    public Task<ActivationProposal?> FindAsync(
        Guid proposalId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _proposals.TryGetValue(proposalId, out var proposal);
            return Task.FromResult(proposal);
        }
    }

    public Task<ActivationProposalApprovalResult> ApproveAndHoldAsync(
        Guid proposalId,
        string approvedBy,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (!_proposals.TryGetValue(proposalId, out var proposal))
            {
                return Task.FromResult(new ActivationProposalApprovalResult(
                    null,
                    null,
                    Approved: false,
                    "activation-proposal-not-found"));
            }

            if (proposal.Status == ActivationProposalStatus.Approved
                && proposal.OutboxItemId is { } existingOutboxId)
            {
                return Task.FromResult(new ActivationProposalApprovalResult(
                    proposal,
                    _outbox[existingOutboxId],
                    Approved: true));
            }

            if (proposal.Status != ActivationProposalStatus.Pending)
            {
                return Task.FromResult(new ActivationProposalApprovalResult(
                    proposal,
                    null,
                    Approved: false,
                    "activation-proposal-not-pending"));
            }

            if (nowUtc >= proposal.ExpiresAtUtc)
            {
                proposal = proposal with { Status = ActivationProposalStatus.Expired };
                _proposals[proposalId] = proposal;
                return Task.FromResult(new ActivationProposalApprovalResult(
                    proposal,
                    null,
                    Approved: false,
                    "activation-proposal-expired"));
            }

            if (string.Equals(proposal.ProposedBy, approvedBy, StringComparison.Ordinal))
            {
                return Task.FromResult(new ActivationProposalApprovalResult(
                    proposal,
                    null,
                    Approved: false,
                    "activation-four-eyes-required"));
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
                nowUtc).EnsureValid();
            proposal = proposal with
            {
                Status = ActivationProposalStatus.Approved,
                ReviewedBy = approvedBy,
                ReviewReason = reason,
                ReviewedAtUtc = nowUtc,
                OutboxItemId = outbox.OutboxItemId,
            };
            _outbox.Add(outbox.OutboxItemId, outbox);
            _proposals[proposalId] = proposal;
            return Task.FromResult(new ActivationProposalApprovalResult(
                proposal,
                outbox,
                Approved: true));
        }
    }

    private static string ProposalKey(ActivationProposal proposal) =>
        $"{proposal.ComparisonId:D}:{proposal.PayloadHash}";
}

public sealed class DefaultActivationProposalUseCase : IActivationProposalUseCase
{
    private static readonly TimeSpan DefaultProposalLifetime = TimeSpan.FromMinutes(30);
    private readonly IShadowPlanComparisonStore _comparisons;
    private readonly IActivationProposalStore _proposals;
    private readonly IOperatorAuditLog _audit;
    private readonly IClock _clock;

    public DefaultActivationProposalUseCase(
        IShadowPlanComparisonStore comparisons,
        IActivationProposalStore proposals,
        IOperatorAuditLog audit,
        IClock clock)
    {
        _comparisons = comparisons ?? throw new ArgumentNullException(nameof(comparisons));
        _proposals = proposals ?? throw new ArgumentNullException(nameof(proposals));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ActivationProposalCreateResult> ProposeAsync(
        string siteId,
        DateOnly deliveryDate,
        string proposedBy,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var comparison = await _comparisons.FindLatestComparisonAsync(
            siteId,
            deliveryDate,
            cancellationToken).ConfigureAwait(false);
        if (comparison is null
            || !comparison.IsEquivalent
            || !comparison.LegacyPayloadReady
            || !comparison.ShadowPayloadReady
            || comparison.Mismatches.Count != 0)
        {
            return await AuditCreateFailureAsync(
                siteId,
                proposedBy,
                reason,
                "activation-comparison-not-eligible",
                cancellationToken).ConfigureAwait(false);
        }

        var snapshot = await _comparisons.FindSnapshotAsync(
            ShadowPlanSide.Shadow,
            siteId,
            deliveryDate,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null || !snapshot.PayloadReady)
        {
            return await AuditCreateFailureAsync(
                siteId,
                proposedBy,
                reason,
                "activation-shadow-payload-not-ready",
                cancellationToken).ConfigureAwait(false);
        }

        var payloadJson = JsonSerializer.Serialize(snapshot.EnsureValid());
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        var now = _clock.UtcNow.ToUniversalTime();
        var result = await _proposals.CreateAsync(
            new ActivationProposal(
                Guid.NewGuid(),
                comparison.ComparisonId,
                comparison.RunId,
                siteId,
                deliveryDate,
                payloadHash,
                payloadJson,
                proposedBy,
                reason,
                now,
                now.Add(DefaultProposalLifetime),
                ActivationProposalStatus.Pending),
            cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(
            now,
            proposedBy,
            "activation-proposal-create",
            siteId,
            reason,
            result.Created ? "accepted" : "idempotent-replay",
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ActivationProposalApprovalResult> ApproveAsync(
        Guid proposalId,
        string approvedBy,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var now = _clock.UtcNow.ToUniversalTime();
        var result = await _proposals.ApproveAndHoldAsync(
            proposalId,
            approvedBy,
            reason,
            now,
            cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(
            now,
            approvedBy,
            "activation-proposal-approve",
            result.Proposal?.SiteId,
            reason,
            result.Approved ? "accepted-held" : result.ErrorCode ?? "rejected",
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<ActivationProposal?> FindAsync(
        Guid proposalId,
        CancellationToken cancellationToken) =>
        _proposals.FindAsync(proposalId, cancellationToken);

    private async Task<ActivationProposalCreateResult> AuditCreateFailureAsync(
        string siteId,
        string proposedBy,
        string reason,
        string errorCode,
        CancellationToken cancellationToken)
    {
        await AppendAuditAsync(
            _clock.UtcNow.ToUniversalTime(),
            proposedBy,
            "activation-proposal-create",
            siteId,
            reason,
            errorCode,
            cancellationToken).ConfigureAwait(false);
        return new ActivationProposalCreateResult(null, Created: false, errorCode);
    }

    private Task AppendAuditAsync(
        DateTimeOffset timestamp,
        string actor,
        string action,
        string? siteId,
        string reason,
        string outcome,
        CancellationToken cancellationToken) =>
        _audit.AppendAsync(
            new AuditEvent(timestamp, actor, action, siteId, reason, outcome),
            cancellationToken);
}
