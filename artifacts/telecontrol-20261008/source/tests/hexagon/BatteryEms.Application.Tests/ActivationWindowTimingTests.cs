using System.Globalization;
using BatteryEms.Application.Orchestration;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class ActivationWindowTimingTests
{
    [Theory]
    [InlineData("2026-09-29", "Z1", "2026-09-28T20:55:00Z")]
    [InlineData("2026-09-29", "Z2", "2026-09-29T02:55:00Z")]
    [InlineData("2026-09-29", "Z3", "2026-09-29T08:55:00Z")]
    [InlineData("2026-09-29", "Z4", "2026-09-29T14:55:00Z")]
    [InlineData("2026-01-15", "Z1", "2026-01-14T21:55:00Z")]
    [InlineData("2026-01-15", "Z2", "2026-01-15T03:55:00Z")]
    [InlineData("2026-01-15", "Z3", "2026-01-15T09:55:00Z")]
    [InlineData("2026-01-15", "Z4", "2026-01-15T15:55:00Z")]
    [InlineData("2027-01-01", "Z1", "2026-12-31T21:55:00Z")]
    public void Trigger_is_bound_to_Kyiv_delivery_date_and_seasonal_offset(
        string date,
        string window,
        string scheduledUtc)
    {
        var scheduled = DateTimeOffset.Parse(scheduledUtc, CultureInfo.InvariantCulture);
        var result = ActivationWindowTimingPolicy.Evaluate(DateOnly.Parse(date, CultureInfo.InvariantCulture), window, scheduled);

        Assert.True(result.CanStart);
        Assert.Equal(scheduled, result.ScheduledAtUtc);
        Assert.Equal(scheduled.AddMinutes(15), result.StartDeadlineUtc);
    }

    [Theory]
    [InlineData(-1, false, "activation-window-trigger-not-due")]
    [InlineData(0, true, null)]
    [InlineData(120, true, null)]
    [InlineData(899, true, null)]
    [InlineData(900, false, "activation-window-trigger-expired")]
    [InlineData(21600, false, "activation-window-trigger-expired")]
    public void Delay_never_advances_selected_window_and_deadline_is_exclusive(
        int seconds,
        bool allowed,
        string? error)
    {
        var scheduled = DateTimeOffset.Parse("2026-09-29T08:55:00Z", CultureInfo.InvariantCulture);
        var result = ActivationWindowTimingPolicy.Evaluate(
            new DateOnly(2026, 9, 29), "Z3", scheduled.AddSeconds(seconds));

        Assert.Equal(allowed, result.CanStart);
        Assert.Equal(error, result.BlockingCode);
        Assert.Equal(scheduled, result.ScheduledAtUtc);
    }

    [Fact]
    public void Z1_remains_for_next_delivery_date_when_delayed_past_local_midnight()
    {
        var result = ActivationWindowTimingPolicy.Evaluate(
            new DateOnly(2026, 9, 29), "Z1", DateTimeOffset.Parse("2026-09-28T21:02:00Z", CultureInfo.InvariantCulture));

        Assert.True(result.CanStart);
        Assert.Equal(DateTimeOffset.Parse("2026-09-28T20:55:00Z", CultureInfo.InvariantCulture), result.ScheduledAtUtc);
    }

    [Theory]
    [InlineData("2026-03-29")]
    [InlineData("2026-10-25")]
    public void Unsupported_DST_delivery_days_fail_closed(string date)
    {
        var result = ActivationWindowTimingPolicy.Evaluate(
            DateOnly.Parse(date, CultureInfo.InvariantCulture), "Z1", DateTimeOffset.Parse(date, CultureInfo.InvariantCulture).ToUniversalTime());

        Assert.False(result.CanStart);
        Assert.Equal("activation-dst-day-unsupported", result.BlockingCode);
    }
}
