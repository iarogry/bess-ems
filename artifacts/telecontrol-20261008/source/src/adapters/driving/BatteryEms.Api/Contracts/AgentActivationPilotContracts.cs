using BatteryEms.Application.Orchestration;

namespace BatteryEms.Api.Contracts;

public sealed record ActivationPilotArmRequestBody(
    Guid SessionId,
    string WindowId,
    string WriterOwnerId,
    long ExpectedSafetyRevision,
    long ExpectedFencingToken,
    string Reason);

public sealed record ActivationPilotAbortRequestBody(string Reason);

public sealed record ActivationPilotSessionResponse(
    Guid SessionId,
    Guid ProposalId,
    Guid OutboxItemId,
    string SiteId,
    string WindowId,
    long SafetyRevision,
    ActivationPilotSessionStatus Status,
    DateTimeOffset ArmedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? AbortedAtUtc)
{
    public static ActivationPilotSessionResponse From(ActivationPilotSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new(
            session.SessionId,
            session.ProposalId,
            session.OutboxItemId,
            session.SiteId,
            session.WindowId,
            session.SafetyRevision,
            session.Status,
            session.ArmedAtUtc,
            session.ExpiresAtUtc,
            session.AbortedAtUtc);
    }
}
