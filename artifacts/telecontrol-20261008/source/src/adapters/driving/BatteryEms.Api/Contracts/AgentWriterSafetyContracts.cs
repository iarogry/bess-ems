using BatteryEms.Application.Orchestration;

namespace BatteryEms.Api.Contracts;

public sealed record AgentWriterSafetyResponse(
    string SiteId,
    bool Configured,
    bool KillSwitchEngaged,
    ActivationWriterAuthority WriterAuthority,
    bool LegacyWriterStopProven,
    long? SafetyRevision,
    bool LeasePresent,
    bool LeaseActive,
    DateTimeOffset? LeaseExpiresAtUtc)
{
    public static AgentWriterSafetyResponse From(
        string siteId,
        ActivationSafetyState? state,
        ActivationWriterLease? lease,
        DateTimeOffset nowUtc) => new(
            siteId,
            Configured: state is not null,
            KillSwitchEngaged: state?.KillSwitchEngaged ?? true,
            WriterAuthority: state?.WriterAuthority ?? ActivationWriterAuthority.None,
            LegacyWriterStopProven: state?.LegacyWriterStoppedAtUtc is not null
                && !string.IsNullOrWhiteSpace(state.LegacyStopEvidence),
            SafetyRevision: state?.Revision,
            LeasePresent: lease is not null,
            LeaseActive: lease?.ExpiresAtUtc > nowUtc.ToUniversalTime(),
            LeaseExpiresAtUtc: lease?.ExpiresAtUtc);
}
