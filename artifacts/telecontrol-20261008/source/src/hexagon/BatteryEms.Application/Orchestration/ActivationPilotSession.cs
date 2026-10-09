using BatteryEms.Application.Persistence;
using BatteryEms.Application.Time;
using BatteryEms.Domain;

namespace BatteryEms.Application.Orchestration;

public enum ActivationPilotSessionStatus
{
    Armed,
    Aborted,
    Expired,
}

public sealed record ActivationPilotSession(
    Guid SessionId,
    Guid ProposalId,
    Guid OutboxItemId,
    string SiteId,
    string WindowId,
    string WriterOwnerId,
    long SafetyRevision,
    long FencingToken,
    ActivationPilotSessionStatus Status,
    string ArmedBy,
    string ArmReason,
    DateTimeOffset ArmedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? AbortedBy = null,
    string? AbortReason = null,
    DateTimeOffset? AbortedAtUtc = null)
{
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(15);

    public ActivationPilotSession EnsureValid()
    {
        if (SessionId == Guid.Empty || ProposalId == Guid.Empty || OutboxItemId == Guid.Empty)
        {
            throw new ArgumentException("Pilot session, proposal and outbox identifiers must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        if (!ActivationPayloadIntegrity.IsWindowIdValid(WindowId))
        {
            throw new ArgumentException("Pilot window must be one of Z1, Z2, Z3 or Z4.", nameof(WindowId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(WriterOwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ArmedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(ArmReason);
        if (SafetyRevision < 1 || FencingToken < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SafetyRevision));
        }

        var lifetime = ExpiresAtUtc - ArmedAtUtc;
        if (lifetime <= TimeSpan.Zero || lifetime > MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExpiresAtUtc),
                "Pilot session must be positive and no longer than 15 minutes.");
        }

        var hasAbortMetadata = AbortedBy is not null || AbortReason is not null || AbortedAtUtc is not null;
        var hasCompleteAbortMetadata = !string.IsNullOrWhiteSpace(AbortedBy)
            && !string.IsNullOrWhiteSpace(AbortReason)
            && AbortedAtUtc is not null;
        if (hasAbortMetadata != hasCompleteAbortMetadata
            || (Status == ActivationPilotSessionStatus.Aborted && !hasCompleteAbortMetadata)
            || (Status != ActivationPilotSessionStatus.Aborted && hasAbortMetadata))
        {
            throw new ArgumentException("Pilot abort metadata must exactly match Aborted status.");
        }

        return this;
    }

    public bool IsActiveAt(DateTimeOffset nowUtc) =>
        Status == ActivationPilotSessionStatus.Armed
        && ArmedAtUtc <= nowUtc.ToUniversalTime()
        && ExpiresAtUtc > nowUtc.ToUniversalTime();
}

public sealed record ActivationPilotArmRequest(
    Guid SessionId,
    Guid ProposalId,
    string WindowId,
    string WriterOwnerId,
    long ExpectedSafetyRevision,
    long ExpectedFencingToken,
    string ArmedBy,
    string Reason,
    DateTimeOffset NowUtc)
{
    public ActivationPilotArmRequest EnsureValid()
    {
        if (SessionId == Guid.Empty || ProposalId == Guid.Empty)
        {
            throw new ArgumentException("Pilot session and proposal identifiers must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(WriterOwnerId);
        if (!ActivationPayloadIntegrity.IsWindowIdValid(WindowId))
        {
            throw new ArgumentException("Pilot window must be one of Z1, Z2, Z3 or Z4.", nameof(WindowId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ArmedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(Reason);
        if (ExpectedSafetyRevision < 1 || ExpectedFencingToken < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpectedSafetyRevision));
        }

        return this;
    }
}

public sealed record ActivationPilotArmResult(
    ActivationPilotSession? Session,
    bool Armed,
    string? ErrorCode = null);

public sealed record ActivationPilotAbortRequest(
    Guid SessionId,
    string AbortedBy,
    string Reason,
    DateTimeOffset NowUtc)
{
    public ActivationPilotAbortRequest EnsureValid()
    {
        if (SessionId == Guid.Empty)
        {
            throw new ArgumentException("Pilot session identifier must not be empty.", nameof(SessionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(AbortedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(Reason);
        return this;
    }
}

public sealed record ActivationPilotAbortResult(
    ActivationPilotSession? Session,
    bool Aborted,
    string? ErrorCode = null);

public interface IActivationPilotSessionStore
{
    Task<ActivationPilotArmResult> ArmAsync(
        ActivationPilotArmRequest request,
        CancellationToken cancellationToken);

    Task<ActivationPilotAbortResult> AbortAsync(
        ActivationPilotAbortRequest request,
        CancellationToken cancellationToken);

    Task<ActivationPilotSession?> FindAsync(
        Guid sessionId,
        CancellationToken cancellationToken);
}

public interface IActivationPilotSessionUseCase
{
    Task<ActivationPilotArmResult> ArmAsync(
        Guid sessionId,
        Guid proposalId,
        string windowId,
        string writerOwnerId,
        long expectedSafetyRevision,
        long expectedFencingToken,
        string armedBy,
        string reason,
        CancellationToken cancellationToken);

    Task<ActivationPilotAbortResult> AbortAsync(
        Guid sessionId,
        string abortedBy,
        string reason,
        CancellationToken cancellationToken);

    Task<ActivationPilotSession?> FindAsync(
        Guid sessionId,
        CancellationToken cancellationToken);
}

public sealed class FailClosedActivationPilotSessionStore : IActivationPilotSessionStore
{
    public Task<ActivationPilotArmResult> ArmAsync(
        ActivationPilotArmRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request.EnsureValid();
        return Task.FromResult(new ActivationPilotArmResult(
            null,
            Armed: false,
            "activation-durable-persistence-required"));
    }

    public Task<ActivationPilotAbortResult> AbortAsync(
        ActivationPilotAbortRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request.EnsureValid();
        return Task.FromResult(new ActivationPilotAbortResult(
            null,
            Aborted: false,
            "activation-durable-persistence-required"));
    }

    public Task<ActivationPilotSession?> FindAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ActivationPilotSession?>(null);
    }
}

public sealed class DefaultActivationPilotSessionUseCase : IActivationPilotSessionUseCase
{
    private readonly IActivationPilotSessionStore _store;
    private readonly IOperatorAuditLog _audit;
    private readonly IClock _clock;

    public DefaultActivationPilotSessionUseCase(
        IActivationPilotSessionStore store,
        IOperatorAuditLog audit,
        IClock clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ActivationPilotArmResult> ArmAsync(
        Guid sessionId,
        Guid proposalId,
        string windowId,
        string writerOwnerId,
        long expectedSafetyRevision,
        long expectedFencingToken,
        string armedBy,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow.ToUniversalTime();
        var result = await _store.ArmAsync(
            new ActivationPilotArmRequest(
                sessionId,
                proposalId,
                windowId,
                writerOwnerId,
                expectedSafetyRevision,
                expectedFencingToken,
                armedBy,
                reason,
                now).EnsureValid(),
            cancellationToken).ConfigureAwait(false);
        await AuditAsync(
            now,
            armedBy,
            "activation-pilot-arm",
            result.Session?.SiteId,
            reason,
            result.Armed ? "accepted-armed" : result.ErrorCode ?? "rejected",
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ActivationPilotAbortResult> AbortAsync(
        Guid sessionId,
        string abortedBy,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow.ToUniversalTime();
        var result = await _store.AbortAsync(
            new ActivationPilotAbortRequest(sessionId, abortedBy, reason, now).EnsureValid(),
            cancellationToken).ConfigureAwait(false);
        await AuditAsync(
            now,
            abortedBy,
            "activation-pilot-abort",
            result.Session?.SiteId,
            reason,
            result.Aborted ? "accepted-aborted" : result.ErrorCode ?? "rejected",
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<ActivationPilotSession?> FindAsync(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        _store.FindAsync(sessionId, cancellationToken);

    private Task AuditAsync(
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
