using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using BatteryEms.Application.Finance;
using BatteryEms.Application.Markets;
using Microsoft.Extensions.Configuration;

namespace BatteryEms.Api.Finance;

public sealed class SiteFinancialPlanQuery
{
    private static readonly JsonSerializerOptions PlanJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private readonly IConfiguration _configuration;
    private readonly IPriceSeriesSource _prices;
    private readonly ConcurrentDictionary<PriceSeriesRequest, CachedPrices> _cache = new();

    public SiteFinancialPlanQuery(IConfiguration configuration, IPriceSeriesSource prices)
    {
        _configuration = configuration;
        _prices = prices;
    }

    public async Task<SiteFinancialPlanResponse> FindAsync(
        string siteId, DateOnly? requestedDate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var date = requestedDate ?? today;
        var options = _configuration.GetSection("SiteFinance").Get<SiteFinancialOptions>() ?? new();
        var profile = options.Sites.SingleOrDefault(site => site.SiteId == siteId);
        if (profile is null) return new(siteId, date, "not_configured");
        if (!profile.IsReviewed(date)) return new(siteId, date, "monthly_review_required",
            Currency: profile.Currency, ParameterRevision: profile.Revision, Parameters: profile.Parameters,
            PriceCurrency: profile.PriceCurrency, ExchangeRate: profile.ExchangeRate);
        var unavailable = new SiteFinancialPlanResponse(siteId, date, "plan_unavailable",
            Currency: profile.Currency, ParameterRevision: profile.Revision, CheckedOn: profile.Review!.CheckedOn, Parameters: profile.Parameters,
            PriceCurrency: profile.PriceCurrency, ExchangeRate: profile.ExchangeRate);
        if (date > today || string.IsNullOrWhiteSpace(options.PlanDirectory)) return unavailable;
        var plan = await ReadPlanAsync(options.PlanDirectory, siteId, date, cancellationToken).ConfigureAwait(false);
        if (plan is null) return unavailable;
        var start = Midnight(date, zone);
        var end = Midnight(date.AddDays(1), zone);
        var count = (int)(end - start).TotalHours;
        ValidatePlan(plan, siteId, date, now, start, count);
        var request = new PriceSeriesRequest(profile.MarketBidArea, "day_ahead", "energy_price",
            "entso-e", start, end, TimeSpan.FromHours(1));
        var prices = await LoadPricesAsync(request, now, cancellationToken).ConfigureAwait(false);
        ValidatePrices(prices, request, profile);
        var values = prices.Values.Select(value => value * profile.ExchangeRate / 1000).ToArray();
        var result = SiteEconomics.Calculate(plan.Intervals, values, profile.Parameters!);
        if (result.UnallocatedEnergyKwh > profile.Parameters!.BalanceToleranceKwh)
        {
            return unavailable with { Status = "energy_balance_incomplete" };
        }
        return unavailable with { Status = "ready", PlanRevision = plan.Revision,
            CreatedAt = plan.CreatedAt, Economics = result };
    }

    private async Task<PriceSeries> LoadPricesAsync(
        PriceSeriesRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var entry in _cache.Where(entry => entry.Value.ExpiresAt <= now))
        {
            _cache.TryRemove(entry.Key, out _);
        }
        if (_cache.TryGetValue(request, out var cached)) return cached.Series;
        var prices = await _prices.LoadAsync(request, cancellationToken).ConfigureAwait(false);
        _cache[request] = new CachedPrices(prices, now.AddHours(1));
        return prices;
    }

    private static async Task<SiteDailyFinancialPlan?> ReadPlanAsync(
        string directory, string siteId, DateOnly date, CancellationToken cancellationToken)
    {
        if (siteId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Invalid site identity.");
        }
        var name = string.Create(CultureInfo.InvariantCulture, $"{siteId}-{date:yyyy-MM-dd}.json");
        var path = Path.Combine(Path.GetFullPath(directory), name);
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (info.Length is <= 0 or > 8 * 1024 * 1024) throw new ArgumentException("Invalid plan size.");
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<SiteDailyFinancialPlan>(content, PlanJson)
            ?? throw new ArgumentException("Invalid plan.");
    }

    private static DateTimeOffset Midnight(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static void ValidatePlan(SiteDailyFinancialPlan plan, string siteId, DateOnly date,
        DateTimeOffset now, DateTimeOffset start, int count)
    {
        if (plan.SiteId != siteId || plan.DeliveryDate != date || plan.Basis != "plan"
            || string.IsNullOrWhiteSpace(plan.Revision) || plan.CreatedAt == default || plan.CreatedAt > now
            || plan.Intervals is null || plan.Intervals.Count != count)
        {
            throw new ArgumentException("Invalid plan identity, provenance or day coverage.");
        }
        for (var index = 0; index < count; index++)
        {
            if (plan.Intervals[index] is null || plan.Intervals[index].Start != start.AddHours(index))
            {
                throw new ArgumentException("Missing, duplicate or misaligned energy interval.");
            }
        }
    }

    private static void ValidatePrices(PriceSeries prices, PriceSeriesRequest request, SiteFinancialProfile profile)
    {
        if (prices.Source != "entso-e" || prices.MarketBidArea != request.MarketBidArea
            || prices.Product != "day_ahead" || prices.PriceKind != "energy_price"
            || prices.HorizonStart != request.HorizonStart || prices.HorizonEnd != request.HorizonEnd
            || prices.TimeStep != request.TimeStep || prices.Unit != profile.PriceCurrency + "/MWh")
        {
            throw new ArgumentException("Market price provenance, horizon or currency mismatch.");
        }
    }

    private sealed record CachedPrices(PriceSeries Series, DateTimeOffset ExpiresAt);
}
