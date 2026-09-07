using BatteryEms.Application.Time;

namespace BatteryEms.Application.Orchestration;

public sealed class DefaultOrchestrationUseCase : IOrchestrationUseCase
{
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(10);

    private readonly IOrchestrationRunStore _runs;
    private readonly IOrchestrationLockStore _locks;
    private readonly IDataBalanceStore _dataBalances;
    private readonly IDataReadinessPolicy _readinessPolicy;
    private readonly IClock _clock;
    private readonly IReadOnlyList<IOrchestrationModule> _modules;

    public DefaultOrchestrationUseCase(
        IOrchestrationRunStore runs,
        IOrchestrationLockStore locks,
        IDataBalanceStore dataBalances,
        IDataReadinessPolicy readinessPolicy,
        IClock clock,
        IEnumerable<IOrchestrationModule>? modules = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(dataBalances);
        ArgumentNullException.ThrowIfNull(readinessPolicy);
        ArgumentNullException.ThrowIfNull(clock);

        _runs = runs;
        _locks = locks;
        _dataBalances = dataBalances;
        _readinessPolicy = readinessPolicy;
        _clock = clock;
        _modules = (modules ?? []).OrderBy(module => module.StepOrder).ToArray();
    }

    public async Task<OrchestrationRun> StartAsync(
        OrchestrationStartCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        command = command.EnsureValid();

        var now = _clock.UtcNow.ToUniversalTime();
        var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
            ? BuildRunIdempotencyKey(command)
            : command.IdempotencyKey!;
        var ownerId = Guid.NewGuid().ToString("N");
        var lockKey = $"orchestration:{idempotencyKey}";
        var acquired = await _locks.TryAcquireAsync(
            new OrchestrationLock(
                lockKey,
                ownerId,
                now,
                now.Add(LockTtl),
                "{}"),
            now,
            cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            return await ReturnDuplicateOrBlockedRunAsync(
                command,
                idempotencyKey,
                now,
                "lock-held",
                "Orchestration lock is already held.",
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var start = await _runs.TryStartAsync(
                new OrchestrationRun(
                    Guid.NewGuid(),
                    command.SiteId,
                    command.RunType,
                    command.HorizonStart?.ToUniversalTime(),
                    command.HorizonEnd?.ToUniversalTime(),
                    command.TriggerType,
                    command.TriggerRef,
                    idempotencyKey,
                    OrchestrationRunStatus.Running,
                    now,
                    CompletedAt: null,
                    command.InputHash,
                    OutputRef: null,
                    ErrorCode: null,
                    ErrorMessage: null,
                    MetadataJson: "{}"),
                cancellationToken).ConfigureAwait(false);
            if (!start.IsNewlyCreated)
            {
                return start.Run;
            }

            var balances = await _dataBalances.QueryAsync(
                command.SiteId,
                cancellationToken).ConfigureAwait(false);
            var readiness = _readinessPolicy.Evaluate(balances, now);
            if (!readiness.CanContinue)
            {
                await _runs.CompleteAsync(
                    start.Run.RunId,
                    new OrchestrationRunCompletion(
                        OrchestrationRunStatus.Blocked,
                        now,
                        ErrorCode: readiness.BlockingCode,
                        ErrorMessage: "Critical or safety data is not ready.",
                        MetadataJson: BuildReadinessMetadata(readiness)),
                    cancellationToken).ConfigureAwait(false);
                return (await _runs.QueryCurrentAsync(command.SiteId, cancellationToken).ConfigureAwait(false))
                    .Single(run => run.RunId == start.Run.RunId);
            }

            var finalStatus = await ExecuteModulesAsync(
                start.Run,
                now,
                idempotencyKey,
                readiness,
                cancellationToken).ConfigureAwait(false);

            await _runs.CompleteAsync(
                start.Run.RunId,
                new OrchestrationRunCompletion(
                    finalStatus,
                    _clock.UtcNow.ToUniversalTime(),
                    MetadataJson: BuildReadinessMetadata(readiness)),
                cancellationToken).ConfigureAwait(false);

            return (await _runs.QueryCurrentAsync(command.SiteId, cancellationToken).ConfigureAwait(false))
                .Single(run => run.RunId == start.Run.RunId);
        }
        finally
        {
            await _locks.ReleaseAsync(lockKey, ownerId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<OrchestrationRunStatus> ExecuteModulesAsync(
        OrchestrationRun run,
        DateTimeOffset startedAt,
        string runIdempotencyKey,
        DataReadinessResult readiness,
        CancellationToken cancellationToken)
    {
        var finalStatus = readiness.IsDegraded
            ? OrchestrationRunStatus.Partial
            : OrchestrationRunStatus.Succeeded;

        foreach (var module in _modules)
        {
            var moduleStartedAt = _clock.UtcNow.ToUniversalTime();
            var context = new OrchestrationModuleContext(
                run.RunId,
                run.SiteId,
                run.RunType,
                run.HorizonStart,
                run.HorizonEnd,
                $"{runIdempotencyKey}:{module.ModuleName}",
                startedAt);
            var result = await module.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            await _runs.AppendStepAsync(
                new OrchestrationStep(
                    Guid.NewGuid(),
                    run.RunId,
                    run.SiteId,
                    module.ModuleName,
                    module.StepOrder,
                    context.IdempotencyKey,
                    result.Status,
                    moduleStartedAt,
                    _clock.UtcNow.ToUniversalTime(),
                    AttemptCount: 1,
                    NextAttemptAt: null,
                    InputHash: run.InputHash,
                    result.OutputRef,
                    result.DataRole,
                    result.DataStatus,
                    result.ErrorCode,
                    result.ErrorMessage,
                    result.MetadataJson),
                cancellationToken).ConfigureAwait(false);
            finalStatus = Merge(finalStatus, result.Status);
        }

        return finalStatus;
    }

    private async Task<OrchestrationRun> ReturnDuplicateOrBlockedRunAsync(
        OrchestrationStartCommand command,
        string idempotencyKey,
        DateTimeOffset now,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var start = await _runs.TryStartAsync(
            new OrchestrationRun(
                Guid.NewGuid(),
                command.SiteId,
                command.RunType,
                command.HorizonStart?.ToUniversalTime(),
                command.HorizonEnd?.ToUniversalTime(),
                command.TriggerType,
                command.TriggerRef,
                idempotencyKey,
                OrchestrationRunStatus.Blocked,
                now,
                now,
                command.InputHash,
                OutputRef: null,
                errorCode,
                errorMessage,
                MetadataJson: "{}"),
            cancellationToken).ConfigureAwait(false);
        return start.Run;
    }

    private static OrchestrationRunStatus Merge(
        OrchestrationRunStatus current,
        OrchestrationStepStatus stepStatus) =>
        stepStatus switch
        {
            OrchestrationStepStatus.Failed => OrchestrationRunStatus.Failed,
            OrchestrationStepStatus.Blocked => OrchestrationRunStatus.Blocked,
            OrchestrationStepStatus.Partial or OrchestrationStepStatus.Retrying
                when current == OrchestrationRunStatus.Succeeded => OrchestrationRunStatus.Partial,
            _ => current,
        };

    private static string BuildRunIdempotencyKey(OrchestrationStartCommand command)
    {
        var horizon = command.HorizonStart is null
            ? "no-horizon"
            : command.HorizonStart.Value.ToUniversalTime().ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
        return $"run:{command.SiteId}:{command.RunType}:{horizon}";
    }

    private static string BuildReadinessMetadata(DataReadinessResult readiness) =>
        readiness.IsDegraded
            ? $$"""{"degraded":true,"blocking_count":{{readiness.BlockingBalances.Count}},"degraded_count":{{readiness.DegradedBalances.Count}}}"""
            : $$"""{"degraded":false,"blocking_count":{{readiness.BlockingBalances.Count}},"degraded_count":{{readiness.DegradedBalances.Count}}}""";
}
