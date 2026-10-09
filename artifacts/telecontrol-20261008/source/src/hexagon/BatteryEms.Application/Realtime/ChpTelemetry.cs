using System.Collections.Concurrent;
using BatteryEms.Domain;

namespace BatteryEms.Application.Realtime;

public sealed record ChpParameter(string Name, string Unit, string? TextValue, double? NumericValue, bool Available);
public sealed record ChpMessage(string Name, int Code, int MessageType);
public sealed record ChpTelemetry(string AssetId, int DeviceId, DateTimeOffset Timestamp,
    DateTimeOffset ReceivedAt, string? SourceTimestamp, double? PowerKw, DataQuality Quality,
    IReadOnlyList<ChpParameter> Parameters, IReadOnlyList<ChpMessage> Messages);

public interface IChpTelemetryStore
{
    void Update(ChpTelemetry telemetry);
    ChpTelemetry? GetLatest(string assetId, DateTimeOffset now, TimeSpan maxAge);
}

public sealed class InMemoryChpTelemetryStore : IChpTelemetryStore
{
    private readonly ConcurrentDictionary<string, ChpTelemetry> _latest = new(StringComparer.Ordinal);

    public void Update(ChpTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        _latest[telemetry.AssetId] = telemetry;
    }

    public ChpTelemetry? GetLatest(string assetId, DateTimeOffset now, TimeSpan maxAge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAge, TimeSpan.Zero);
        if (!_latest.TryGetValue(assetId, out var value)) { return null; }
        return (now - value.Timestamp > maxAge || now - value.ReceivedAt > maxAge)
            && value.Quality.Flag is DataQualityState.Valid or DataQualityState.Substituted
            ? value with { Quality = DataQuality.Stale("telecontrol-measurement-aged") }
            : value;
    }
}
