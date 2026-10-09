using System.Collections.Concurrent;
using System.Text.Json;

namespace BatteryEms.Application.Site;

public sealed class InMemorySiteConsumptionStore : ISiteConsumptionStore
{
    private readonly ConcurrentDictionary<ConsumptionKey, SiteConsumptionReading> _readings = new();

    public Task AppendAsync(
        IReadOnlyList<SiteConsumptionReading> readings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readings);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var reading in readings)
        {
            Validate(reading);
            _readings[ConsumptionKey.From(reading)] = reading with
            {
                Timestamp = reading.Timestamp.ToUniversalTime(),
            };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SiteConsumptionReading>> QueryAsync(
        SiteConsumptionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        query = query.EnsureValid();
        var from = query.From.ToUniversalTime();
        var to = query.To.ToUniversalTime();

        var result = _readings.Values
            .Where(reading => string.Equals(reading.SiteId, query.SiteId, StringComparison.Ordinal))
            .Where(reading => reading.Timestamp >= from && reading.Timestamp < to)
            .Where(reading => query.PointId is null || string.Equals(reading.PointId, query.PointId, StringComparison.Ordinal))
            .Where(reading => query.Source is null || string.Equals(reading.Source, query.Source, StringComparison.Ordinal))
            .OrderBy(reading => reading.Timestamp)
            .ThenBy(reading => reading.PointId, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult<IReadOnlyList<SiteConsumptionReading>>(result);
    }

    private static void Validate(SiteConsumptionReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentException.ThrowIfNullOrWhiteSpace(reading.SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reading.PointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reading.PointName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reading.Source);
        if (IsNotFinite(reading.Apoz)
            || IsNotFinite(reading.Aneg)
            || IsNotFinite(reading.Ppoz)
            || IsNotFinite(reading.Pneg))
        {
            throw new ArgumentException("Consumption values must be finite when present.", nameof(reading));
        }

        if (reading.IntervalSeconds is { } intervalSeconds && intervalSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reading), intervalSeconds, "Consumption interval must be positive when present.");
        }

        if (reading.MetadataJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(reading.MetadataJson);
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("MetadataJson must be a valid JSON string.", nameof(reading), ex);
            }
        }
    }

    private static bool IsNotFinite(double? value) => value is double actual && !double.IsFinite(actual);

    private sealed record ConsumptionKey(
        string SiteId,
        string Source,
        string PointId,
        DateTimeOffset Timestamp)
    {
        public static ConsumptionKey From(SiteConsumptionReading reading) => new(
            reading.SiteId,
            reading.Source,
            reading.PointId,
            reading.Timestamp.ToUniversalTime());
    }
}
