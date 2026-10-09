using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class ShadowPlanOrchestrationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly DeliveryDate = new(2026, 9, 23);

    [Fact]
    public async Task Equivalent_observations_complete_shadow_run_and_persist_result()
    {
        var store = new InMemoryShadowPlanComparisonStore();
        var plan = Plan();
        await store.PutSnapshotAsync(ShadowPlanSide.Legacy, plan, CancellationToken.None);
        await store.PutSnapshotAsync(ShadowPlanSide.Shadow, plan with { }, CancellationToken.None);
        var (useCase, runs) = CreateUseCase(store);

        var run = await useCase.StartAsync(Command(), CancellationToken.None);

        Assert.Equal(OrchestrationRunStatus.Succeeded, run.Status);
        var comparison = await store.FindLatestComparisonAsync(
            plan.SiteId,
            DeliveryDate,
            CancellationToken.None);
        Assert.NotNull(comparison);
        Assert.True(comparison!.IsEquivalent);
        Assert.Empty(comparison.Mismatches);
        var step = Assert.Single(await runs.QueryStepsAsync(run.RunId, CancellationToken.None));
        Assert.Equal(OrchestrationStepStatus.Succeeded, step.Status);
        Assert.StartsWith("shadow-comparison:", step.OutputRef, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mismatch_is_persisted_as_partial_without_affecting_legacy_snapshot()
    {
        var store = new InMemoryShadowPlanComparisonStore();
        var legacy = Plan();
        var shadow = ChangePower(legacy, 79_990);
        await store.PutSnapshotAsync(ShadowPlanSide.Legacy, legacy, CancellationToken.None);
        await store.PutSnapshotAsync(ShadowPlanSide.Shadow, shadow, CancellationToken.None);
        var (useCase, _) = CreateUseCase(store);

        var run = await useCase.StartAsync(Command(idempotencyKey: "shadow-mismatch"), CancellationToken.None);

        Assert.Equal(OrchestrationRunStatus.Partial, run.Status);
        var comparison = await store.FindLatestComparisonAsync(
            legacy.SiteId,
            DeliveryDate,
            CancellationToken.None);
        Assert.NotNull(comparison);
        Assert.False(comparison!.IsEquivalent);
        Assert.Equal("windows[0].intervals[1].power_watts", Assert.Single(comparison.Mismatches).Path);
        var storedLegacy = await store.FindSnapshotAsync(
            ShadowPlanSide.Legacy,
            legacy.SiteId,
            DeliveryDate,
            CancellationToken.None);
        Assert.Equal(80_000, storedLegacy!.Windows[0].Intervals[1].PowerWatts);
    }

    [Fact]
    public async Task Missing_observation_blocks_shadow_run_without_creating_comparison()
    {
        var store = new InMemoryShadowPlanComparisonStore();
        await store.PutSnapshotAsync(ShadowPlanSide.Legacy, Plan(), CancellationToken.None);
        var (useCase, runs) = CreateUseCase(store);

        var run = await useCase.StartAsync(Command(idempotencyKey: "shadow-missing"), CancellationToken.None);

        Assert.Equal(OrchestrationRunStatus.Blocked, run.Status);
        var step = Assert.Single(await runs.QueryStepsAsync(run.RunId, CancellationToken.None));
        Assert.Equal("shadow-input-missing", step.ErrorCode);
        Assert.Null(await store.FindLatestComparisonAsync(
            "site-reference",
            DeliveryDate,
            CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_delivery_date_blocks_before_snapshot_lookup()
    {
        var store = new InMemoryShadowPlanComparisonStore();
        var (useCase, runs) = CreateUseCase(store);
        var command = Command(idempotencyKey: "shadow-invalid-date") with { TriggerRef = "23-09-2026" };

        var run = await useCase.StartAsync(command, CancellationToken.None);

        Assert.Equal(OrchestrationRunStatus.Blocked, run.Status);
        var step = Assert.Single(await runs.QueryStepsAsync(run.RunId, CancellationToken.None));
        Assert.Equal("shadow-delivery-date-invalid", step.ErrorCode);
    }

    [Fact]
    public async Task Store_freezes_published_snapshot_against_later_array_mutation()
    {
        var store = new InMemoryShadowPlanComparisonStore();
        var source = Plan();
        var mutableWindows = source.Windows.ToArray();
        var published = source with { Windows = mutableWindows };
        await store.PutSnapshotAsync(ShadowPlanSide.Legacy, published, CancellationToken.None);

        mutableWindows[0] = mutableWindows[0] with
        {
            Intervals = mutableWindows[0].Intervals
                .Select(interval => interval with { PowerWatts = 1 })
                .ToArray(),
        };

        var stored = await store.FindSnapshotAsync(
            ShadowPlanSide.Legacy,
            source.SiteId,
            DeliveryDate,
            CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(80_000, stored!.Windows[0].Intervals[1].PowerWatts);
    }

    private static (DefaultOrchestrationUseCase UseCase, InMemoryOrchestrationRunStore Runs)
        CreateUseCase(IShadowPlanComparisonStore store)
    {
        var runs = new InMemoryOrchestrationRunStore();
        var clock = new FixedClock(Now);
        return (
            new DefaultOrchestrationUseCase(
                runs,
                new InMemoryOrchestrationLockStore(),
                new InMemoryDataBalanceStore(),
                new DefaultDataReadinessPolicy(),
                clock,
                [new ShadowPlanComparisonModule(store, clock)]),
            runs);
    }

    private static OrchestrationStartCommand Command(string idempotencyKey = "shadow-equivalent") =>
        new(
            "site-reference",
            ShadowPlanComparisonModule.SupportedRunType,
            Now,
            Now.AddDays(1),
            OrchestrationTriggerType.Manual,
            DeliveryDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            InputHash: "sanitized-input-hash",
            IdempotencyKey: idempotencyKey);

    private static ShadowPlanSnapshot ChangePower(ShadowPlanSnapshot source, int powerWatts)
    {
        var intervals = source.Windows[0].Intervals.ToArray();
        intervals[1] = intervals[1] with { PowerWatts = powerWatts };
        var windows = source.Windows.ToArray();
        windows[0] = windows[0] with { Intervals = intervals };
        return source with { Windows = windows };
    }

    private static ShadowPlanSnapshot Plan() =>
        new(
            "site-reference",
            DeliveryDate,
            true,
            [],
            Enumerable.Range(1, 4)
                .Select(index => new ShadowTouWindow(
                    $"Z{index}",
                    [
                        Interval("00:00", 0),
                        Interval("01:00", 80_000),
                        Interval("02:00", 0),
                        Interval("03:00", 80_000),
                        Interval("04:00", 0),
                        Interval("05:00", 0),
                    ]))
                .ToArray());

    private static ShadowTouInterval Interval(string startTime, int powerWatts) =>
        new(startTime, true, false, powerWatts > 0, powerWatts, 30, 290);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
