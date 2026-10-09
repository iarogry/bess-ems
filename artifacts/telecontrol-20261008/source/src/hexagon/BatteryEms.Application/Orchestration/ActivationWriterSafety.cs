namespace BatteryEms.Application.Orchestration;

public enum ActivationWriterAuthority
{
    None,
    LegacyRunner,
    ProductAgent,
}

public sealed record ActivationSafetyState(
    string SiteId,
    bool KillSwitchEngaged,
    ActivationWriterAuthority WriterAuthority,
    DateTimeOffset? LegacyWriterStoppedAtUtc,
    string? LegacyStopEvidence,
    long Revision,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy,
    string Reason)
{
    public ActivationSafetyState EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(UpdatedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(Reason);
        if (Revision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Revision),
                Revision,
                "Safety-state revision must be positive.");
        }

        if (WriterAuthority == ActivationWriterAuthority.ProductAgent
            && (LegacyWriterStoppedAtUtc is null
                || string.IsNullOrWhiteSpace(LegacyStopEvidence)))
        {
            throw new ArgumentException(
                "Product writer authority requires explicit legacy-writer stop evidence.",
                nameof(LegacyStopEvidence));
        }

        return this;
    }

    public static ActivationSafetyState InitialFailClosed(
        string siteId,
        DateTimeOffset nowUtc,
        string actor,
        string reason) => new ActivationSafetyState(
            siteId,
            KillSwitchEngaged: true,
            ActivationWriterAuthority.LegacyRunner,
            LegacyWriterStoppedAtUtc: null,
            LegacyStopEvidence: null,
            Revision: 1,
            nowUtc.ToUniversalTime(),
            actor,
            reason).EnsureValid();
}

public sealed record ActivationWriterLease(
    string SiteId,
    string OwnerId,
    long FencingToken,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public ActivationWriterLease EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(OwnerId);
        if (FencingToken < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FencingToken),
                FencingToken,
                "Writer fencing token must be positive.");
        }

        if (AcquiredAtUtc >= ExpiresAtUtc)
        {
            throw new ArgumentException(
                "Writer lease expiration must be after acquisition.",
                nameof(ExpiresAtUtc));
        }

        return this;
    }
}

public sealed record ActivationLeaseAcquireResult(
    ActivationWriterLease? Lease,
    bool Acquired,
    string? ErrorCode = null);

public interface IActivationWriterSafetyStore
{
    Task<ActivationSafetyState?> FindStateAsync(
        string siteId,
        CancellationToken cancellationToken);

    Task<bool> CompareExchangeStateAsync(
        ActivationSafetyState nextState,
        long? expectedRevision,
        CancellationToken cancellationToken);

    Task<ActivationLeaseAcquireResult> TryAcquireLeaseAsync(
        string siteId,
        string ownerId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<ActivationWriterLease?> FindLeaseAsync(
        string siteId,
        CancellationToken cancellationToken);
}

public sealed record ActivationWriterGateResult(
    bool CanWrite,
    string? BlockingCode = null,
    long? FencingToken = null,
    long? SafetyRevision = null);

public static class ActivationWriterSafetyGate
{
    public static ActivationWriterGateResult Evaluate(
        string siteId,
        string ownerId,
        ActivationSafetyState? safetyState,
        ActivationWriterLease? lease,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (safetyState is null
            || !string.Equals(safetyState.SiteId, siteId, StringComparison.Ordinal))
        {
            return Blocked("activation-safety-state-missing");
        }

        safetyState = safetyState.EnsureValid();
        if (safetyState.KillSwitchEngaged)
        {
            return Blocked("activation-kill-switch-engaged", safetyState.Revision);
        }

        if (safetyState.WriterAuthority != ActivationWriterAuthority.ProductAgent)
        {
            return Blocked("activation-product-writer-not-authorized", safetyState.Revision);
        }

        if (safetyState.LegacyWriterStoppedAtUtc is null
            || string.IsNullOrWhiteSpace(safetyState.LegacyStopEvidence))
        {
            return Blocked("activation-legacy-writer-stop-unproven", safetyState.Revision);
        }

        if (lease is null
            || !string.Equals(lease.SiteId, siteId, StringComparison.Ordinal))
        {
            return Blocked("activation-writer-lease-missing", safetyState.Revision);
        }

        lease = lease.EnsureValid();
        if (!string.Equals(lease.OwnerId, ownerId, StringComparison.Ordinal))
        {
            return Blocked("activation-writer-lease-owned-by-other", safetyState.Revision);
        }

        if (lease.ExpiresAtUtc <= nowUtc.ToUniversalTime())
        {
            return Blocked("activation-writer-lease-expired", safetyState.Revision);
        }

        return new ActivationWriterGateResult(
            CanWrite: true,
            FencingToken: lease.FencingToken,
            SafetyRevision: safetyState.Revision);
    }

    private static ActivationWriterGateResult Blocked(
        string code,
        long? revision = null) => new(
            CanWrite: false,
            BlockingCode: code,
            SafetyRevision: revision);
}

public sealed class InMemoryActivationWriterSafetyStore : IActivationWriterSafetyStore
{
    public static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromMinutes(2);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ActivationSafetyState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActivationWriterLease> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastFencingTokens = new(StringComparer.Ordinal);

    public Task<ActivationSafetyState?> FindStateAsync(
        string siteId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _states.TryGetValue(siteId, out var state);
            return Task.FromResult(state);
        }
    }

    public Task<bool> CompareExchangeStateAsync(
        ActivationSafetyState nextState,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nextState);
        cancellationToken.ThrowIfCancellationRequested();
        nextState = nextState.EnsureValid();
        lock (_gate)
        {
            if (!_states.TryGetValue(nextState.SiteId, out var current))
            {
                if (expectedRevision is not null || nextState.Revision != 1)
                {
                    return Task.FromResult(false);
                }

                _states.Add(nextState.SiteId, nextState);
                return Task.FromResult(true);
            }

            if (current.Revision != expectedRevision
                || nextState.Revision != current.Revision + 1)
            {
                return Task.FromResult(false);
            }

            _states[nextState.SiteId] = nextState;
            return Task.FromResult(true);
        }
    }

    public Task<ActivationLeaseAcquireResult> TryAcquireLeaseAsync(
        string siteId,
        string ownerId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        cancellationToken.ThrowIfCancellationRequested();
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                leaseDuration,
                $"Writer lease must be positive and at most {MaximumLeaseDuration}.");
        }

        nowUtc = nowUtc.ToUniversalTime();
        lock (_gate)
        {
            if (_leases.TryGetValue(siteId, out var existing)
                && existing.ExpiresAtUtc > nowUtc
                && !string.Equals(existing.OwnerId, ownerId, StringComparison.Ordinal))
            {
                return Task.FromResult(new ActivationLeaseAcquireResult(
                    existing,
                    Acquired: false,
                    "activation-writer-lease-owned-by-other"));
            }

            var isRenewal = existing is not null
                && existing.ExpiresAtUtc > nowUtc
                && string.Equals(existing.OwnerId, ownerId, StringComparison.Ordinal);
            var fencingToken = isRenewal
                ? existing!.FencingToken
                : NextFencingToken(siteId);
            var lease = new ActivationWriterLease(
                siteId,
                ownerId,
                fencingToken,
                nowUtc,
                nowUtc.Add(leaseDuration)).EnsureValid();
            _leases[siteId] = lease;
            return Task.FromResult(new ActivationLeaseAcquireResult(lease, Acquired: true));
        }
    }

    public Task<ActivationWriterLease?> FindLeaseAsync(
        string siteId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _leases.TryGetValue(siteId, out var lease);
            return Task.FromResult(lease);
        }
    }

    private long NextFencingToken(string siteId)
    {
        _lastFencingTokens.TryGetValue(siteId, out var lastToken);
        var next = checked(lastToken + 1);
        _lastFencingTokens[siteId] = next;
        return next;
    }
}
