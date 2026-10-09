using BatteryEms.Api.Scheduling;
using BatteryEms.Application.Markets;
using BatteryEms.Application.Orchestration;
using BatteryEms.Domain;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class ShadowDayAheadSchedulerTests
{
    [Fact]
    public async Task Uses_current_delivery_day_before_trigger_and_tomorrow_after_trigger()
    {
        var before = Fixture(new DateOnly(2026, 9, 23));
        var after = Fixture(new DateOnly(2026, 9, 24));

        var beforeResult = await before.Scheduler.RunOnceAsync(
            new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        var afterResult = await after.Scheduler.RunOnceAsync(
            new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 9, 23), beforeResult.DeliveryDate);
        Assert.Equal(new DateOnly(2026, 9, 24), afterResult.DeliveryDate);
        Assert.Equal(OrchestrationTriggerType.Scheduled, before.Trigger.TriggerType);
        Assert.Equal(OrchestrationTriggerType.Scheduled, after.Trigger.TriggerType);
    }

    [Fact]
    public async Task Does_not_consume_run_when_legacy_input_is_late()
    {
        var fixture = Fixture(new DateOnly(2026, 9, 23), legacyAvailable: false);

        var result = await fixture.Scheduler.RunOnceAsync(
            new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(ShadowSchedulerTickStatus.LegacyUnavailable, result.Status);
        Assert.Equal(0, fixture.Trigger.CallCount);
    }

    [Fact]
    public async Task Does_not_consume_run_when_new_schedule_is_late()
    {
        var fixture = Fixture(new DateOnly(2026, 9, 23), scheduleAvailable: false);

        var result = await fixture.Scheduler.RunOnceAsync(
            new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(ShadowSchedulerTickStatus.ShadowScheduleUnavailable, result.Status);
        Assert.Equal("shadow-day-ahead-schedule-missing", result.ReasonCode);
        Assert.Equal(0, fixture.Trigger.CallCount);
    }

    [Fact]
    public async Task Starts_at_most_once_per_delivery_day_in_one_process()
    {
        var fixture = Fixture(new DateOnly(2026, 9, 23));

        var first = await fixture.Scheduler.RunOnceAsync(
            new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        var second = await fixture.Scheduler.RunOnceAsync(
            new DateTimeOffset(2026, 9, 23, 8, 1, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(ShadowSchedulerTickStatus.Started, first.Status);
        Assert.Equal(ShadowSchedulerTickStatus.AlreadyStarted, second.Status);
        Assert.Equal(1, fixture.Trigger.CallCount);
    }

    private static FixtureState Fixture(
        DateOnly deliveryDate,
        bool legacyAvailable = true,
        bool scheduleAvailable = true)
    {
        const string siteId = "site-shadow";
        var repository = new InMemoryScheduleRepository();
        if (scheduleAvailable)
        {
            repository.Replace(Schedule(siteId, deliveryDate), 0);
        }

        var trigger = new CapturingTrigger();
        var scheduler = new ShadowDayAheadScheduler(
            new ShadowDayAheadSchedulerOptions(siteId, 14, 0, 60),
            new ShadowDeyeProjectionOptions(),
            new FakeLegacySource(legacyAvailable),
            repository,
            trigger);
        return new FixtureState(scheduler, trigger);
    }

    private static Schedule Schedule(string siteId, DateOnly date)
    {
        var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3))
            .ToUniversalTime();
        return new Schedule(
            siteId,
            ScheduleType.DayAhead,
            "UA-IPS",
            1,
            Enumerable.Range(0, 24)
                .Select(hour => new ScheduleWindow(
                    start.AddHours(hour),
                    start.AddHours(hour + 1),
                    0))
                .ToArray());
    }

    private sealed record FixtureState(
        ShadowDayAheadScheduler Scheduler,
        CapturingTrigger Trigger);

    private sealed class FakeLegacySource(bool available) : ILegacyPlanSnapshotSource
    {
        public Task<LegacyPlanSnapshotLoadResult> LoadAsync(
            string siteId,
            DateOnly deliveryDate,
            CancellationToken cancellationToken) =>
            Task.FromResult(available
                ? new LegacyPlanSnapshotLoadResult(
                    new ShadowPlanSnapshot(siteId, deliveryDate, false, ["legacy-blocked"], []))
                : new LegacyPlanSnapshotLoadResult(
                    null,
                    "legacy-scenario-not-found",
                    "Legacy scenario is not available yet."));
    }

    private sealed class CapturingTrigger : IShadowRunTrigger
    {
        public int CallCount { get; private set; }
        public OrchestrationTriggerType? TriggerType { get; private set; }

        public Task<OrchestrationRun> StartAsync(
            string siteId,
            DateOnly deliveryDate,
            OrchestrationTriggerType triggerType,
            CancellationToken cancellationToken)
        {
            CallCount++;
            TriggerType = triggerType;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new OrchestrationRun(
                Guid.NewGuid(),
                siteId,
                "day-ahead-shadow",
                null,
                null,
                triggerType,
                deliveryDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                $"day-ahead-shadow:{siteId}:{deliveryDate:yyyy-MM-dd}",
                OrchestrationRunStatus.Succeeded,
                now,
                now,
                null,
                null,
                null,
                null,
                "{}"));
        }
    }
}
