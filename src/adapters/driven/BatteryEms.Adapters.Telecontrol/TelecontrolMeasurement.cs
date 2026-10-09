using System.Globalization;
using System.Text.Json;
using BatteryEms.Application.Realtime;
using BatteryEms.Application.Site;
using BatteryEms.Domain;

namespace BatteryEms.Adapters.Telecontrol;

public static class TelecontrolMeasurement
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static SiteMeasurementReading Create(ChpTelemetry reading, TelecontrolOptions options)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(options);
        var id = reading.DeviceId.ToString(CultureInfo.InvariantCulture);
        var siteId = string.IsNullOrWhiteSpace(options.SiteId) ? $"unassigned:telecontrol:{id}" : options.SiteId;
        var quality = reading.Quality.Flag switch
        {
            DataQualityState.Valid => "valid",
            DataQualityState.Substituted => "substituted",
            DataQualityState.Stale => "stale",
            _ => "source_error",
        };
        var metadata = JsonSerializer.Serialize(new
        {
            asset_id = reading.AssetId,
            received_at = reading.ReceivedAt,
            source_timestamp = reading.SourceTimestamp,
            source_timezone = options.SourceTimeZoneId,
            clock_confirmed = options.ClockConfirmed,
            reason = reading.Quality.Reason,
            parameters = reading.Parameters,
            messages = reading.Messages,
        }, JsonOptions);
        return new SiteMeasurementReading(siteId, "telecontrol", "chp", id, reading.AssetId,
            reading.Timestamp, null, "chp_power", reading.PowerKw, "kW", quality, MetadataJson: metadata).EnsureValid();
    }
}
