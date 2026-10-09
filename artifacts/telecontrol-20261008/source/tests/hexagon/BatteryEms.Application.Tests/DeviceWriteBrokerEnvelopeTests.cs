using BatteryEms.Application.Orchestration;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class DeviceWriteBrokerEnvelopeTests
{
    private static readonly string[] SlotStarts = ["00:00", "04:00", "08:00", "12:00", "16:00", "20:00"];
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 20, 55, 25, TimeSpan.Zero);

    [Fact]
    public void Exact_saved_plan_and_window_pass_within_trigger_window()
        => Assert.NotNull(Envelope().EnsureValid(Now));

    [Theory]
    [InlineData(-26)]
    [InlineData(875)]
    public void Before_trigger_or_at_deadline_is_not_permission_to_start(int seconds)
        => Assert.Throws<ArgumentException>(() => Envelope().EnsureValid(Now.AddSeconds(seconds)));

    [Theory]
    [InlineData(-1, 30, 0)]
    [InlineData(80001, 30, 0)]
    [InlineData(10000, 29, 0)]
    [InlineData(10000, 101, 0)]
    [InlineData(10000, 30, -1)]
    public void Valid_hash_does_not_authorize_out_of_bounds_device_settings(int power, int soc, int voltage)
    {
        var valid = Envelope();
        var intervals = valid.Window.Intervals.ToArray();
        intervals[0] = intervals[0] with { PowerWatts = power, SocPercent = soc, Voltage = voltage };
        var window = valid.Window with { Intervals = intervals };
        var plan = valid.Plan with { Windows = valid.Plan.Windows.Select(item => item.WindowId == window.WindowId ? window : item).ToArray() };
        var envelope = new DeviceWriteBrokerEnvelope(valid.Attempt with { PayloadHash = ActivationPayloadIntegrity.ComputeHash(plan) }, plan, window);
        Assert.Throws<ArgumentException>(() => envelope.EnsureValid(Now));
    }

    [Fact]
    public void Duplicate_or_unknown_windows_are_not_a_complete_daily_plan()
    {
        var valid = Envelope();
        var plan = valid.Plan with { Windows = [valid.Window, valid.Window, valid.Window, valid.Window] };
        var envelope = valid with { Plan = plan, Attempt = valid.Attempt with { PayloadHash = ActivationPayloadIntegrity.ComputeHash(plan) } };
        Assert.Throws<ArgumentException>(() => envelope.EnsureValid(Now));
    }

    [Fact]
    public void Foreign_site_date_hash_or_selected_window_is_rejected()
    {
        var valid = Envelope();
        Assert.Throws<ArgumentException>(() => (valid with { Attempt = valid.Attempt with { SiteId = "other-site" } }).EnsureValid(Now));
        Assert.Throws<ArgumentException>(() => (valid with { Attempt = valid.Attempt with { DeliveryDate = valid.Plan.DeliveryDate.AddDays(1) } }).EnsureValid(Now));
        Assert.Throws<ArgumentException>(() => (valid with { Attempt = valid.Attempt with { PayloadHash = new string('A', 64) } }).EnsureValid(Now));
        Assert.Throws<ArgumentException>(() => (valid with { Window = valid.Plan.Windows[1] }).EnsureValid(Now));
    }

    private static DeviceWriteBrokerEnvelope Envelope()
    {
        var plan = new ShadowPlanSnapshot("site", new DateOnly(2026, 9, 23), true, [],
            Enumerable.Range(1, 4).Select(index => new ShadowTouWindow($"Z{index}",
                SlotStarts
                    .Select(start => new ShadowTouInterval(start, true, false, false, 10000, 30, 0)).ToArray())).ToArray());
        var request = new DeviceWriteBrokerBeginRequest(Guid.NewGuid(), plan.SiteId, plan.DeliveryDate, "Z1",
            ActivationPayloadIntegrity.ComputeHash(plan), ActivationWriterAuthority.LegacyRunner, "writer", 1, 1, null, Now);
        return new(request, plan, plan.Windows[0]);
    }
}
