using System.Globalization;
using System.Text.Json;
using BatteryEms.Api.Finance;
using BatteryEms.Application.Finance;
using BatteryEms.Application.Markets;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class SiteFinancialPlanQueryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bess-finance-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions PlanJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public SiteFinancialPlanQueryTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(2026, 3, 29, 23)]
    [InlineData(2026, 10, 25, 25)]
    [InlineData(2026, 10, 7, 24)]
    public async Task Loads_entsoe_for_exact_Kyiv_day_and_revalues_using_configured_currency(
        int year, int month, int day, int hours)
    {
        var date = new DateOnly(year, month, day);
        var now = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero);
        await WritePlanAsync(date);
        var config = Configuration(date);
        var source = new Prices();
        var query = new SiteFinancialPlanQuery(config, source);
        var result = await query.FindAsync("site", null, now, CancellationToken.None);
        Assert.Equal("ready", result.Status);
        Assert.Equal(hours, source.LastRequest!.EnsureValid().HorizonEnd.Subtract(source.LastRequest.HorizonStart).TotalHours);
        Assert.Equal("entso-e", source.LastRequest.Source);
        // 100 EUR/MWh * 40 UAH/EUR / 1000 + 2 + 1 = 7 UAH/kWh.
        Assert.Equal(hours * 10 * 7, result.Economics!.PlannedProfit);
        await query.FindAsync("site", null, now, CancellationToken.None);
        Assert.Equal(1, source.Calls);
        config["SiteFinance:Sites:0:Parameters:DistributionPerKwh"] = "3";
        result = await query.FindAsync("site", null, now, CancellationToken.None);
        Assert.Equal("monthly_review_required", result.Status);
        Assert.Null(result.Economics);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Review_once_in_month_is_reused_but_not_carried_into_next_month()
    {
        var date = new DateOnly(2026, 10, 7);
        var config = Configuration(date);
        var query = new SiteFinancialPlanQuery(config, new Prices());
        var result = await query.FindAsync("site", null, new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), CancellationToken.None);
        Assert.Equal("monthly_review_required", result.Status);
        Assert.Null(result.Economics);
    }

    [Theory]
    [InlineData("EUR/kWh")]
    [InlineData("UAH/MWh")]
    public async Task Rejects_inconsistent_price_currency_or_energy_units(string unit)
    {
        var date = new DateOnly(2026, 10, 7);
        await WritePlanAsync(date);
        var query = new SiteFinancialPlanQuery(Configuration(date), new Prices { Unit = unit });
        await Assert.ThrowsAsync<ArgumentException>(() => query.FindAsync("site", date,
            new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), CancellationToken.None));
    }

    [Fact]
    public async Task Actual_or_partial_energy_file_cannot_be_used_as_complete_daily_plan()
    {
        var date = new DateOnly(2026, 10, 7);
        await WritePlanAsync(date, "actual");
        var query = new SiteFinancialPlanQuery(Configuration(date), new Prices());
        await Assert.ThrowsAsync<ArgumentException>(() => query.FindAsync("site", date,
            new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), CancellationToken.None));
        await WritePlanAsync(date, "plan", partial: true);
        await Assert.ThrowsAsync<ArgumentException>(() => query.FindAsync("site", date,
            new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), CancellationToken.None));
    }

    [Fact]
    public async Task Http_endpoint_returns_current_plan_in_dashboard_wire_format()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv")).DateTime);
        await WritePlanAsync(today);
        var source = new Prices();
        using var factory = new BatteryEmsApiFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddConfiguration(Configuration(today)));
            builder.ConfigureServices(services => services.AddSingleton<IPriceSeriesSource>(source));
        });
        using var client = factory.CreateClient();
        var text = await client.GetStringAsync("/site/site/financial-plan");
        using var body = JsonDocument.Parse(text);
        Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), body.RootElement.GetProperty("delivery_date").GetString());
        Assert.True(body.RootElement.GetProperty("economics").GetProperty("planned_profit").GetDouble() > 0);
        Assert.Equal(1, source.Calls);
    }

    private IConfiguration Configuration(DateOnly date)
    {
        var parameters = new SiteEconomicParameters(2, 1, 0.965, 0, 0, 0, 0, 0.01);
        var month = new DateOnly(date.Year, date.Month, 1);
        var review = new MonthlyFinancialReview(month, month, month, month.AddMonths(1).AddDays(-1),
            "v1", "operator", "2", new Uri("https://www.nerc.gov.ua/distribution"), "D",
            new Uri("https://www.nerc.gov.ua/transmission"), "T", "contract", "currency",
            parameters, "UAH", "EUR", 40, true, false, "area");
        var profile = new SiteFinancialProfile { SiteId = "site", Revision = "v1", MarketBidArea = "area",
            Currency = "UAH", PriceCurrency = "EUR", ExchangeRate = 40, DistributionOperator = "operator",
            VoltageClass = "2", Parameters = parameters, Review = review };
        var json = JsonSerializer.Serialize(new { SiteFinance = new SiteFinancialOptions
            { PlanDirectory = _directory, Sites = [profile] } });
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    private async Task WritePlanAsync(DateOnly date, string basis = "plan", bool partial = false)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var local = date.ToDateTime(TimeOnly.MinValue);
        var start = new DateTimeOffset(local, zone.GetUtcOffset(local));
        var next = local.AddDays(1);
        var end = new DateTimeOffset(next, zone.GetUtcOffset(next));
        var count = (int)(end - start).TotalHours - (partial ? 1 : 0);
        var intervals = Enumerable.Range(0, count).Select(index => new SiteEnergyInterval(
            start.AddHours(index), 0, 10, 0, 0, 0, 0, 0, 0)).ToArray();
        var plan = new SiteDailyFinancialPlan("site", date, basis, "plan-v1", start.AddHours(-1), intervals);
        await File.WriteAllTextAsync(Path.Combine(_directory, "site-" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json"),
            JsonSerializer.Serialize(plan, PlanJson));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class Prices : IPriceSeriesSource
    {
        public int Calls { get; private set; }
        public string Unit { get; init; } = "EUR/MWh";
        public PriceSeriesRequest? LastRequest { get; private set; }
        public Task<PriceSeries> LoadAsync(PriceSeriesRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            var count = (int)(request.HorizonEnd - request.HorizonStart).TotalHours;
            return Task.FromResult(new PriceSeries(request.MarketBidArea, request.Product, request.PriceKind,
                Unit, request.Source, request.HorizonStart, request.HorizonEnd, request.TimeStep,
                Enumerable.Repeat(100.0, count).ToArray()));
        }
    }
}
