namespace BatteryEms.Application.Orchestration;

public enum OrchestrationRunStatus
{
    Queued,
    Running,
    Succeeded,
    Partial,
    Blocked,
    Failed,
    Cancelled,
    AwaitingApproval,
}

public enum OrchestrationStepStatus
{
    Queued,
    Running,
    Succeeded,
    Partial,
    Retrying,
    Skipped,
    Blocked,
    Failed,
}

public enum OrchestrationTriggerType
{
    Scheduled,
    PollInterval,
    SourceEvent,
    Manual,
    Retry,
    Approval,
}

public enum DataRole
{
    Critical,
    BlockingSafety,
    Advisory,
    Informational,
    Optional,
}

public enum DataBalanceState
{
    Unknown,
    Ready,
    Partial,
    Missing,
    Stale,
    SourceError,
    Invalid,
    Retrying,
    Blocked,
    Degraded,
}

public sealed record OrchestrationRun(
    Guid RunId,
    string SiteId,
    string RunType,
    DateTimeOffset? HorizonStart,
    DateTimeOffset? HorizonEnd,
    OrchestrationTriggerType TriggerType,
    string? TriggerRef,
    string IdempotencyKey,
    OrchestrationRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? InputHash,
    string? OutputRef,
    string? ErrorCode,
    string? ErrorMessage,
    string MetadataJson)
{
    public OrchestrationRun EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(RunType);
        ArgumentException.ThrowIfNullOrWhiteSpace(IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(MetadataJson);
        if (HorizonStart is not null
            && HorizonEnd is not null
            && HorizonStart >= HorizonEnd)
        {
            throw new ArgumentException("Run horizon start must be before horizon end.", nameof(HorizonStart));
        }

        return this;
    }
}

public sealed record OrchestrationStep(
    Guid StepId,
    Guid RunId,
    string SiteId,
    string Module,
    int StepOrder,
    string IdempotencyKey,
    OrchestrationStepStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    string? InputHash,
    string? OutputRef,
    DataRole? DataRole,
    DataBalanceState? DataStatus,
    string? ErrorCode,
    string? ErrorMessage,
    string MetadataJson)
{
    public OrchestrationStep EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Module);
        ArgumentException.ThrowIfNullOrWhiteSpace(IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(MetadataJson);
        if (StepOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(StepOrder), StepOrder, "Step order must not be negative.");
        }

        if (AttemptCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AttemptCount), AttemptCount, "Attempt count must not be negative.");
        }

        return this;
    }
}

public sealed record DataBalanceStatus(
    string SiteId,
    string DataGroup,
    string Source,
    string InstrumentId,
    DataRole Role,
    DataBalanceState Status,
    DateTimeOffset? FreshnessDeadlineUtc,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    int AttemptCount,
    string? LastErrorCode,
    string? LastErrorMessage,
    string? QualitySummary,
    string MetadataJson)
{
    public DataBalanceStatus EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(DataGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(InstrumentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(MetadataJson);
        if (AttemptCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AttemptCount), AttemptCount, "Attempt count must not be negative.");
        }

        return this;
    }
}

public sealed record OrchestrationLock(
    string LockKey,
    string OwnerId,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt,
    string MetadataJson)
{
    public OrchestrationLock EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(LockKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(OwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(MetadataJson);
        if (AcquiredAt >= ExpiresAt)
        {
            throw new ArgumentException("Lock expiration must be after acquisition time.", nameof(ExpiresAt));
        }

        return this;
    }
}
