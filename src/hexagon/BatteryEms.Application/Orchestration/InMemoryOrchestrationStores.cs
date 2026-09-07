using System.Collections.Concurrent;

namespace BatteryEms.Application.Orchestration;

public sealed class InMemoryOrchestrationRunStore : IOrchestrationRunStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Guid> _idempotencyIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, OrchestrationRun> _runs = [];
    private readonly Dictionary<Guid, List<OrchestrationStep>> _stepsByRun = [];

    public Task<OrchestrationRunStartResult> TryStartAsync(
        OrchestrationRun run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();
        run = run.EnsureValid();

        lock (_gate)
        {
            if (_idempotencyIndex.TryGetValue(run.IdempotencyKey, out var existingRunId))
            {
                return Task.FromResult(new OrchestrationRunStartResult(_runs[existingRunId], false));
            }

            _idempotencyIndex[run.IdempotencyKey] = run.RunId;
            _runs[run.RunId] = run;
            _stepsByRun[run.RunId] = [];
            return Task.FromResult(new OrchestrationRunStartResult(run, true));
        }
    }

    public Task CompleteAsync(
        Guid runId,
        OrchestrationRunCompletion completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run))
            {
                throw new InvalidOperationException($"Orchestration run '{runId}' was not found.");
            }

            _runs[runId] = run with
            {
                Status = completion.Status,
                CompletedAt = completion.CompletedAt,
                OutputRef = completion.OutputRef ?? run.OutputRef,
                ErrorCode = completion.ErrorCode,
                ErrorMessage = completion.ErrorMessage,
                MetadataJson = completion.MetadataJson ?? run.MetadataJson,
            };
        }

        return Task.CompletedTask;
    }

    public Task AppendStepAsync(
        OrchestrationStep orchestrationStep,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orchestrationStep);
        cancellationToken.ThrowIfCancellationRequested();
        orchestrationStep = orchestrationStep.EnsureValid();

        lock (_gate)
        {
            if (!_runs.ContainsKey(orchestrationStep.RunId))
            {
                throw new InvalidOperationException($"Orchestration run '{orchestrationStep.RunId}' was not found.");
            }

            _stepsByRun[orchestrationStep.RunId].Add(orchestrationStep);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OrchestrationRun>> QueryCurrentAsync(
        string siteId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var result = _runs.Values
                .Where(run => string.Equals(run.SiteId, siteId, StringComparison.Ordinal))
                .OrderByDescending(run => run.StartedAt)
                .ToArray();
            return Task.FromResult<IReadOnlyList<OrchestrationRun>>(result);
        }
    }

    public Task<IReadOnlyList<OrchestrationStep>> QueryStepsAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var result = _stepsByRun.TryGetValue(runId, out var steps)
                ? steps.OrderBy(step => step.StepOrder).ToArray()
                : [];
            return Task.FromResult<IReadOnlyList<OrchestrationStep>>(result);
        }
    }
}

public sealed class InMemoryOrchestrationLockStore : IOrchestrationLockStore
{
    private readonly ConcurrentDictionary<string, OrchestrationLock> _locks = new(StringComparer.Ordinal);

    public Task<bool> TryAcquireAsync(
        OrchestrationLock orchestrationLock,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orchestrationLock);
        cancellationToken.ThrowIfCancellationRequested();
        orchestrationLock = orchestrationLock.EnsureValid();

        while (true)
        {
            if (!_locks.TryGetValue(orchestrationLock.LockKey, out var existing))
            {
                return Task.FromResult(_locks.TryAdd(orchestrationLock.LockKey, orchestrationLock));
            }

            if (existing.ExpiresAt > now)
            {
                return Task.FromResult(false);
            }

            if (_locks.TryUpdate(orchestrationLock.LockKey, orchestrationLock, existing))
            {
                return Task.FromResult(true);
            }
        }
    }

    public Task ReleaseAsync(
        string lockKey,
        string ownerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        cancellationToken.ThrowIfCancellationRequested();

        if (_locks.TryGetValue(lockKey, out var existing)
            && string.Equals(existing.OwnerId, ownerId, StringComparison.Ordinal))
        {
            _locks.TryRemove(lockKey, out _);
        }

        return Task.CompletedTask;
    }
}

public sealed class InMemoryDataBalanceStore : IDataBalanceStore
{
    private readonly ConcurrentDictionary<Key, DataBalanceStatus> _statuses = new();

    public Task UpsertAsync(
        DataBalanceStatus status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        cancellationToken.ThrowIfCancellationRequested();
        status = status.EnsureValid();
        _statuses[Key.From(status)] = status;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DataBalanceStatus>> QueryAsync(
        string siteId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        cancellationToken.ThrowIfCancellationRequested();

        var result = _statuses.Values
            .Where(status => string.Equals(status.SiteId, siteId, StringComparison.Ordinal))
            .OrderBy(status => status.DataGroup, StringComparer.Ordinal)
            .ThenBy(status => status.Source, StringComparer.Ordinal)
            .ThenBy(status => status.InstrumentId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<DataBalanceStatus>>(result);
    }

    private readonly record struct Key(
        string SiteId,
        string DataGroup,
        string Source,
        string InstrumentId)
    {
        public static Key From(DataBalanceStatus status) => new(
            status.SiteId,
            status.DataGroup,
            status.Source,
            status.InstrumentId);
    }
}
