namespace BatteryEms.Application.Orchestration;

// Deye-specific authorization boundary. No TOU/window rules are added to the
// universal equipment optimizer or the EMS day-plan scheduler.
public sealed record DeyeDayAuthorization(
    Guid AuthorizationId, Guid ProposalId, string SiteId, DateOnly DeliveryDate,
    string PayloadHash, string WriterOwnerId, long SafetyRevision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, bool Revoked);

public sealed record DeyeDayAuthorizationRequest(
    Guid AuthorizationId, Guid ProposalId, string WriterOwnerId, long SafetyRevision,
    string Actor, string Reason, DateTimeOffset NowUtc)
{
    public void EnsureValid()
    {
        if (AuthorizationId == Guid.Empty || ProposalId == Guid.Empty || SafetyRevision < 1)
        { throw new ArgumentException("Invalid authorization identity or safety revision."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(WriterOwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(Reason);
    }
}

public sealed record DeyeDayAuthorizationResult(DeyeDayAuthorization? Authorization, bool Accepted, string? BlockingCode = null);

public sealed record DeyeDayWindowClaim(
    Guid ClaimId, Guid AuthorizationId, string SiteId, DateOnly DeliveryDate,
    string WindowId, string PayloadHash, string WindowPayloadHash, string WriterOwnerId,
    long SafetyRevision, long FencingToken, DateTimeOffset ClaimedAtUtc, DateTimeOffset ExpiresAtUtc);

public sealed record DeyeDayWindowClaimRequest(
    string SiteId, DateOnly DeliveryDate, string WindowId, string PayloadHash,
    string WriterOwnerId, long SafetyRevision, long FencingToken, DateTimeOffset NowUtc);

public sealed record DeyeDayWindowClaimResult(DeyeDayWindowClaim? Claim, bool Accepted, bool IsReplay = false, string? BlockingCode = null);

public interface IDeyeDayAuthorizationStore
{
    Task<DeyeDayAuthorizationResult> AuthorizeAsync(DeyeDayAuthorizationRequest request, CancellationToken cancellationToken);
    Task<DeyeDayAuthorization?> FindAsync(string siteId, DateOnly deliveryDate, CancellationToken cancellationToken);
    Task<bool> RevokeAsync(Guid authorizationId, string actor, string reason, DateTimeOffset now, CancellationToken cancellationToken);
    Task<DeyeDayWindowClaimResult> ClaimAsync(DeyeDayWindowClaimRequest request, CancellationToken cancellationToken);
}

public interface IDeyeDayWindowDispatcher
{
    Task<ActivationPlanDispatchResult> DispatchAsync(DeyeDayWindowClaim claim, CancellationToken cancellationToken);
}
