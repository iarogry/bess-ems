using BatteryEms.Application.Site;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class InMemorySiteMeasurementStoreTests
{
    [Fact]
    public async Task AppendAsync_upserts_and_query_returns_site_measurements()
    {
        var store = new InMemorySiteMeasurementStore();
        var timestamp = new DateTimeOffset(2026, 6, 5, 1, 0, 0, TimeSpan.Zero);
        await store.AppendAsync([
            new SiteMeasurementReading(
                "site-1",
                "askue",
                "meter",
                "869",
                "Main meter",
                timestamp,
                TimeSpan.FromMinutes(30),
                "active_energy_import",
                0.023,
                "kWh",
                "measured",
                GroupParentId: "0",
                Scale: -1),
            new SiteMeasurementReading(
                "site-1",
                "askue",
                "meter",
                "869",
                "Main meter",
                timestamp,
                TimeSpan.FromMinutes(30),
                "active_energy_import",
                0.024,
                "kWh",
                "measured",
                GroupParentId: "0",
                Scale: -1),
            new SiteMeasurementReading(
                "site-2",
                "askue",
                "meter",
                "869",
                "Other site",
                timestamp,
                TimeSpan.FromMinutes(30),
                "active_energy_import",
                99,
                "kWh",
                "measured"),
        ], CancellationToken.None);

        var result = await store.QueryAsync(
            new SiteMeasurementQuery(
                "site-1",
                timestamp.AddMinutes(-1),
                timestamp.AddHours(1),
                Source: "askue",
                InstrumentId: "869",
                Metric: "active_energy_import"),
            CancellationToken.None);

        var reading = Assert.Single(result);
        Assert.Equal("site-1", reading.SiteId);
        Assert.Equal("askue", reading.Source);
        Assert.Equal("meter", reading.InstrumentType);
        Assert.Equal("869", reading.InstrumentId);
        Assert.Equal("active_energy_import", reading.Metric);
        Assert.Equal(0.024, reading.Value);
        Assert.Equal("kWh", reading.Unit);
        Assert.Equal("0", reading.GroupParentId);
        Assert.Equal(-1, reading.Scale);
    }

    [Fact]
    public async Task QueryAsync_filters_source_instrument_type_and_metric()
    {
        var store = new InMemorySiteMeasurementStore();
        var timestamp = new DateTimeOffset(2026, 6, 5, 1, 0, 0, TimeSpan.Zero);
        await store.AppendAsync([
            new SiteMeasurementReading("site-1", "askue", "meter", "869", "Meter", timestamp, TimeSpan.FromMinutes(30), "active_power_import", 0.046, "kW", "measured"),
            new SiteMeasurementReading("site-1", "deye", "pv", "station-1", "PV", timestamp, null, "pv_power", 12.5, "kW", "measured"),
            new SiteMeasurementReading("site-1", "askue", "meter", "870", "Other", timestamp, TimeSpan.FromMinutes(30), "active_power_import", 1.5, "kW", "measured"),
        ], CancellationToken.None);

        var result = await store.QueryAsync(
            new SiteMeasurementQuery(
                "site-1",
                timestamp.AddMinutes(-1),
                timestamp.AddHours(1),
                Source: "deye",
                InstrumentType: "pv",
                Metric: "pv_power"),
            CancellationToken.None);

        var reading = Assert.Single(result);
        Assert.Equal("station-1", reading.InstrumentId);
        Assert.Equal(12.5, reading.Value);
        Assert.Equal("kW", reading.Unit);
    }
}
