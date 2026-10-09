namespace BatteryEms.Application.Orchestration;

public enum DeviceWriteBrokerAttemptState
{
    Prepared,
    Initiated,
    Unknown,
    Verified,
    NotSent,
}

public enum DeviceWriteBrokerObservation
{
    Verified,
    NotSent,
    Unknown,
}

// Internal broker control-plane request, NOT an agent tool or HTTP contract.
// The server must supply authenticated identities and its own clock.
public sealed record DeviceWriteBrokerBeginRequest(
    Guid AttemptId,
    string SiteId,
    DateOnly DeliveryDate,
    string WindowId,
    string PayloadHash,
    ActivationWriterAuthority Authority,
    string WriterOwnerId,
    long SafetyRevision,
    long FencingToken,
    Guid? ActivationClaimId,
    DateTimeOffset NowUtc)
{
    public DeviceWriteBrokerBeginRequest EnsureValid()
    {
        if (AttemptId == Guid.Empty) { throw new ArgumentException("Attempt identifier is required."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(WriterOwnerId);
        if (DeliveryDate == DateOnly.MinValue || DeliveryDate == DateOnly.MaxValue
            || !ActivationPayloadIntegrity.IsWindowIdValid(WindowId))
        {
            throw new ArgumentException("A supported delivery date and explicit window are required.");
        }
        if (PayloadHash is null || PayloadHash.Length != 64
            || PayloadHash.Any(character => !((character >= '0' && character <= '9') || (character >= 'A' && character <= 'F'))))
        {
            throw new ArgumentException("Canonical uppercase SHA-256 is required.", nameof(PayloadHash));
        }
        if (SafetyRevision < 1 || FencingToken < 1) { throw new ArgumentOutOfRangeException(nameof(SafetyRevision)); }
        var validAuthority = Authority switch
        {
            ActivationWriterAuthority.LegacyRunner => ActivationClaimId is null,
            ActivationWriterAuthority.ProductAgent => ActivationClaimId is not null && ActivationClaimId != Guid.Empty,
            _ => false,
        };
        if (!validAuthority) { throw new ArgumentException("Writer authority and activation claim do not match."); }
        return this;
    }
}

public sealed record DeviceWriteBrokerAttemptResult(
    bool Accepted,
    bool IsReplay = false,
    DeviceWriteBrokerAttemptState? State = null,
    string? BlockingCode = null);

public interface IDeviceWriteBrokerAttemptStore
{
    Task<DeviceWriteBrokerAttemptResult> BeginAsync(DeviceWriteBrokerBeginRequest request, CancellationToken cancellationToken);

    // Persist Initiated before touching the mutation transport. Replays MUST NOT send.
    Task<DeviceWriteBrokerAttemptResult> MarkInitiatedAsync(Guid attemptId, string writerOwnerId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    // Unknown stays latched. NotSent is permitted only before Initiated.
    // Verified must come from trusted broker readback, never an agent assertion.
    Task<DeviceWriteBrokerAttemptResult> ObserveAsync(Guid attemptId, string writerOwnerId, DeviceWriteBrokerObservation observation, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}
