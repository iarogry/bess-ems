using BatteryEms.Application.Markets;
using BatteryEms.Application.Orchestration;
using BatteryEms.Domain;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class ScheduleShadowPlanProjectionTests
{
    [Fact]
    public void Projects_hourly_schedule_to_four_six_interval_windows_without_writes()
    {
        var targets = new double[24];
        targets[2] = -160;
        targets[7] = 160;
        targets[13] = -80;
        targets[20] = 40;

        var snapshot = ScheduleShadowPlanProjector.Project(
            "site-shadow",
            new DateOnly(2026, 9, 23),
            Schedule("site-shadow", new DateOnly(2026, 9, 23), targets),
            new ShadowDeyeProjectionOptions());

        Assert.True(snapshot.PayloadReady);
        Assert.Empty(snapshot.BlockingCodes);
        Assert.Collection(
            snapshot.Windows,
            window => Assert.Equal("Z1", window.WindowId),
            window => Assert.Equal("Z2", window.WindowId),
            window => Assert.Equal("Z3", window.WindowId),
            window => Assert.Equal("Z4", window.WindowId));
        Assert.All(snapshot.Windows, window => Assert.Equal(6, window.Intervals.Count));
        Assert.Contains(snapshot.Windows[0].Intervals, interval =>
            interval.StartTime == "02:00"
            && interval.EnableGridCharge
            && interval.PowerWatts == 80_000
            && interval.SocPercent == 100);
        Assert.Contains(snapshot.Windows[1].Intervals, interval =>
            interval.StartTime == "07:00"
            && interval.EnableSell
            && interval.PowerWatts == 80_000
            && interval.SocPercent == 30);
    }

    [Fact]
    public void Fails_closed_when_a_zone_needs_more_than_six_setting_transitions()
    {
        var targets = new double[24];
        for (var hour = 6; hour < 12; hour++)
        {
            targets[hour] = hour % 2 == 0 ? -160 : 160;
        }

        var snapshot = ScheduleShadowPlanProjector.Project(
            "site-shadow",
            new DateOnly(2026, 9, 23),
            Schedule("site-shadow", new DateOnly(2026, 9, 23), targets),
            new ShadowDeyeProjectionOptions());

        Assert.False(snapshot.PayloadReady);
        Assert.Equal(["shadow-z2-not-representable"], snapshot.BlockingCodes);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task Module_persists_blocked_shadow_observation_when_schedule_is_missing()
    {
        var schedules = new InMemoryScheduleRepository();
        var store = new InMemoryShadowPlanComparisonStore();
        var module = new ScheduleShadowPlanProjectionModule(
            schedules,
            store,
            new ShadowDeyeProjectionOptions());
        var context = new OrchestrationModuleContext(
            Guid.NewGuid(),
            "site-shadow",
            "day-ahead-shadow",
            null,
            null,
            "projection-test",
            DateTimeOffset.UtcNow,
            "2026-09-23");

        var result = await module.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(OrchestrationStepStatus.Blocked, result.Status);
        Assert.Equal("shadow-day-ahead-schedule-missing", result.ErrorCode);
        var snapshot = await store.FindSnapshotAsync(
            ShadowPlanSide.Shadow,
            "site-shadow",
            new DateOnly(2026, 9, 23),
            CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.False(snapshot!.PayloadReady);
    }

    private static Schedule Schedule(string siteId, DateOnly date, IReadOnlyList<double> targets)
    {
        var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3))
            .ToUniversalTime();
        var windows = targets
            .Select((target, hour) => new ScheduleWindow(
                start.AddHours(hour),
                start.AddHours(hour + 1),
                target))
            .ToArray();
        return new Schedule(siteId, ScheduleType.DayAhead, "UA-IPS", 1, windows);
    }
}
