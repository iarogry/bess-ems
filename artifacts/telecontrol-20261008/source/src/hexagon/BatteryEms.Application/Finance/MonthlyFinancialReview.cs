namespace BatteryEms.Application.Finance;

// Evidence is tied to one complete parameter revision and one local month.
// A failed attempt is not a successful review; it must remain due.
public sealed record MonthlyFinancialReview(
    DateOnly Month,
    DateOnly CheckedOn,
    DateOnly EffectiveFrom,
    DateOnly EffectiveThrough,
    string ParameterRevision,
    string DistributionOperator,
    string VoltageClass,
    Uri DistributionSource,
    string DistributionResolution,
    Uri TransmissionSource,
    string TransmissionResolution,
    string CoefficientEvidence,
    string CurrencyEvidence,
    SiteEconomicParameters Parameters,
    string Currency,
    string PriceCurrency,
    double ExchangeRate,
    bool Confirmed,
    bool HasConflict,
    string MarketBidArea = "")
{
    public bool Covers(DateOnly date, string revision, string distributionOperator, string voltageClass,
        SiteEconomicParameters parameters, string currency, string priceCurrency, double exchangeRate) =>
        Confirmed && !HasConflict
        && Month == new DateOnly(date.Year, date.Month, 1)
        && CheckedOn >= Month && CheckedOn <= date
        && EffectiveFrom <= date && EffectiveThrough >= date
        && ParameterRevision == revision && DistributionOperator == distributionOperator
        && VoltageClass == voltageClass
        && Parameters == parameters && Currency == currency
        && PriceCurrency == priceCurrency && ExchangeRate == exchangeRate
        && IsOfficialSource(DistributionSource) && IsOfficialSource(TransmissionSource)
        && !string.IsNullOrWhiteSpace(DistributionResolution)
        && !string.IsNullOrWhiteSpace(TransmissionResolution)
        && !string.IsNullOrWhiteSpace(CoefficientEvidence)
        && !string.IsNullOrWhiteSpace(CurrencyEvidence);

    private static bool IsOfficialSource(Uri source) =>
        source is not null && source.IsAbsoluteUri && source.Scheme == Uri.UriSchemeHttps
        && (source.Host == "www.nerc.gov.ua" || source.Host == "nerc.gov.ua");
}
