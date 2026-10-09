using BatteryEms.Application.Orchestration;

namespace BatteryEms.Api.Contracts;

public sealed record ActivationOutboxReleaseRequestBody(
    Guid PilotSessionId,
    string WriterOwnerId,
    long ExpectedSafetyRevision,
    long ExpectedFencingToken,
    string Reason);

public sealed record ActivationRollbackRequestBody(
    Guid OperationId,
    long ExpectedSafetyRevision,
    string Reason);

public sealed record ActivationOutboxReleaseResponse(
    Guid ProposalId,
    Guid OutboxItemId,
    string SiteId,
    DateOnly DeliveryDate,
    ActivationOutboxStatus Status,
    long SafetyRevision,
    Guid PilotSessionId,
    string? WindowId,
    bool Released)
{
    public static ActivationOutboxReleaseResponse From(
        ActivationProposal proposal,
        ActivationOutboxItem outbox,
        bool released)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(outbox);
        return new ActivationOutboxReleaseResponse(
            proposal.ProposalId,
            outbox.OutboxItemId,
            proposal.SiteId,
            proposal.DeliveryDate,
            outbox.Status,
            outbox.ReleaseSafetyRevision ?? 0,
            outbox.ReleasePilotSessionId ?? Guid.Empty,
            outbox.ReleaseWindowId,
            released);
    }
}

public sealed record ActivationRollbackResponse(
    string SiteId,
    bool KillSwitchEngaged,
    ActivationWriterAuthority WriterAuthority,
    long SafetyRevision,
    int CancelledReadyItems,
    bool RolledBack)
{
    public static ActivationRollbackResponse From(
        ActivationSafetyState state,
        int cancelledReadyItems,
        bool rolledBack)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new ActivationRollbackResponse(
            state.SiteId,
            state.KillSwitchEngaged,
            state.WriterAuthority,
            state.Revision,
            cancelledReadyItems,
            rolledBack);
    }
}
