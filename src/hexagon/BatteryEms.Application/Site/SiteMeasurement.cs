namespace BatteryEms.Application.Site;

public sealed record SiteMeasurementReading(
    string SiteId,
    string Source,
    string InstrumentType,
    string InstrumentId,
    string InstrumentName,
    DateTimeOffset Timestamp,
    TimeSpan? Interval,
    string Metric,
    double? Value,
    string Unit,
    string Quality,
    string? GroupParentId = null,
    double? Scale = null,
    string? MetadataJson = null)
{
    public SiteMeasurementReading EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(InstrumentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(InstrumentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(InstrumentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(Metric);
        ArgumentException.ThrowIfNullOrWhiteSpace(Unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(Quality);
        if (Value is null)
        {
            if (!IsStatusOnlyQuality(Quality))
            {
                throw new ArgumentOutOfRangeException(nameof(Value), Value, "Measurement value is required for numeric readings.");
            }
        }
        else if (!double.IsFinite(Value.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(Value), Value, "Measurement value must be finite.");
        }

        if (Interval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Interval), interval, "Measurement interval must be positive.");
        }

        if (Scale is { } scale && !double.IsFinite(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(Scale), scale, "Measurement scale must be finite.");
        }

        return this;
    }

    private static bool IsStatusOnlyQuality(string quality) =>
        string.Equals(quality, "missing", StringComparison.Ordinal)
        || string.Equals(quality, "source_error", StringComparison.Ordinal)
        || string.Equals(quality, "invalid", StringComparison.Ordinal);
}

public sealed record SiteMeasurementQuery(
    string SiteId,
    DateTimeOffset From,
    DateTimeOffset To,
    string? Source = null,
    string? InstrumentType = null,
    string? InstrumentId = null,
    string? Metric = null)
{
    public SiteMeasurementQuery EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        if (From >= To)
        {
            throw new ArgumentException("Measurement query From must be before To.", nameof(From));
        }

        return this;
    }
}

public interface ISiteMeasurementStore
{
    Task AppendAsync(
        IReadOnlyList<SiteMeasurementReading> readings,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SiteMeasurementReading>> QueryAsync(
        SiteMeasurementQuery query,
        CancellationToken cancellationToken);
}
