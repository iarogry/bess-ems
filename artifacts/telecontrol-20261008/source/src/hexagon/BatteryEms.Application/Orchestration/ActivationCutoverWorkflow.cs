using BatteryEms.Application.Persistence;
using BatteryEms.Application.Time;
using BatteryEms.Domain;

namespace BatteryEms.Application.Orchestration;

public sealed record ActivationOutboxReleaseRequest(
    Guid ProposalId,
    Guid PilotSessionId,
    string WriterOwnerId,
    long ExpectedSafetyRevision,
    long ExpectedFencingToken,
    string ReleasedBy,
    string Reason,
    DateTimeOffset NowUtc)
{
    public ActivationOutboxReleaseRequest EnsureValid()
    {
        if (ProposalId == Guid.Empty || PilotSessionId == Guid.Empty)
        {
            throw new ArgumentException("Proposal and pilot session identifiers must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(WriterOwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ReleasedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(Reason);
        if (ExpectedSafetyRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpectedSafetyRevision));
        }

        if (ExpectedFencingToken < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpectedFencingToken));
        }

        return this;
    }
}

public sealed record ActivationOutboxReleaseResult(
    ActivationProposal? Proposal,
    ActivationOutboxItem? OutboxItem,
    bool Released,
    string? ErrorCode = null);

public sealed record ActivationRollbackRequest(
    Guid OperationId,
    string SiteId,
    long ExpectedSafetyRevision,
    string RolledBackBy,
    string Reason,
    DateTimeOffset NowUtc)
{
    public ActivationRollbackRequest EnsureValid()
    {
        if (OperationId == Guid.Empty)
        {
            throw new ArgumentException("Rollback operation id must not be empty.", nameof(OperationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(RolledBackBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(Reason);
        if (ExpectedSafetyRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpectedSafetyRevision));
        }

        return this;
    }
}

public sealed record ActivationRollbackResult(
    ActivationSafetyState? SafetyState,
    int CancelledReadyItems,
    bool RolledBack,
    string? ErrorCode = null);

public interface IActivationCutoverStore
{
    Task<ActivationOutboxReleaseResult> ReleaseHeldAsync(
        ActivationOutboxReleaseRequest request,
        CancellationToken cancellationToken);

    Task<ActivationRollbackResult> RollbackAsync(
        ActivationRollbackRequest request,
        CancellationToken cancellationToken);
}

public interface IActivationCutoverUseCase
{
    Task<ActivationOutboxReleaseResult> ReleaseAsync(
        Guid proposalId,
        Guid pilotSessionId,
        string writerOwnerId,
        long expectedSafetyRevision,
        long expectedFencingToken,
        string releasedBy,
        string reason,
        CancellationToken cancellationToken);

    Task<ActivationRollbackResult> RollbackAsync(
        Guid operationId,
        string siteId,
        long expectedSafetyRevision,
        string rolledBackBy,
        string reason,
        CancellationToken cancellationToken);
}

public sealed class FailClosedActivationCutoverStore : IActivationCutoverStore
{
    public Task<ActivationOutboxReleaseResult> ReleaseHeldAsync(
        ActivationOutboxReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request.EnsureValid();
        return Task.FromResult(new ActivationOutboxReleaseResult(
            null,
            null,
            Released: false,
            "activation-durable-persistence-required"));
    }

    public Task<ActivationRollbackResult> RollbackAsync(
        ActivationRollbackRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request.EnsureValid();
        return Task.FromResult(new ActivationRollbackResult(
            null,
            CancelledReadyItems: 0,
            RolledBack: false,
            "activation-durable-persistence-required"));
    }
}

public sealed class DefaultActivationCutoverUseCase : IActivationCutoverUseCase
{
    private readonly IActivationCutoverStore _store;
    private readonly IOperatorAuditLog _audit;
    private readonly IClock _clock;

    public DefaultActivationCutoverUseCase(
        IActivationCutoverStore store,
        IOperatorAuditLog audit,
        IClock clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ActivationOutboxReleaseResult> ReleaseAsync(
        Guid proposalId,
        Guid pilotSessionId,
        string writerOwnerId,
        long expectedSafetyRevision,
        long expectedFencingToken,
        string releasedBy,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow.ToUniversalTime();
        var request = new ActivationOutboxReleaseRequest(
            proposalId,
            pilotSessionId,
            writerOwnerId,
            expectedSafetyRevision,
            expectedFencingToken,
            releasedBy,
            reason,
            now).EnsureValid();
        var result = await _store.ReleaseHeldAsync(request, cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(
            now,
            releasedBy,
            "activation-outbox-release",
            result.Proposal?.SiteId,
            reason,
            result.Released ? "accepted-ready" : result.ErrorCode ?? "rejected",
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ActivationRollbackResult> RollbackAsync(
        Guid operationId,
        string siteId,
        long expectedSafetyRevision,
        string rolledBackBy,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow.ToUniversalTime();
        var request = new ActivationRollbackRequest(
            operationId,
            siteId,
            expectedSafetyRevision,
            rolledBackBy,
            reason,
            now).EnsureValid();
        var result = await _store.RollbackAsync(request, cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(
            now,
            rolledBackBy,
            "activation-writer-rollback",
            siteId,
            reason,
            result.RolledBack ? $"accepted-cancelled-{result.CancelledReadyItems}" : result.ErrorCode ?? "rejected",
            cancellationToken).ConfigureAwait(false);
        return result;
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
