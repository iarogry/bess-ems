using BatteryEms.Application.Site;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class InMemorySiteConsumptionStoreTests
{
    [Fact]
    public async Task AppendAsync_upserts_and_query_returns_site_consumption_readings()
    {
        var store = new InMemorySiteConsumptionStore();
        var timestamp = new DateTimeOffset(2026, 6, 5, 1, 0, 0, TimeSpan.Zero);
        await store.AppendAsync([
            new SiteConsumptionReading("site-1", "101", "Main meter", timestamp, 10, null, null, null, "askue"),
            new SiteConsumptionReading("site-1", "101", "Main meter", timestamp, 11, 1, null, null, "askue"),
            new SiteConsumptionReading("site-2", "101", "Other site", timestamp, 99, null, null, null, "askue"),
        ], CancellationToken.None);

        var result = await store.QueryAsync(
            new SiteConsumptionQuery("site-1", timestamp.AddMinutes(-1), timestamp.AddHours(1)),
            CancellationToken.None);

        var reading = Assert.Single(result);
        Assert.Equal("site-1", reading.SiteId);
        Assert.Equal("101", reading.PointId);
        Assert.Equal(11, reading.Apoz);
        Assert.Equal(1, reading.Aneg);
    }

    [Fact]
    public async Task QueryAsync_filters_by_point_and_source()
    {
        var store = new InMemorySiteConsumptionStore();
        var timestamp = new DateTimeOffset(2026, 6, 5, 1, 0, 0, TimeSpan.Zero);
        await store.AppendAsync([
            new SiteConsumptionReading("site-1", "101", "Main", timestamp, 10, null, null, null, "askue"),
            new SiteConsumptionReading("site-1", "102", "Backup", timestamp, 20, null, null, null, "askue"),
            new SiteConsumptionReading("site-1", "101", "Main", timestamp.AddHours(1), 30, null, null, null, "manual"),
        ], CancellationToken.None);

        var result = await store.QueryAsync(
            new SiteConsumptionQuery("site-1", timestamp.AddMinutes(-1), timestamp.AddHours(2), PointId: "101", Source: "askue"),
            CancellationToken.None);

        var reading = Assert.Single(result);
        Assert.Equal(10, reading.Apoz);
    }
}
