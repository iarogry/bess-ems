namespace BatteryEms.Application.Site;

public sealed record SiteConsumptionReading(
    string SiteId,
    string PointId,
    string PointName,
    DateTimeOffset Timestamp,
    double? Apoz,
    double? Aneg,
    double? Ppoz,
    double? Pneg,
    string Source,
    int? IntervalSeconds = null,
    string? MetadataJson = null);

public sealed record SiteConsumptionQuery(
    string SiteId,
    DateTimeOffset From,
    DateTimeOffset To,
    string? PointId = null,
    string? Source = null)
{
    public SiteConsumptionQuery EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        if (From >= To)
        {
            throw new ArgumentException("Consumption query From must be before To.", nameof(From));
        }

        return this;
    }
}

public interface ISiteConsumptionStore
{
    Task AppendAsync(
        IReadOnlyList<SiteConsumptionReading> readings,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SiteConsumptionReading>> QueryAsync(
        SiteConsumptionQuery query,
        CancellationToken cancellationToken);
}
