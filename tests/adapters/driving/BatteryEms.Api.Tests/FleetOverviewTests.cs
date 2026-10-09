using System.Text;
using System.Text.Json;
using BatteryEms.Api.Endpoints;
using BatteryEms.Application.Api;
using BatteryEms.Application.Realtime;
using BatteryEms.Application.Time;
using BatteryEms.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class FleetOverviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-12, 35)]
    [InlineData(12, 47)]
    public async Task Fleet_sums_PV_CHP_and_positive_BESS_once_and_separates_consumption(double battery, double expected)
    {
        var store = Store();
        Update(store, "a", 10, 30);
        Update(store, "chp", 5, null);
        Update(store, "b", 20, 40, quality: DataQuality.Substituted("sample-time-unknown"));
        var sites = new[] { Site("a", Source("a-pv", "a", "pv"), Source("a-load", "a", "load"),
            Source("a-chp", "chp", "chp"), Source("a-bess", "battery", "battery")),
            Site("b", Source("b-pv", "b", "pv"), Source("b-load", "b", "load")) };
        var fleet = await Read(sites, store, battery);
        Assert.Equal(expected, fleet.Generation.PowerKw);
        Assert.Equal(70, fleet.Consumption.PowerKw);
        Assert.Equal("estimated", fleet.Generation.Status);
        Assert.Equal("estimated", fleet.Consumption.Status);
        Assert.Equal(4, fleet.Generation.AvailableSources);
        Assert.Equal(4, fleet.Generation.ExpectedSources);
    }

    [Fact]
    public async Task Missing_or_stale_sources_are_not_zero_and_no_load_configuration_is_partial()
    {
        var store = Store();
        Update(store, "a", 0, 0);
        Update(store, "b", 100, 100, Now.AddMinutes(-20));
        var fleet = await Read([Site("a", Source("a-pv", "a", "pv"), Source("a-load", "a", "load")),
            Site("b", Source("b-pv", "b", "pv"))], store);
        Assert.Equal(0, fleet.Generation.PowerKw);
        Assert.Equal("partial", fleet.Generation.Status);
        Assert.Equal(1, fleet.Generation.AvailableSources);
        Assert.Equal(2, fleet.Generation.ExpectedSources);
        Assert.Equal("partial", fleet.Consumption.Status);
        Assert.Null(fleet.Sites[1].Generation.PowerKw);
        Assert.Null(fleet.Sites[1].Consumption.PowerKw);
    }

    [Fact]
    public async Task Shared_installations_require_mapping_before_any_fleet_total_is_published()
    {
        var store = Store();
        Update(store, "a", 10, 30);
        var fleet = await Read([Site("a", Source("a", "a", "pv"), Source("load", "a", "load"))], store, confirmed: false);
        Assert.False(fleet.MembershipConfirmed);
        Assert.Null(fleet.Generation.PowerKw);
        Assert.Null(fleet.Consumption.PowerKw);
        Assert.Equal("membership_pending", fleet.Generation.Status);
        Assert.Equal(10, fleet.Sites[0].Generation.PowerKw);
    }

    [Theory]
    [InlineData("same-device", "other-feed")]
    [InlineData("other-device", "same-feed")]
    public async Task Duplicate_physical_components_or_telemetry_are_rejected(string physical, string telemetry)
    {
        var config = Config([Site("a", Source("same-device", "same-feed", "pv")),
            Site("b", Source(physical, telemetry, "pv"))], true);
        var result = await FleetOverview.ReadAsync(config, Store(), new BatteryQuery(0), new Clock(), CancellationToken.None);
        Assert.Equal(503, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Endpoint_is_available_for_readers_and_empty_fleet_has_no_invented_zero()
    {
        using var factory = new BatteryEmsApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/sites/overview");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("sites").GetArrayLength());
        Assert.False(body.RootElement.GetProperty("generation").TryGetProperty("power_kw", out _));
    }

    private static InMemorySiteTelemetryStore Store() => new(TimeSpan.FromMinutes(10));
    private static void Update(InMemorySiteTelemetryStore store, string id, double pv, double? load,
        DateTimeOffset? measured = null, DataQuality? quality = null) =>
        store.Update(new SiteTelemetry(measured ?? Now, id, pv, load, null, null, quality ?? DataQuality.Valid), Now);
    private static FleetSourceDefinition Source(string physical, string telemetry, string kind) =>
        new() { PhysicalId = physical, TelemetryId = telemetry, Kind = kind };
    private static FleetSiteDefinition Site(string id, params FleetSourceDefinition[] sources) =>
        new() { SiteId = id, Name = id, Sources = sources };
    private static IConfiguration Config(FleetSiteDefinition[] sites, bool confirmed)
    {
        var json = JsonSerializer.Serialize(new { Dashboard = new { Sites = sites, MembershipConfirmed = confirmed } });
        return new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
    }
    private static async Task<FleetOverviewResponse> Read(FleetSiteDefinition[] sites, InMemorySiteTelemetryStore store,
        double battery = 0, bool confirmed = true)
    {
        var result = await FleetOverview.ReadAsync(Config(sites, confirmed), store, new BatteryQuery(battery), new Clock(), CancellationToken.None);
        return Assert.IsType<FleetOverviewResponse>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
    }
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => Now; }
    private sealed class BatteryQuery(double power) : IBatteryStatusQuery
    {
        public Task<BatteryStatusView?> FindAsync(string assetId, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<BatteryStatusView?>(new BatteryStatusView(assetId,
                new BatteryTelemetry(Now, assetId, 50, 100, power, 0, 400, 0, 20, true, "none", DataQuality.Valid),
                DataQuality.Valid, Now, null));
    }
}
