namespace BatteryEms.Application.Site;

public sealed class InMemorySiteMeasurementStore : ISiteMeasurementStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Key, SiteMeasurementReading> _readings = [];

    public Task AppendAsync(
        IReadOnlyList<SiteMeasurementReading> readings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readings);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            foreach (var reading in readings)
            {
                var valid = reading.EnsureValid();
                _readings[Key.From(valid)] = valid;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SiteMeasurementReading>> QueryAsync(
        SiteMeasurementQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        query = query.EnsureValid();

        var from = query.From.ToUniversalTime();
        var to = query.To.ToUniversalTime();
        lock (_gate)
        {
            var result = _readings.Values
                .Where(reading => string.Equals(reading.SiteId, query.SiteId, StringComparison.Ordinal))
                .Where(reading => reading.Timestamp.ToUniversalTime() >= from && reading.Timestamp.ToUniversalTime() < to)
                .Where(reading => query.Source is null || string.Equals(reading.Source, query.Source, StringComparison.Ordinal))
                .Where(reading => query.InstrumentType is null || string.Equals(reading.InstrumentType, query.InstrumentType, StringComparison.Ordinal))
                .Where(reading => query.InstrumentId is null || string.Equals(reading.InstrumentId, query.InstrumentId, StringComparison.Ordinal))
                .Where(reading => query.Metric is null || string.Equals(reading.Metric, query.Metric, StringComparison.Ordinal))
                .OrderBy(reading => reading.Timestamp)
                .ThenBy(reading => reading.Source, StringComparer.Ordinal)
                .ThenBy(reading => reading.InstrumentId, StringComparer.Ordinal)
                .ThenBy(reading => reading.Metric, StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult<IReadOnlyList<SiteMeasurementReading>>(result);
        }
    }

    private readonly record struct Key(
        string SiteId,
        string Source,
        string InstrumentType,
        string InstrumentId,
        DateTimeOffset Timestamp,
        TimeSpan? Interval,
        string Metric)
    {
        public static Key From(SiteMeasurementReading reading) => new(
            reading.SiteId,
            reading.Source,
            reading.InstrumentType,
            reading.InstrumentId,
            reading.Timestamp.ToUniversalTime(),
            reading.Interval,
            reading.Metric);
    }
}
