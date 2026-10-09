using BatteryEms.Application.Orchestration;

namespace BatteryEms.Api.Contracts;

public sealed record ActivationProposalRequest(string Reason);

public sealed record ActivationProposalResponse(
    Guid ProposalId,
    Guid ComparisonId,
    Guid RunId,
    string SiteId,
    DateOnly DeliveryDate,
    string PayloadHash,
    ActivationProposalStatus Status,
    string ProposedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAtUtc,
    Guid? OutboxItemId,
    ActivationOutboxStatus? OutboxStatus)
{
    public static ActivationProposalResponse From(
        ActivationProposal proposal,
        ActivationOutboxItem? outbox = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return new ActivationProposalResponse(
            proposal.ProposalId,
            proposal.ComparisonId,
            proposal.RunId,
            proposal.SiteId,
            proposal.DeliveryDate,
            proposal.PayloadHash,
            proposal.Status,
            proposal.ProposedBy,
            proposal.CreatedAtUtc,
            proposal.ExpiresAtUtc,
            proposal.ReviewedBy,
            proposal.ReviewedAtUtc,
            proposal.OutboxItemId,
            outbox?.Status
            ?? (proposal.OutboxItemId is null ? null : ActivationOutboxStatus.Held));
    }
}
