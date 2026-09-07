using BatteryEms.Domain;

namespace BatteryEms.Application.Realtime;

public sealed record SiteTelemetry(
    DateTimeOffset Timestamp,
    string AssetId,
    double? PvPowerKw,
    double? LoadPowerKw,
    double? GridPowerKw,
    double? IrradianceWPerSquareMeter,
    DataQuality DataQuality);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SiteSnapshot(
    SiteTelemetry Telemetry,
    DateTimeOffset ReceivedAt,
    DataQuality Quality);
