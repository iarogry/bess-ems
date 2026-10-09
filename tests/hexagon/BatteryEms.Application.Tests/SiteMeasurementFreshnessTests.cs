using BatteryEms.Application.Realtime;
using BatteryEms.Domain;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class SiteMeasurementFreshnessTests
{
    [Theory]
    [InlineData(DataQualityState.Valid)]
    [InlineData(DataQualityState.Substituted)]
    public void Recent_poll_does_not_refresh_old_vendor_measurement(DataQualityState flag)
    {
        var store = new InMemorySiteTelemetryStore(TimeSpan.FromMinutes(10));
        var received = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        store.Update(new SiteTelemetry(received.AddMinutes(-9), "pv", 20, null, null, null, new DataQuality(flag, "test")), received);
        Assert.Equal(flag, store.GetLatest("pv", received)!.Quality.Flag);
        Assert.Equal(DataQualityState.Stale, store.GetLatest("pv", received.AddMinutes(2))!.Quality.Flag);
    }
}
