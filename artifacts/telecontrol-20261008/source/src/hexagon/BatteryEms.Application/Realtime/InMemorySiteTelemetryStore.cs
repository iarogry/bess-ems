using System.Collections.Concurrent;
using System.Globalization;
using BatteryEms.Domain;

namespace BatteryEms.Application.Realtime;

public sealed class InMemorySiteTelemetryStore : ISiteTelemetryStore
{
    private readonly TimeSpan _maxAge;
    private readonly ConcurrentDictionary<string, SiteSnapshot> _byAsset = new(StringComparer.Ordinal);

    public InMemorySiteTelemetryStore(TimeSpan maxAge)
    {
        if (maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge), "Max age must be positive.");
        }

        _maxAge = maxAge;
    }

    public TimeSpan MaxAge => _maxAge;

    public void Update(SiteTelemetry telemetry, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        var quality = Plausibilize(telemetry);
        _byAsset[telemetry.AssetId] = new SiteSnapshot(telemetry, receivedAt, quality);
    }

    public SiteSnapshot? GetLatest(string assetId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        if (!_byAsset.TryGetValue(assetId, out var snapshot))
        {
            return null;
        }

        var age = now - snapshot.ReceivedAt;
        var measurementAge = now - snapshot.Telemetry.Timestamp;
        if (measurementAge > age) { age = measurementAge; }
        if (age > _maxAge && snapshot.Quality.Flag is DataQualityState.Valid or DataQualityState.Substituted)
        {
            var ageSeconds = age.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
            return snapshot with { Quality = DataQuality.Stale($"site-snapshot-aged-{ageSeconds}s") };
        }

        return snapshot;
    }

    private static DataQuality Plausibilize(SiteTelemetry telemetry)
    {
        if (!telemetry.DataQuality.IsUsableForControl)
        {
            return telemetry.DataQuality;
        }

        if (IsNotFinite(telemetry.PvPowerKw))
        {
            return DataQuality.ProtocolError("pv-power-not-finite");
        }

        if (IsNotFinite(telemetry.LoadPowerKw))
        {
            return DataQuality.ProtocolError("load-power-not-finite");
        }

        if (IsNotFinite(telemetry.GridPowerKw))
        {
            return DataQuality.ProtocolError("grid-power-not-finite");
        }

        if (IsNotFinite(telemetry.IrradianceWPerSquareMeter))
        {
            return DataQuality.ProtocolError("irradiance-not-finite");
        }

        return DataQuality.Valid;
    }

    private static bool IsNotFinite(double? value) => value is double actual && !double.IsFinite(actual);
}
