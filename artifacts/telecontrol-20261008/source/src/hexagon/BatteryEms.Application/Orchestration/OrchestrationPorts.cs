namespace BatteryEms.Application.Orchestration;

public sealed record OrchestrationRunStartResult(
    OrchestrationRun Run,
    bool IsNewlyCreated);

public sealed record OrchestrationRunCompletion(
    OrchestrationRunStatus Status,
    DateTimeOffset CompletedAt,
    string? OutputRef = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? MetadataJson = null);

public interface IOrchestrationRunStore
{
    Task<OrchestrationRunStartResult> TryStartAsync(
        OrchestrationRun run,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        Guid runId,
        OrchestrationRunCompletion completion,
        CancellationToken cancellationToken);

    Task AppendStepAsync(
        OrchestrationStep orchestrationStep,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OrchestrationRun>> QueryCurrentAsync(
        string siteId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OrchestrationStep>> QueryStepsAsync(
        Guid runId,
        CancellationToken cancellationToken);
}

public interface IOrchestrationLockStore
{
    Task<bool> TryAcquireAsync(
        OrchestrationLock orchestrationLock,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task ReleaseAsync(
        string lockKey,
        string ownerId,
        CancellationToken cancellationToken);
}

public interface IDataBalanceStore
{
    Task UpsertAsync(
        DataBalanceStatus status,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DataBalanceStatus>> QueryAsync(
        string siteId,
        CancellationToken cancellationToken);
}

public sealed record DataReadinessResult(
    bool CanContinue,
    bool IsDegraded,
    string? BlockingCode,
    IReadOnlyList<DataBalanceStatus> BlockingBalances,
    IReadOnlyList<DataBalanceStatus> DegradedBalances);

public interface IDataReadinessPolicy
{
    DataReadinessResult Evaluate(
        IReadOnlyList<DataBalanceStatus> balances,
        DateTimeOffset now);
}

public sealed record OrchestrationModuleContext(
    Guid RunId,
    string SiteId,
    string RunType,
    DateTimeOffset? HorizonStart,
    DateTimeOffset? HorizonEnd,
    string IdempotencyKey,
    DateTimeOffset StartedAt,
    string? TriggerRef = null);

public sealed record OrchestrationModuleResult(
    OrchestrationStepStatus Status,
    DataRole? DataRole = null,
    DataBalanceState? DataStatus = null,
    string? OutputRef = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string MetadataJson = "{}");

public interface IOrchestrationModule
{
    string ModuleName { get; }

    int StepOrder { get; }

    Task<OrchestrationModuleResult> ExecuteAsync(
        OrchestrationModuleContext context,
        CancellationToken cancellationToken);
}

public sealed record OrchestrationStartCommand(
    string SiteId,
    string RunType,
    DateTimeOffset? HorizonStart,
    DateTimeOffset? HorizonEnd,
    OrchestrationTriggerType TriggerType,
    string? TriggerRef = null,
    string? InputHash = null,
    string? IdempotencyKey = null)
{
    public OrchestrationStartCommand EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(RunType);
        if (HorizonStart is not null
            && HorizonEnd is not null
            && HorizonStart >= HorizonEnd)
        {
            throw new ArgumentException("Command horizon start must be before horizon end.", nameof(HorizonStart));
        }

        return this;
    }
}

public interface IOrchestrationUseCase
{
    Task<OrchestrationRun> StartAsync(
        OrchestrationStartCommand command,
        CancellationToken cancellationToken);
}
