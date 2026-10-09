using System.Net.Http.Json;
using System.Text.Json;
using BatteryEms.Application.Site;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
namespace BatteryEms.Api.Tests;
public sealed class AskueEnergyEndpointTests
{
    [Theory]
    [InlineData("site-khlibzavod-5", true)]
    [InlineData("site-another", false)]
    public async Task Interval_energy_uses_verified_transformer_product_only_for_its_site(string siteId, bool normalized)
    {
        using var factory = new BatteryEmsApiFactory();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ISiteConsumptionStore>();
        await store.AppendAsync([new SiteConsumptionReading(siteId, "870", "Main", DateTimeOffset.UtcNow.AddMinutes(-5),
            0.01, 0.02, null, null, "askue", 1800)], CancellationToken.None);
        var body = await client.GetFromJsonAsync<JsonElement>("/site/" + siteId + "/consumption/recent");
        var row = body.GetProperty("readings")[0];
        Assert.Equal(0.01, row.GetProperty("apoz").GetDouble());
        if (normalized)
        {
            Assert.Equal(30, row.GetProperty("active_import_kwh").GetDouble());
            Assert.Equal(60, row.GetProperty("active_export_kwh").GetDouble());
        }
        else Assert.False(row.TryGetProperty("active_import_kwh", out _));
    }
    [Theory]
    [InlineData(30, 100, 0.0335, 100.5)] // Portal 871, 2026-10-09 00:00–00:30.
    [InlineData(12, 20, 0.01, 2.4)]
    public async Task Both_transformer_ratios_apply_to_interval_energy(double kt, double kn, double raw, double expected)
    {
        using var factory = new BatteryEmsApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SiteBalance:site-khlibzavod-5:Meters:871:Kt"] = kt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["SiteBalance:site-khlibzavod-5:Meters:871:Kn"] = kn.ToString(System.Globalization.CultureInfo.InvariantCulture)
            })));
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<ISiteConsumptionStore>().AppendAsync([
            new SiteConsumptionReading("site-khlibzavod-5", "871", "Main", DateTimeOffset.UtcNow.AddMinutes(-5),
                raw, null, null, null, "askue", 1800)], CancellationToken.None);
        var body = await client.GetFromJsonAsync<JsonElement>("/site/site-khlibzavod-5/consumption/recent");
        Assert.Equal(expected, body.GetProperty("readings")[0].GetProperty("active_import_kwh").GetDouble(), 8);
    }

    [Theory]
    [InlineData("site-ukrainska-96", "550", 10, 60)]
    [InlineData("site-zachyniaieva-113", "761", 15, 60)]
    [InlineData("site-verkhnia-1", "756", 200, 1)]
    public async Task New_sites_have_normalized_balance_and_Kyiv_day(string site, string point, double kt, double kn)
    {
        using var factory = new BatteryEmsApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"SiteBalance:{site}:Meters:{point}:Name"] = "Input",
                [$"SiteBalance:{site}:Meters:{point}:Role"] = "MainGridMeter",
                [$"SiteBalance:{site}:Meters:{point}:Kt"] = kt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [$"SiteBalance:{site}:Meters:{point}:Kn"] = kn.ToString(System.Globalization.CultureInfo.InvariantCulture)
            })));
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<ISiteConsumptionStore>().AppendAsync([
            new SiteConsumptionReading(site, point, "Input", DateTimeOffset.Parse("2026-10-08T21:30:00Z", System.Globalization.CultureInfo.InvariantCulture),
                0.1, 0.02, null, null, "askue", 1800),
            new SiteConsumptionReading("another-site", point, "Other", DateTimeOffset.Parse("2026-10-08T21:30:00Z", System.Globalization.CultureInfo.InvariantCulture),
                100, null, null, null, "askue", 1800)], CancellationToken.None);
        var balance = await client.GetFromJsonAsync<JsonElement>($"/site/{site}/balance?date=2026-10-09");
        Assert.Equal(DateTimeOffset.Parse("2026-10-08T21:00:00Z", System.Globalization.CultureInfo.InvariantCulture), balance.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(0.1 * kt * kn, balance.GetProperty("main_grid_import_kwh").GetDouble(), 8);
        Assert.Equal(1, balance.GetProperty("meter_totals")[0].GetProperty("reading_count").GetInt32());
    }
}
