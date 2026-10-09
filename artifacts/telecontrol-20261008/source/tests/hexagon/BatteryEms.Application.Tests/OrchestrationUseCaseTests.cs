using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class OrchestrationUseCaseTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 6, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartAsync_prevents_duplicate_run_by_idempotency_key()
    {
        var runs = new InMemoryOrchestrationRunStore();
        var useCase = CreateUseCase(runs: runs);
        var command = DayAheadCommand(idempotencyKey: "same-key");

        var first = await useCase.StartAsync(command, CancellationToken.None);
        var second = await useCase.StartAsync(command, CancellationToken.None);

        Assert.Equal(first.RunId, second.RunId);
        var stored = await runs.QueryCurrentAsync("site-1", CancellationToken.None);
        Assert.Single(stored);
    }

    [Fact]
    public async Task StartAsync_blocks_when_critical_data_is_not_ready()
    {
        var balances = new InMemoryDataBalanceStore();
        await balances.UpsertAsync(
            Balance("prices", DataRole.Critical, DataBalanceState.Missing),
            CancellationToken.None);
        var useCase = CreateUseCase(dataBalances: balances);

        var run = await useCase.StartAsync(DayAheadCommand(), CancellationToken.None);

        Assert.Equal(OrchestrationRunStatus.Blocked, run.Status);
        Assert.Equal("prices-not-ready", run.ErrorCode);
    }

    [Fact]
    public async Task StartAsync_continues_with_partial_status_when_advisory_data_is_not_ready()
    {
        var balances = new InMemoryDataBalanceStore();
        await balances.UpsertAsync(
            Balance("fusionsolar", DataRole.Advisory, DataBalanceState.SourceError),
            CancellationToken.None);
        var module = new RecordingModule("site_settings_preparation", 10);
        var useCase = CreateUseCase(dataBalances: balances, modules: [module]);

        var run = await useCase.StartAsync(DayAheadCommand(), CancellationToken.None);

        Assert.Equal(OrchestrationRunStatus.Partial, run.Status);
        Assert.Equal(1, module.CallCount);
    }

    [Fact]
    public async Task StartAsync_records_module_steps_when_data_is_ready()
    {
        var runs = new InMemoryOrchestrationRunStore();
        var balances = new InMemoryDataBalanceStore();
        await balances.UpsertAsync(
            Balance("prices", DataRole.Critical, DataBalanceState.Ready),
            CancellationToken.None);
        var useCase = CreateUseCase(
            runs: runs,
            dataBalances: balances,
            modules:
            [
                new RecordingModule("price_import_rdn", 0),
                new RecordingModule("battery_day_ahead_optimization", 20),
            ]);

        var run = await useCase.StartAsync(DayAheadCommand(), CancellationToken.None);

        Assert.Equal(OrchestrationRunStatus.Succeeded, run.Status);
        var steps = await runs.QueryStepsAsync(run.RunId, CancellationToken.None);
        Assert.Equal(["price_import_rdn", "battery_day_ahead_optimization"], steps.Select(step => step.Module));
        Assert.All(steps, step => Assert.Equal(OrchestrationStepStatus.Succeeded, step.Status));
    }

    [Fact]
    public async Task StartAsync_returns_blocked_run_when_lock_is_held()
    {
        var locks = new InMemoryOrchestrationLockStore();
        var command = DayAheadCommand(idempotencyKey: "held-key");
        await locks.TryAcquireAsync(
            new OrchestrationLock(
                "orchestration:held-key",
                "other-owner",
                Now,
                Now.AddMinutes(5),
                "{}"),
            Now,
            CancellationToken.None);
        var useCase = CreateUseCase(locks: locks);

        var run = await useCase.StartAsync(command, CancellationToken.None);

        Assert.Equal(OrchestrationRunStatus.Blocked, run.Status);
        Assert.Equal("lock-held", run.ErrorCode);
    }

    private static DefaultOrchestrationUseCase CreateUseCase(
        IOrchestrationRunStore? runs = null,
        IOrchestrationLockStore? locks = null,
        IDataBalanceStore? dataBalances = null,
        IReadOnlyList<IOrchestrationModule>? modules = null) =>
        new(
            runs ?? new InMemoryOrchestrationRunStore(),
            locks ?? new InMemoryOrchestrationLockStore(),
            dataBalances ?? new InMemoryDataBalanceStore(),
            new DefaultDataReadinessPolicy(),
            new FixedClock(Now),
            modules);

    private static OrchestrationStartCommand DayAheadCommand(string? idempotencyKey = null) => new(
        "site-1",
        "day-ahead",
        new DateTimeOffset(2026, 6, 7, 0, 0, 0, TimeSpan.FromHours(3)),
        new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.FromHours(3)),
        OrchestrationTriggerType.Manual,
        IdempotencyKey: idempotencyKey);

    private static DataBalanceStatus Balance(
        string group,
        DataRole role,
        DataBalanceState status) => new(
        "site-1",
        group,
        "test",
        group,
        role,
        status,
        FreshnessDeadlineUtc: Now.AddMinutes(30),
        LastSuccessAtUtc: status == DataBalanceState.Ready ? Now : null,
        LastAttemptAtUtc: Now,
        NextAttemptAtUtc: null,
        AttemptCount: 1,
        LastErrorCode: status == DataBalanceState.SourceError ? "source-error" : null,
        LastErrorMessage: null,
        QualitySummary: null,
        MetadataJson: "{}");

    private sealed class RecordingModule : IOrchestrationModule
    {
        public RecordingModule(string module, int stepOrder)
        {
            ModuleName = module;
            StepOrder = stepOrder;
        }

        public string ModuleName { get; }

        public int StepOrder { get; }

        public int CallCount { get; private set; }

        public Task<OrchestrationModuleResult> ExecuteAsync(
            OrchestrationModuleContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new OrchestrationModuleResult(
                OrchestrationStepStatus.Succeeded,
                MetadataJson: $$"""{"module":"{{ModuleName}}"}"""));
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }
    }
}
