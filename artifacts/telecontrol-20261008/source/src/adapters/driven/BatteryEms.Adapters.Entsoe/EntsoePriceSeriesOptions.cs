namespace BatteryEms.Adapters.Entsoe;

public sealed class EntsoePriceSeriesOptions
{
    public Uri BaseUrl { get; set; } = new("https://web-api.tp.entsoe.eu/api");
    public string SecurityToken { get; set; } = string.Empty;
    public string DomainCode { get; set; } = string.Empty;
    public string Source { get; set; } = EntsoePriceSeriesSource.SourceId;
    public string Product { get; set; } = EntsoePriceSeriesSource.DayAheadProduct;
    public string PriceKind { get; set; } = EntsoePriceSeriesSource.EnergyPriceKind;
    public string Unit { get; set; } = EntsoePriceSeriesSource.EuroPerMwhUnit;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public EntsoePriceSeriesOptions EnsureValid()
    {
        ArgumentNullException.ThrowIfNull(BaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(SecurityToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(DomainCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(Product);
        ArgumentException.ThrowIfNullOrWhiteSpace(PriceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(Unit);
        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "RequestTimeout must be positive.");
        }

        return this;
    }
}
