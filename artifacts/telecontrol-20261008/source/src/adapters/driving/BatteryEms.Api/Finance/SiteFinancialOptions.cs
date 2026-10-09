using BatteryEms.Application.Finance;

namespace BatteryEms.Api.Finance;

public sealed class SiteFinancialOptions
{
    public string PlanDirectory { get; set; } = string.Empty;
    public IReadOnlyList<SiteFinancialProfile> Sites { get; set; } = [];
}

public sealed class SiteFinancialProfile
{
    public string SiteId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string MarketBidArea { get; set; } = string.Empty;
    public string Currency { get; set; } = "UAH";
    public string PriceCurrency { get; set; } = "EUR";
    public double ExchangeRate { get; set; }
    public string DistributionOperator { get; set; } = string.Empty;
    public string VoltageClass { get; set; } = string.Empty;
    public SiteEconomicParameters? Parameters { get; set; }
    public MonthlyFinancialReview? Review { get; set; }

    public bool IsReviewed(DateOnly date) => Parameters is not null
        && Review is not null && !string.IsNullOrWhiteSpace(Revision)
        && !string.IsNullOrWhiteSpace(MarketBidArea)
        && Review.MarketBidArea == MarketBidArea
        && !string.IsNullOrWhiteSpace(DistributionOperator) && !string.IsNullOrWhiteSpace(VoltageClass)
        && IsCurrency(Currency) && IsCurrency(PriceCurrency)
        && double.IsFinite(ExchangeRate) && ExchangeRate > 0
        && (PriceCurrency != Currency || ExchangeRate == 1)
        && Review.Covers(date, Revision, DistributionOperator, VoltageClass,
            Parameters, Currency, PriceCurrency, ExchangeRate);

    private static bool IsCurrency(string value) => !string.IsNullOrEmpty(value) && value.Length == 3
        && value.All(character => character is >= 'A' and <= 'Z');
}

public sealed record SiteDailyFinancialPlan(
    string SiteId,
    DateOnly DeliveryDate,
    string Basis,
    string Revision,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SiteEnergyInterval> Intervals);

public sealed record SiteFinancialPlanResponse(
    string SiteId,
    DateOnly DeliveryDate,
    string Status,
    string TimeZone = "Europe/Kyiv",
    string? Currency = null,
    string? ParameterRevision = null,
    DateOnly? CheckedOn = null,
    string? PlanRevision = null,
    DateTimeOffset? CreatedAt = null,
    SiteEconomicResult? Economics = null,
    SiteEconomicParameters? Parameters = null,
    string? PriceCurrency = null,
    double? ExchangeRate = null);
